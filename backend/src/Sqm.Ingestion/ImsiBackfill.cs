using System.Globalization;
using Sqm.Application.DataImport;

namespace Sqm.Ingestion;

/// <summary>
/// Populates <c>sqm.binding_by_imsi</c> from <c>sqm.binding_current</c>.
/// </summary>
/// <remarks>
/// <para>
/// Run once after migration 018, and again after any rebuild of <c>binding_current</c> that
/// bypasses the materialized view - an <c>EXCHANGE TABLES</c> swap, for instance, which fires no
/// insert and therefore mirrors nothing.
/// </para>
/// <para>
/// <b>Why it is a command and not part of the migration.</b> It needs three things a migration
/// runner cannot do: pause merges for the duration, work in chunks so a failure costs one chunk
/// rather than six minutes, and reconcile the result against the source before claiming success.
/// The mart backfill is a command for the same reasons.
/// </para>
/// </remarks>
internal static class ImsiBackfill
{
    /// <summary>
    /// How the source is divided. The source is ordered by MSISDN, so each chunk is a contiguous
    /// primary-index read rather than a scan.
    /// </summary>
    private const ulong ChunkSize = 500_000_000;

    /// <summary>
    /// Where the chunks stop, and where the catch-all begins.
    /// </summary>
    /// <remarks>
    /// An Iranian MSISDN is ten digits, so the loop could stop at 10^10 - and the first version
    /// did, and lost exactly five rows. The data holds MSISDNs of 12, 13 and 15 digits: numbers
    /// beginning 971, 994 and 964, which are the UAE, Azerbaijan and Iraq. They are anomalies and
    /// they are real, and a backfill that silently drops them is worse than one that fails.
    /// A final unbounded chunk collects everything past this point.
    /// </remarks>
    private const ulong ChunkedRangeEnd = 10_000_000_000;

    /// <summary>Runs the backfill and returns a process exit code.</summary>
    public static async Task<int> RunAsync(
        IAnalyticsIngestionStore analytics, bool truncate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(analytics);

        Console.WriteLine("backfilling sqm.binding_by_imsi from sqm.binding_current");

        // Merges are paused throughout. A single merge of a multi-gigabyte part can hold 4 GiB on
        // a 6 GiB node, and the OvercommitTracker then stops whichever query is running - which is
        // how the first attempt at this died after 43.68M of 295M rows, reporting a memory limit
        // against a query using 1.53 GiB. See docs/architecture/11-clickhouse-memory.md section 6.
        await analytics.SetMergesEnabledAsync(false, ct).ConfigureAwait(false);

        try
        {
            if (truncate)
            {
                await analytics.ExecuteMartStatementAsync(
                    "TRUNCATE TABLE sqm.binding_by_imsi", 0, ct).ConfigureAwait(false);
                Console.WriteLine("  destination truncated");
            }

            var started = DateTimeOffset.UtcNow;
            var failures = 0;

            for (ulong low = 0; low < ChunkedRangeEnd; low += ChunkSize)
            {
                var high = low + ChunkSize;
                failures += await CopyAsync(
                    analytics,
                    $"msisdn >= {low} AND msisdn < {high}",
                    $"[{low,11} , {high,11})",
                    ct).ConfigureAwait(false);
            }

            // The tail: everything the chunk loop could not reach.
            failures += await CopyAsync(
                analytics,
                $"msisdn >= {ChunkedRangeEnd}",
                $"[{ChunkedRangeEnd,11} , ...      )",
                ct).ConfigureAwait(false);

            var elapsed = DateTimeOffset.UtcNow - started;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  copied in {elapsed.TotalSeconds:N0}s, {failures} chunk failure(s)"));

            if (failures > 0)
            {
                Console.Error.WriteLine("re-run to retry; the copy is idempotent by construction");
                return 1;
            }
        }
        finally
        {
            // In a finally, including on failure: leaving merges off lets parts accumulate until
            // the table is unusable, and the next person has no reason to suspect a batch job
            // turned them off.
            await analytics.SetMergesEnabledAsync(true, ct).ConfigureAwait(false);
            Console.WriteLine("  merges resumed");
        }

        return await ReconcileAsync(analytics, ct).ConfigureAwait(false);
    }

    private static async Task<int> CopyAsync(
        IAnalyticsIngestionStore analytics, string predicate, string label, CancellationToken ct)
    {
        // FINAL on the source, because a partially merged ReplacingMergeTree still holds the
        // superseded rows. Copying those would put rows in the destination that the source does
        // not consider current - and the reconciliation below would then fail, correctly, with no
        // hint as to why.
        var sql = $"""
            INSERT INTO sqm.binding_by_imsi
                (imsi, msisdn, imei, active, last_change_seq, last_change_date)
            SELECT imsi, msisdn, imei, active, last_change_seq, last_change_date
            FROM sqm.binding_current AS b FINAL
            WHERE {predicate}
            """;

        var started = DateTimeOffset.UtcNow;

        try
        {
            await analytics.ExecuteMartStatementAsync(sql, 0, ct).ConfigureAwait(false);
            var seconds = (DateTimeOffset.UtcNow - started).TotalSeconds;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  msisdn {label}  {seconds,6:N0}s"));
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"  msisdn {label}  FAILED: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Proves the copy is complete before reporting success.
    /// </summary>
    /// <remarks>
    /// This is the step that earns the command's existence. The first run of this backfill
    /// reported every chunk as succeeding and was short by five rows, because the chunk loop
    /// stopped at ten digits and five MSISDNs in the data have twelve, thirteen and fifteen. A
    /// job that says "done" without checking is a job that will be believed.
    /// </remarks>
    private static async Task<int> ReconcileAsync(
        IAnalyticsIngestionStore analytics, CancellationToken ct)
    {
        var sourceRows = await analytics.ScalarAsync(
            "SELECT count() FROM sqm.binding_current AS b FINAL", ct).ConfigureAwait(false) ?? -1;
        var copyRows = await analytics.ScalarAsync(
            "SELECT count() FROM sqm.binding_by_imsi AS b FINAL", ct).ConfigureAwait(false) ?? -2;

        var sourceActive = await analytics.ScalarAsync(
            "SELECT countIf(active = 1) FROM sqm.binding_current AS b FINAL", ct)
            .ConfigureAwait(false) ?? -1;
        var copyActive = await analytics.ScalarAsync(
            "SELECT countIf(active = 1) FROM sqm.binding_by_imsi AS b FINAL", ct)
            .ConfigureAwait(false) ?? -2;

        Console.WriteLine();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  binding_current   {sourceRows,14:N0} rows   {sourceActive,14:N0} active"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  binding_by_imsi   {copyRows,14:N0} rows   {copyActive,14:N0} active"));

        var rowGap = sourceRows - copyRows;
        var activeGap = sourceActive - copyActive;

        if (rowGap == 0 && activeGap == 0)
        {
            Console.WriteLine("  difference                     0                     0");
            Console.WriteLine();
            Console.WriteLine("reconciled. IMSI search is serving complete data.");
            return 0;
        }

        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  difference        {rowGap,14:N0}        {activeGap,14:N0}"));
        Console.Error.WriteLine();
        Console.Error.WriteLine("NOT reconciled. IMSI search would under-report. Find the missing rows with:");
        Console.Error.WriteLine("  SELECT msisdn, imsi, imei FROM sqm.binding_current AS b FINAL");
        Console.Error.WriteLine("  WHERE (imsi, msisdn, imei) NOT IN (SELECT imsi, msisdn, imei FROM sqm.binding_by_imsi)");
        Console.Error.WriteLine("  LIMIT 20");
        return 1;
    }
}
