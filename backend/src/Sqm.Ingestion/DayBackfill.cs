using System.Globalization;

namespace Sqm.Ingestion;

/// <summary>
/// Runs one piece of per-day work over a range of days, skipping the ones already done.
/// </summary>
/// <remarks>
/// <para>
/// The loop the historical backfills share, so each of them is only its own two questions - which
/// days are done, and what doing a day means - and none of them re-implements skipping, progress
/// or failure reporting.
/// </para>
/// <para>
/// A failure on one day does not stop the run. Days are independent, so stopping at the first
/// failure would leave the operator re-running the ones that already worked to reach the ones that
/// did not; reporting the failures at the end lets them re-run just those.
/// </para>
/// </remarks>
internal static class DayBackfill
{
    /// <summary>Runs the work and returns a process exit code: 0 for success, 1 if any day failed.</summary>
    /// <param name="what">What is being rebuilt, for the output.</param>
    /// <param name="days">Every day in range.</param>
    /// <param name="done">Days already done, skipped; empty to redo every day.</param>
    /// <param name="refreshDay">The work for one day.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<int> RunAsync(
        string what,
        IReadOnlyList<DateOnly> days,
        IReadOnlySet<DateOnly> done,
        Func<DateOnly, Task> refreshDay,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(done);
        ArgumentNullException.ThrowIfNull(refreshDay);

        if (days.Count == 0)
        {
            Console.WriteLine("no days in the event log for that range");
            return 0;
        }

        var total = days.Count;
        var todo = days.Where(d => !done.Contains(d)).ToList();

        if (todo.Count == 0)
        {
            Console.WriteLine($"all {total} day(s) already built; pass --force to rebuild them");
            return 0;
        }

        if (todo.Count < total)
        {
            Console.WriteLine($"{total - todo.Count} day(s) already built, skipping them");
        }

        Console.WriteLine($"rebuilding {what} for {todo.Count} day(s), "
            + $"{todo[0]:yyyy-MM-dd} .. {todo[^1]:yyyy-MM-dd}");

        var failures = new List<string>();
        var started = DateTime.UtcNow;

        for (var i = 0; i < todo.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var day = todo[i];
            var dayStarted = DateTime.UtcNow;

            try
            {
                await refreshDay(day).ConfigureAwait(false);

                var elapsed = (DateTime.UtcNow - dayStarted).TotalSeconds;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {i + 1,3}/{todo.Count}  {day:yyyy-MM-dd}  {elapsed,6:F1}s"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"  {i + 1,3}/{todo.Count}  {day:yyyy-MM-dd}  FAILED: {ex.Message}");
                failures.Add(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }
        }

        var minutes = (DateTime.UtcNow - started).TotalMinutes;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"finished in {minutes:F1} minutes, {failures.Count} failed"));

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
        ArgumentNullException.ThrowIfNull(args);

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
