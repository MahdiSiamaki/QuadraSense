using System.Reflection;
using Microsoft.Extensions.Logging;
using Sqm.Application.DataImport;
using Sqm.Application.Sql;

namespace Sqm.Ingestion.Processing;

/// <summary>
/// Rebuilds the dashboard marts for one delivery sequence.
/// </summary>
/// <remarks>
/// <para>
/// The change marts are day-partitioned and rebuilt per day by the importer. These are the other
/// half: the KPI counters, the device rollup, the capability and dimension marts - a snapshot of
/// current state per delivery, partitioned by <c>seq</c>.
/// </para>
/// <para>
/// They matter because an import that is not followed by this leaves the event log holding today
/// and every headline figure showing yesterday, with nothing on screen to say so. That is the
/// most confusing state the system can be in, and it is the reason this runs as part of the
/// import rather than as a job someone remembers to schedule.
/// </para>
/// <para>
/// The SQL is embedded from <c>db/analytics/jobs/refresh_marts.sql</c> rather than duplicated in
/// C#. It stays reviewable as SQL and runnable by hand, and the worker cannot drift from what
/// that file says.
/// </para>
/// </remarks>
internal sealed partial class MartRefresh(
    IAnalyticsIngestionStore analytics,
    ILogger<MartRefresh> logger)
{
    [LoggerMessage(EventId = 3800, Level = LogLevel.Information,
        Message = "Mart refresh for seq {Sequence}: {Completed} of {Total} statements in {Seconds}s")]
    private partial void LogFinished(int sequence, int completed, int total, long seconds);

    [LoggerMessage(EventId = 3801, Level = LogLevel.Warning,
        Message = "Mart refresh statement failed: {Label}")]
    private partial void LogStatementFailed(string label, Exception exception);

    [LoggerMessage(EventId = 3802, Level = LogLevel.Warning,
        Message = "Dashboard marts for delivery {Sequence} skipped: delivery {Latest} holds the "
                  + "latest day, so current state is no longer delivery {Sequence}'s state")]
    private partial void LogSkippedOutOfOrder(int sequence, int latest);

    private const string ResourceName = "Sqm.Ingestion.refresh_marts.sql";

    /// <summary>How many times to run the whole script before giving up.</summary>
    /// <remarks>
    /// <para>
    /// The script is idempotent by construction: every mart drops its own partition before
    /// inserting into it, which is what makes re-running the whole thing safe rather than
    /// hopeful. That property was put there to stop a re-run doubling the counts - an
    /// insert-only version once doubled agg_device_daily to 251,879,046 against a real
    /// 125,939,523 - and it turns out to buy this as well.
    /// </para>
    /// <para>
    /// It is needed because statements run back to back and ClickHouse releases memory lazily:
    /// each aggregate runs comfortably under its 1.2 GiB cap in isolation, and a run of them
    /// together can push the server total to its ceiling even though no single one is close.
    /// Retrying the pass lets the stragglers through once the previous ones have let go.
    /// </para>
    /// </remarks>
    private const int MaxPasses = 3;

    /// <summary>A breath between statements; see <see cref="RunPassAsync"/>.</summary>
    /// <remarks>Settable only so that tests of the code around the refresh need not wait.</remarks>
    internal TimeSpan BetweenStatements { get; init; } = TimeSpan.FromMilliseconds(750);

    /// <summary>The wait before retrying a pass that had failures.</summary>
    internal TimeSpan BetweenPasses { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Runs the refresh, retrying the whole script until nothing fails.</summary>
    /// <returns>How many statements still failed after the last pass.</returns>
    /// <summary>The delivery whose day is the latest in the event log.</summary>
    /// <remarks>
    /// By date, not by sequence number. A missing day imported late takes the next number, so
    /// "highest sequence" would name 12 May as the newest delivery while 31 August is loaded -
    /// the dashboard would then be rebuilt for, and dated as, a day three months old. Falls back
    /// to the highest sequence while no daily file has landed, which is the initial dump.
    /// </remarks>
    public static async Task<int> LatestDeliveryAsync(
        IAnalyticsIngestionStore analytics, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(analytics);

        var days = await analytics.GetBusinessDatesAsync(null, null, ct).ConfigureAwait(false);
        var latest = days.Count > 0
            ? await analytics.GetSequenceForDateAsync(days[^1], ct).ConfigureAwait(false)
            : null;

        return latest ?? await analytics.GetMaxSequenceAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> RunAsync(
        int sequence, Func<string, Task>? onProgress, CancellationToken ct)
    {
        var failures = 0;
        var statements = SqlScript.Split(LoadScript()).Count;

        // ------------------------------------------------------------------ the ordering guard
        //
        // Every mart here is "the state of the network at delivery N", and every one of them is
        // built by aggregating binding_current AS IT IS NOW. Those are the same thing only while
        // no LATER delivery has been folded. Fold day N+1 first and this script will happily
        // write day N+1's state into day N's partition, label it N, and publish it.
        //
        // That is not hypothetical. Days 06-16 and 06-17 were each interrupted and retried; by
        // the time they ran again, 06-18 was folded. Both rebuilt their own partitions from a
        // binding_current that already contained 06-18, and all three deliveries ended up holding
        // the identical figure of 114,230,645 active bindings - while the independent per-day
        // change marts said 06-16 GAINED 167,111 bindings and its snapshot claimed a loss.
        //
        // The state as of delivery N cannot be recovered afterwards: binding_current keeps one
        // row per binding carrying only its LATEST change, so the earlier state is not there to
        // read. So this refuses rather than guesses. The delivery's own data is already loaded
        // and folded - correctly, because the fold is order-independent - and the next delivery
        // to run in order will publish a correct snapshot.
        var latest = await LatestDeliveryAsync(analytics, ct).ConfigureAwait(false);

        if (latest != sequence)
        {
            var message =
                $"Skipping the dashboard marts for delivery {sequence}: delivery {latest} holds "
                + "the latest day, so current state is no longer this delivery's state. The "
                + "events for this day are loaded and correct; only the per-delivery snapshot is "
                + "unavailable, and it cannot be reconstructed after the fact.";

            LogSkippedOutOfOrder(sequence, latest);

            if (onProgress is not null)
            {
                await onProgress(message).ConfigureAwait(false);
            }

            // Not published. A snapshot that cannot be built must not be claimed as ready - the
            // dashboard reads max(seq) from mart_ready, and an entry here is what makes a
            // delivery believed.
            return 0;
        }

        // Withdraw the delivery before touching it. From here until the refresh finishes, the
        // dashboard reads the previous complete delivery rather than a mart that is being
        // dropped and rebuilt underneath it.
        await analytics.SetMartsReadyAsync(sequence, false, 0, ct).ConfigureAwait(false);

        for (var pass = 1; pass <= MaxPasses; pass++)
        {
            failures = await RunPassAsync(sequence, pass, onProgress, ct).ConfigureAwait(false);

            if (failures == 0)
            {
                // Checked before it is published, not assumed. Every statement dropping its own
                // partition first makes the script idempotent - but only against itself. A write
                // from somewhere else that lands after the drop is simply added to: on 2026-09-30
                // the laptop slept mid-rebuild, the job was declared lost, the idle worker began a
                // fresh rebuild, and the lost job's INSERT - still running inside ClickHouse -
                // finished two minutes after the fresh one had dropped the partition. Delivery 216
                // held every device row twice: 244,507,534 active bindings against 122,253,767.
                var duplicated = await FindDuplicatesAsync(sequence, ct).ConfigureAwait(false);

                if (duplicated.Count > 0)
                {
                    failures = duplicated.Count;

                    if (onProgress is not null)
                    {
                        await onProgress(
                            $"Not published: {string.Join(", ", duplicated)} - a write from outside "
                            + "this rebuild landed in the delivery. Rebuilding it.").ConfigureAwait(false);
                    }
                }
            }

            if (failures == 0)
            {
                // Published only now. Completion is a fact the refresh records, not something
                // inferred from rows existing.
                await analytics.SetMartsReadyAsync(sequence, true, statements, ct)
                    .ConfigureAwait(false);
                return 0;
            }

            if (pass < MaxPasses)
            {
                if (onProgress is not null)
                {
                    await onProgress(
                        $"{failures} statement(s) failed on pass {pass}; retrying the whole script "
                        + "- it drops each partition before rebuilding it, so a re-run is safe")
                        .ConfigureAwait(false);
                }

                await Task.Delay(BetweenPasses, ct).ConfigureAwait(false);
            }
        }

        return failures;
    }

    /// <summary>
    /// Each mart and the key every one of its rows is unique on, within one delivery.
    /// </summary>
    /// <remarks>
    /// Taken from each table's sorting key. One refresh writes each key once, so more rows than
    /// keys means a second writer. The marts are Summing- and ReplacingMergeTrees and are read
    /// without FINAL, so a duplicate row is not a cosmetic problem: it is a doubled figure.
    /// </remarks>
    private static readonly (string Table, string Key)[] UniqueWithinDelivery =
    [
        ("agg_device_daily", "tac, active"),
        ("agg_device_model", "tac"),
        ("agg_kpi_daily", "seq"),
        ("agg_device_class_daily", "measure, device_class"),
        ("agg_capability_daily", "measure, capability"),
        ("agg_dimension_daily", "dimension, dim_value"),
    ];

    /// <summary>The marts holding more rows than keys for this delivery, if any.</summary>
    private async Task<IReadOnlyList<string>> FindDuplicatesAsync(int sequence, CancellationToken ct)
    {
        var duplicated = new List<string>();

        foreach (var (table, key) in UniqueWithinDelivery)
        {
            var extra = await analytics.ScalarAsync(
                $"SELECT count() - uniqExact({key}) FROM sqm.{table} WHERE seq = {sequence}", ct)
                .ConfigureAwait(false) ?? 0;

            if (extra > 0)
            {
                duplicated.Add($"{table} has {extra:N0} duplicate row(s)");
            }
        }

        // Not per delivery - one table, truncated and refilled by every refresh - so it is open to
        // the same second writer.
        var capability = await analytics.ScalarAsync(
            "SELECT count() - uniqExact(tac) FROM sqm.tac_capability", ct).ConfigureAwait(false) ?? 0;

        if (capability > 0)
        {
            duplicated.Add($"tac_capability has {capability:N0} duplicate row(s)");
        }

        return duplicated;
    }

    private async Task<int> RunPassAsync(
        int sequence, int pass, Func<string, Task>? onProgress, CancellationToken ct)
    {
        var statements = SqlScript.Split(LoadScript());
        var started = DateTime.UtcNow;
        var failures = 0;

        for (var i = 0; i < statements.Count; i++)
        {
            // A breath between statements. ClickHouse frees a query's memory after it reports
            // completion, and starting the next aggregate the same millisecond means competing
            // with the previous one's tail.
            if (i > 0)
            {
                await Task.Delay(BetweenStatements, ct).ConfigureAwait(false);
            }

            var label = SqlScript.Label(statements[i], i + 1);

            try
            {
                await analytics.ExecuteMartStatementAsync(statements[i], sequence, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One statement failing does not invalidate the ones that succeeded, and the
                // marts are independent of each other. Reporting the failure and continuing
                // leaves the operator with a partly-refreshed set and a precise list of what to
                // re-run, rather than nothing and a stack trace.
                failures++;
                LogStatementFailed(label, ex);

                if (onProgress is not null)
                {
                    await onProgress($"Mart statement failed: {label} - {ex.Message}")
                        .ConfigureAwait(false);
                }
            }
        }

        var seconds = (long)(DateTime.UtcNow - started).TotalSeconds;
        LogFinished(sequence, statements.Count - failures, statements.Count, seconds);
        _ = pass;

        return failures;
    }

    private static string LoadScript()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"the embedded mart refresh script '{ResourceName}' is missing from the build");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
