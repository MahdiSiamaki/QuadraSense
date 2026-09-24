using Microsoft.Extensions.Logging;
using Sqm.Application.DataImport;

namespace Sqm.Ingestion.Processing;

/// <summary>
/// The dashboard snapshot: rebuilt once at the end of a run of files, not once per file.
/// </summary>
/// <remarks>
/// <para>
/// Every daily import used to end by rebuilding the dashboard snapshot for the delivery holding
/// the latest date - 14 full scans of <c>binding_current</c>, ~31 minutes of a ~34-minute import
/// (<c>system.query_log</c>, 31 August). After a late or corrected day that is the same delivery
/// every time, and each rebuild is made stale by the next file in the queue. While it runs, the
/// delivery is withdrawn and the dashboard falls back to the one before it: for a run of 27 files
/// the dashboard would have switched between two dates 27 times over about 20 hours.
/// </para>
/// <para>
/// So a file with more of its source queued behind it skips the rebuild and records that it is
/// owed (<c>imports.dashboard_refresh_owed</c>). The last file of the run rebuilds it. Until then
/// the dashboard shows the figures from before the run, and the Data freshness card says so. The
/// data itself - events, current state, day-level charts - is current after every file; only the
/// snapshot waits.
/// </para>
/// <para>
/// A run can stop part way: a file fails and blocks the ones after it, or they are cancelled.
/// Then no file would be last, so the worker settles the debt itself as soon as it has nothing it
/// can claim (<see cref="RunAsync"/>). The dashboard cannot be left behind its data.
/// </para>
/// </remarks>
internal sealed partial class DashboardSnapshot(
    IAnalyticsIngestionStore analytics,
    IImportJobRepository repository,
    MartRefresh martRefresh,
    ILogger<DashboardSnapshot> logger) : IIdleTask
{
    [LoggerMessage(EventId = 3810, Level = LogLevel.Information,
        Message = "Dashboard snapshot for {Source} was owed with nothing queued; rebuilding it")]
    private partial void LogSettling(string source);

    [LoggerMessage(EventId = 3811, Level = LogLevel.Warning,
        Message = "Dashboard snapshot rebuild for {Source} did not finish; it stays owed")]
    private partial void LogSettleFailed(string source, Exception exception);

    /// <summary>
    /// After a daily file has landed: rebuild the snapshot, or defer it to a file still queued.
    /// </summary>
    public async Task AfterDayAsync(ClaimedJob job, IImportContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        var waiting = await repository.CountWaitingJobsAsync(job.SourceCode, job.JobId, ct)
            .ConfigureAwait(false);

        if (waiting > 0)
        {
            await repository.MarkDashboardOwedAsync(
                job.SourceCode, job.JobId, $"deferred by job {job.JobId}", ct).ConfigureAwait(false);

            await context.NoteAsync(
                "info",
                $"Dashboard figures not rebuilt yet: {waiting:N0} more {job.SourceCode} file(s) are "
                + "queued behind this one, and each would rebuild the same snapshot again. The last "
                + "of them rebuilds it once. This day's data is loaded and current now.",
                new { waiting }, ct).ConfigureAwait(false);
            return;
        }

        await RebuildAsync(
            job.SourceCode,
            message => context.NoteAsync("info", message, null, ct),
            message => context.NoteAsync("warning", message, null, ct),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Settles an owed snapshot once nothing is left to claim. Called by the worker when idle.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        foreach (var source in await repository.ListDashboardOwedAsync(ct).ConfigureAwait(false))
        {
            // A job of this source running in another worker will rebuild, or defer again, when it
            // lands. Rebuilding underneath its fold would only publish a half-folded day.
            if (await repository.IsSourceRunningAsync(source, ct).ConfigureAwait(false))
            {
                continue;
            }

            LogSettling(source);

            try
            {
                await RebuildAsync(source, null, null, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Stays owed, and is tried again the next time the worker is idle.
                LogSettleFailed(source, ex);
            }
        }
    }

    private async Task RebuildAsync(
        string source,
        Func<string, Task>? onInfo,
        Func<string, Task>? onWarning,
        CancellationToken ct)
    {
        // Read before the rebuild starts. A file that defers while it runs moves this forward,
        // so settling below cannot clear a debt the rebuild never covered.
        var owedSince = await repository.GetDashboardOwedSinceAsync(source, ct).ConfigureAwait(false);

        var sequence = await MartRefresh.LatestDeliveryAsync(analytics, ct).ConfigureAwait(false);

        if (onInfo is not null)
        {
            await onInfo($"Rebuilding the dashboard marts for delivery {sequence}").ConfigureAwait(false);
        }

        var failures = await martRefresh.RunAsync(sequence, onWarning, ct).ConfigureAwait(false);

        if (failures > 0)
        {
            // Not a failed import: the day's data is in and correct, only a derived view is stale.
            // Owed rather than forgotten, so the next idle moment tries again.
            await repository.MarkDashboardOwedAsync(
                source, null, $"{failures} mart statement(s) failed", ct).ConfigureAwait(false);

            if (onWarning is not null)
            {
                await onWarning(
                    $"{failures} mart statement(s) failed. The day's data is imported and correct; "
                    + "the dashboard figures stay owed and are rebuilt when the worker is next idle.")
                    .ConfigureAwait(false);
            }

            return;
        }

        if (owedSince is { } since)
        {
            await repository.SettleDashboardOwedAsync(source, since, ct).ConfigureAwait(false);
        }
    }
}
