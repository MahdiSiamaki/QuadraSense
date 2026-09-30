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
    /// <summary>Runs the backfill and returns a process exit code.</summary>
    /// <remarks>
    /// Skips what is already built, unless asked not to. On a constrained machine a pass can lose
    /// a handful of days to memory pressure, and converging then means running again; rebuilding
    /// all 133 to redo four is fifty minutes of work for four minutes of it. <c>--force</c> is for
    /// the case that needs it: the aggregates themselves changed, so every day is recomputed.
    /// </remarks>
    public static async Task<int> RunAsync(
        IAnalyticsIngestionStore analytics,
        DateOnly? from,
        DateOnly? to,
        bool force,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(analytics);

        var days = await analytics.GetBusinessDatesAsync(from, to, ct).ConfigureAwait(false);
        var built = force
            ? []
            : (await analytics.GetBuiltMartDatesAsync(ct).ConfigureAwait(false)).ToHashSet();

        return await DayBackfill.RunAsync("day-level marts", days, built, async day =>
        {
            var sequence = await analytics.GetSequenceForDateAsync(day, ct).ConfigureAwait(false) ?? 0;
            await analytics.RefreshChangeMartsForDayAsync(day, sequence, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Reads <c>--from</c> and <c>--to</c> off the command line.</summary>
    public static (DateOnly? From, DateOnly? To) ParseRange(string[] args) => DayBackfill.ParseRange(args);
}
