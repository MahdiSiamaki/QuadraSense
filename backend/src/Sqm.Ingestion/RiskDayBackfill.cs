using Sqm.Application.DataImport;

namespace Sqm.Ingestion;

/// <summary>
/// Writes the risk pages' per-day SIM changes for days imported before they existed, or again.
/// </summary>
/// <remarks>
/// The same call the import makes for each new day, in a loop, so the backfilled days and tomorrow's
/// are built alike. <c>--force</c> rewrites days already written - needed after
/// <c>--refresh-quality --force</c>, because the screen reads each day's multi-number SIM list.
/// </remarks>
internal static class RiskDayBackfill
{
    /// <summary>Runs the backfill and returns a process exit code.</summary>
    public static async Task<int> RunAsync(
        IAnalyticsIngestionStore analytics, DateOnly? from, DateOnly? to, bool force, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(analytics);

        var days = await analytics.GetBusinessDatesAsync(from, to, ct).ConfigureAwait(false);
        var written = force ? new HashSet<DateOnly>() : await analytics.GetRiskDaysAsync(ct).ConfigureAwait(false);

        return await DayBackfill.RunAsync("risk SIM changes", days, written, async day =>
        {
            if (await analytics.RefreshRiskDayAsync(day, ct).ConfigureAwait(false) is null)
            {
                throw new InvalidOperationException("the risk tables do not exist; apply analytics migration 023");
            }
        }, ct).ConfigureAwait(false);
    }
}
