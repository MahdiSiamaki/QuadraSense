using Sqm.Application.DataImport;
using Sqm.Application.Quality;

namespace Sqm.Ingestion;

/// <summary>
/// Measures feed quality for days imported before it existed, or again after a TAC version change.
/// </summary>
/// <remarks>
/// The same call the import makes for each new day, in a loop: one implementation, so the history
/// and tomorrow's day are measured alike. Measured on the real files at 6-12 seconds a day.
/// </remarks>
internal static class FeedQualityBackfill
{
    /// <summary>Runs the backfill and returns a process exit code.</summary>
    /// <remarks>
    /// <c>--force</c> re-measures days already measured - needed after a new GSMA version is
    /// activated, since the unknown-TAC and shifted-IMEI counts are judged against the version
    /// active when a day was measured.
    /// </remarks>
    public static async Task<int> RunAsync(
        IAnalyticsIngestionStore analytics,
        IFeedQualityReader reader,
        DateOnly? from,
        DateOnly? to,
        bool force,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(analytics);
        ArgumentNullException.ThrowIfNull(reader);

        var days = await analytics.GetBusinessDatesAsync(from, to, ct).ConfigureAwait(false);

        IReadOnlySet<DateOnly> measured;
        try
        {
            measured = force
                ? new HashSet<DateOnly>()
                : (await reader.GetDaysAsync(from, to, ct).ConfigureAwait(false)).Select(d => d.Date).ToHashSet();
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"cannot read the feed-quality tables - apply the analytics migrations first: {ex.Message}");
            return 2;
        }

        return await DayBackfill.RunAsync("feed quality", days, measured, async day =>
        {
            if (!await analytics.RefreshFeedQualityForDayAsync(day, ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException("the feed-quality tables do not exist");
            }
        }, ct).ConfigureAwait(false);
    }
}
