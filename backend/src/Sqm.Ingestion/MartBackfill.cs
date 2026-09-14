using System.Globalization;
using Sqm.Application.DataImport;

namespace Sqm.Ingestion;

/// <summary>
/// Rebuilds the day-level marts for a range of days, one day at a time.
/// </summary>
/// <remarks>
/// <para>
/// This exists so the historical backfill and the daily import cannot disagree. Both call
/// <see cref="IAnalyticsIngestionStore.RefreshChangeMartsForDayAsync"/>; this one just calls it
/// in a loop. A separate batch SQL script would be a second implementation of the same
/// aggregates, and the two would drift the first time one of them was corrected.
/// </para>
/// <para>
/// One day at a time is not a convenience either. The churn aggregates group by
/// <c>(data_date, msisdn)</c>, which across 133 days is hundreds of millions of groups and fits
/// nowhere; for one day it is a few million and fits comfortably. The answer for a day depends
/// only on that day, so there is nothing to lose by computing it that way.
/// </para>
/// </remarks>
internal static class MartBackfill
{
    /// <summary>
    /// Runs the backfill and returns a process exit code.
    /// </summary>
    /// <remarks>
    /// A failure on one day does not stop the run. Days are independent, so stopping at the
    /// first failure would leave the operator re-running the ones that already worked to reach
    /// the ones that did not; reporting the failures at the end lets them re-run just those.
    /// </remarks>
    public static async Task<int> RunAsync(
        IAnalyticsIngestionStore analytics,
        DateOnly? from,
        DateOnly? to,
        CancellationToken ct)
    {
        var days = await analytics.GetBusinessDatesAsync(from, to, ct).ConfigureAwait(false);

        if (days.Count == 0)
        {
            Console.WriteLine("no days in the event log for that range");
            return 0;
        }

        Console.WriteLine($"rebuilding day-level marts for {days.Count} day(s), "
            + $"{days[0]:yyyy-MM-dd} .. {days[^1]:yyyy-MM-dd}");

        var failures = new List<string>();
        var started = DateTime.UtcNow;

        for (var i = 0; i < days.Count; i++)
        {
            var day = days[i];
            var sequence = await analytics.GetSequenceForDateAsync(day, ct).ConfigureAwait(false) ?? 0;
            var dayStarted = DateTime.UtcNow;

            try
            {
                await analytics.RefreshChangeMartsForDayAsync(day, sequence, ct).ConfigureAwait(false);

                var elapsed = (DateTime.UtcNow - dayStarted).TotalSeconds;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {i + 1,3}/{days.Count}  {day:yyyy-MM-dd}  {elapsed,6:F1}s"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"  {i + 1,3}/{days.Count}  {day:yyyy-MM-dd}  FAILED: {ex.Message}");
                failures.Add(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }
        }

        var total = (DateTime.UtcNow - started).TotalMinutes;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"finished in {total:F1} minutes, {failures.Count} failed"));

        if (failures.Count > 0)
        {
            Console.WriteLine("re-run just these: --from " + failures[0] + " --to " + failures[^1]);
            return 1;
        }

        return 0;
    }

    /// <summary>Reads <c>--from</c> and <c>--to</c> off the command line.</summary>
    public static (DateOnly? From, DateOnly? To) ParseRange(string[] args)
    {
        DateOnly? from = null;
        DateOnly? to = null;

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--from" && TryDate(args[i + 1], out var f))
            {
                from = f;
            }
            else if (args[i] == "--to" && TryDate(args[i + 1], out var t))
            {
                to = t;
            }
        }

        return (from, to);
    }

    private static bool TryDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(
            value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
