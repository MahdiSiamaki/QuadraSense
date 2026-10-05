using Sqm.Application.DataImport;

namespace Sqm.Ingestion;

/// <summary>
/// Writes the per-model day counts for days imported before they existed, and the initial dump's
/// models, once.
/// </summary>
/// <remarks>
/// The same call the import makes for each new day, in a loop. <c>--force</c> rewrites days already
/// written, and the dump.
/// </remarks>
internal static class TacDayBackfill
{
    /// <summary>Runs the backfill and returns a process exit code.</summary>
    public static async Task<int> RunAsync(
        IAnalyticsIngestionStore analytics, DateOnly? from, DateOnly? to, bool force, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(analytics);

        var written = force ? new HashSet<DateOnly>() : await analytics.GetTacDaysAsync(ct).ConfigureAwait(false);

        if (!written.Contains(DateOnly.FromDateTime(DateTime.UnixEpoch)))
        {
            Console.WriteLine("writing the initial dump's models");
            if (await analytics.RefreshTacDumpAsync(ct).ConfigureAwait(false) is not { } dump)
            {
                Console.Error.WriteLine("the model table does not exist; apply analytics migration 025");
                return 1;
            }

            Console.WriteLine($"  {dump.Models:N0} models, {dump.Imeis:N0} handsets in the dump");
        }

        var days = await analytics.GetBusinessDatesAsync(from, to, ct).ConfigureAwait(false);
        return await DayBackfill.RunAsync("per-model day counts", days, written, async day =>
        {
            if (await analytics.RefreshTacDayAsync(day, ct).ConfigureAwait(false) is null)
            {
                throw new InvalidOperationException("the model table does not exist; apply analytics migration 025");
            }
        }, ct).ConfigureAwait(false);
    }
}
