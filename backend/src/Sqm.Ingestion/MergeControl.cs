using System.Globalization;
using Sqm.Application.DataImport;

namespace Sqm.Ingestion;

/// <summary>
/// Pauses background merges for a batch job, and waits for the ones already running to finish.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the wait exists.</b> <c>SYSTEM STOP MERGES</c> stops new merges being scheduled. It does
/// not stop the ones already in flight, and on this node a single merge of a multi-gigabyte part
/// holds around 4 GiB - see <c>docs/architecture/11-clickhouse-memory.md</c> section 6. A job that
/// pauses merges and starts work immediately is therefore still racing whatever was already
/// running, and loses.
/// </para>
/// <para>
/// That is not hypothetical. A backfill run that had correctly paused merges failed its ninth
/// chunk with "would use 5.21 GiB, current RSS 2.35 GiB" - a 2.9 GiB gap between the tracker and
/// the process, which is a merge holding memory the query is charged for. The previous run had
/// left 295 fresh parts and resumed merges on the way out; those merges were still going when the
/// next run began.
/// </para>
/// <para>
/// So pausing is two steps, and only the second one makes it true.
/// </para>
/// </remarks>
internal static class MergeControl
{
    /// <summary>How long to wait for in-flight merges before giving up and proceeding anyway.</summary>
    /// <remarks>
    /// A ceiling, not a promise. Proceeding while a merge is still running is what happened before
    /// this class existed and is survivable - the job reports the failed chunk and the operator
    /// re-runs. Blocking forever is not survivable, because the merge of a 25 GiB table can take
    /// longer than anybody is willing to watch.
    /// </remarks>
    private static readonly TimeSpan MaxDrainWait = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>How long to wait for a table to compact before verifying it.</summary>
    /// <remarks>
    /// Longer than the merge drain, because this is merging gigabytes rather than finishing one
    /// merge already in progress. Still a ceiling: past it the check runs and may fail, which is
    /// reported rather than thrown.
    /// </remarks>
    private static readonly TimeSpan MaxCompactionWait = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan CompactionPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Part count at which a <c>FINAL</c> query is affordable on this node.
    /// </summary>
    /// <remarks>
    /// Measured: <c>count() FINAL</c> over 295 million rows succeeded comfortably at 4, 9 and 10
    /// parts and exhausted a 5.20 GiB ceiling at 301. Thirty rather than a tighter number because
    /// the part count turned out not to be the thing that breaks it - a run at 18 parts failed
    /// with 1.22 GiB of RSS, because a concurrent merge owned the tracker. The caller pauses
    /// merges for the check; this wait only has to get the parts into a sane range.
    /// </remarks>
    private const int AffordableParts = 30;

    private const string MergesSql = "SELECT count() FROM system.merges";

    /// <summary>
    /// Reads a counter from the server, returning <see langword="null"/> if it cannot be read.
    /// </summary>
    /// <remarks>
    /// A monitoring loop must not be able to kill the job it is monitoring. These polls run at the
    /// exact moments the server is under most pressure - straight after a bulk write, while
    /// gigabytes are being merged - and on this node that is when it is most likely to stop
    /// answering. An earlier version let the exception escape and the command died with an HTTP
    /// stack trace after a copy that had completely succeeded.
    /// </remarks>
    private static async Task<int?> TryCountAsync(
        IAnalyticsIngestionStore analytics, string sql, CancellationToken ct)
    {
        try
        {
            return (int)(await analytics.ScalarAsync(sql, ct).ConfigureAwait(false) ?? 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Stops new merges and waits for the running ones to drain.</summary>
    /// <returns>How many merges were still running when the wait gave up. Zero is the good case.</returns>
    public static async Task<int> PauseAsync(IAnalyticsIngestionStore analytics, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(analytics);

        await analytics.SetMergesEnabledAsync(false, ct).ConfigureAwait(false);

        var deadline = DateTimeOffset.UtcNow + MaxDrainWait;
        var announced = false;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var running = await TryCountAsync(analytics, MergesSql, ct).ConfigureAwait(false);

            if (running is null)
            {
                // Unreadable is not the same as zero. Proceeding would start a bulk write against
                // a server that just stopped answering, which is the worst possible moment.
                Console.Error.WriteLine("  cannot read system.merges; is the server up?");
                return -1;
            }

            if (running == 0)
            {
                if (announced)
                {
                    Console.WriteLine("  merges drained");
                }

                return 0;
            }

            if (!announced)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  merges paused; waiting for {running} in flight to finish"));
                announced = true;
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }

        var remaining = await TryCountAsync(analytics, MergesSql, ct).ConfigureAwait(false) ?? -1;

        var minutes = MaxDrainWait.TotalMinutes.ToString("N0", CultureInfo.InvariantCulture);
        Console.Error.WriteLine(
            $"  {remaining} merge(s) still running after {minutes} minutes; proceeding anyway - "
            + "a chunk may fail on memory and can be re-run");

        return remaining;
    }

    /// <summary>
    /// Waits until a table has finished compacting, so a <c>FINAL</c> query over it is affordable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>FINAL</c> holds one read buffer per active part. Straight after a bulk copy that is
    /// hundreds of parts, and a <c>count() FINAL</c> over 295 million rows in 301 parts reached
    /// 4.97 GiB of real RSS against a 5.20 GiB ceiling - with merges paused, so no merge was
    /// involved. The same query over the same data in ten parts costs a fraction of that.
    /// </para>
    /// <para>
    /// So a verification step that runs immediately after a bulk write is measuring the server's
    /// worst moment. This waits for the parts to come down first.
    /// </para>
    /// </remarks>
    /// <returns>Active parts when the wait ended.</returns>
    public static async Task<int> WaitForCompactionAsync(
        IAnalyticsIngestionStore analytics, string table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(analytics);

        var partsSql =
            $"SELECT count() FROM system.parts WHERE database = 'sqm' AND table = '{table}' AND active";

        var deadline = DateTimeOffset.UtcNow + MaxCompactionWait;
        var announced = false;
        var parts = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var readParts = await TryCountAsync(analytics, partsSql, ct).ConfigureAwait(false);
            var merging = await TryCountAsync(analytics, MergesSql, ct).ConfigureAwait(false);

            if (readParts is null || merging is null)
            {
                // The server has stopped answering - on this node, most likely killed by the
                // kernel while merging what the copy just wrote. Keep waiting rather than
                // failing: it usually comes back, and giving up here would report a problem with
                // the copy when the copy was fine.
                Console.Error.WriteLine("  server not answering; still waiting");
                await Task.Delay(CompactionPollInterval, ct).ConfigureAwait(false);
                continue;
            }

            parts = readParts.Value;

            // Not "one part" - that would wait for a full compaction nobody needs. Few enough
            // that FINAL is cheap, and nothing still merging, is the condition that matters.
            if (merging == 0 && parts <= AffordableParts)
            {
                if (announced)
                {
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"  compacted to {parts} part(s)"));
                }

                return parts;
            }

            if (!announced)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  waiting for {table} to compact ({parts} parts) before verifying"));
                announced = true;
            }

            await Task.Delay(CompactionPollInterval, ct).ConfigureAwait(false);
        }

        Console.Error.WriteLine(
            $"  {table} still has {parts} parts; verifying anyway, which may run out of memory");

        return parts;
    }

    /// <summary>Lets merges run again.</summary>
    /// <remarks>
    /// Always called from a <c>finally</c>. Leaving merges off lets parts accumulate until the
    /// table is unusable, and the next person has no reason to suspect a batch job turned them off.
    /// </remarks>
    public static async Task ResumeAsync(IAnalyticsIngestionStore analytics, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(analytics);

        await analytics.SetMergesEnabledAsync(true, ct).ConfigureAwait(false);
        Console.WriteLine("  merges resumed");
    }
}
