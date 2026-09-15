using System.Globalization;
using Sqm.Application.DataImport;

namespace Sqm.Ingestion;

/// <summary>
/// Which re-ordered copy of current state is being filled.
/// </summary>
/// <param name="Table">Destination table, unqualified by database.</param>
/// <param name="Columns">Its columns, in the order the copy writes them.</param>
/// <param name="Purpose">What breaks if the copy is short, said in the operator's terms.</param>
/// <param name="Command">The command-line switch that runs this backfill.</param>
/// <param name="KeyTuple">The destination's sort key, for the "find the missing rows" hint.</param>
internal sealed record BackfillTarget(
    string Table, string Columns, string Purpose, string Command, string KeyTuple);

/// <summary>
/// Fills a re-ordered copy of <c>sqm.binding_current</c> from <c>sqm.binding_current</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two tables need this: <c>binding_by_imsi</c> (ADR-008) and <c>binding_by_imei</c> (ADR-009).
/// They exist for the same reason - a filter on a non-leading key column of the primary table
/// prunes nothing and reads all 295 million rows - and they fail in the same ways, so they share
/// one implementation rather than two that drift.
/// </para>
/// <para>
/// Run once after the migration that creates the table, and again after any rebuild of
/// <c>binding_current</c> that bypasses the materialized view - an <c>EXCHANGE TABLES</c> swap,
/// for instance, which fires no insert and therefore mirrors nothing.
/// </para>
/// <para>
/// <b>Why it is a command and not part of the migration.</b> It needs three things a migration
/// runner cannot do: pause merges for the duration, work in chunks so a failure costs one chunk
/// rather than twenty minutes, and reconcile the result against the source before claiming
/// success. The mart backfill is a command for the same reasons.
/// </para>
/// </remarks>
internal static class CurrentStateBackfill
{
    /// <summary>
    /// How the source is divided. The source is ordered by MSISDN, so each chunk is a contiguous
    /// primary-index read rather than a scan - whatever the destination is ordered by.
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

    /// <summary>The IMSI-ordered copy. See ADR-008.</summary>
    public static readonly BackfillTarget ByImsi = new(
        Table: "binding_by_imsi",
        Columns: "imsi, msisdn, imei, active, last_change_seq, last_change_date",
        Purpose: "IMSI search",
        Command: "--backfill-imsi",
        KeyTuple: "imsi, msisdn, imei");

    /// <summary>The IMEI-ordered copy, which is also the TAC-ordered one. See ADR-009.</summary>
    /// <remarks>
    /// One table serves both because a TAC is the first eight digits of an IMEI - proven on every
    /// one of the 284,341,927 well-formed rows, with zero mismatches - so a device model is a
    /// contiguous range of a table ordered by IMEI.
    /// </remarks>
    public static readonly BackfillTarget ByImei = new(
        Table: "binding_by_imei",
        Columns: "imei, msisdn, imsi, active, last_change_seq, last_change_date",
        Purpose: "device and IMEI search",
        Command: "--backfill-imei",
        KeyTuple: "imei, msisdn, imsi");

    /// <summary>Runs the backfill and returns a process exit code.</summary>
    /// <param name="analytics">The analytics store.</param>
    /// <param name="target">Which copy to fill.</param>
    /// <param name="truncate">Empty the destination first, for a full rebuild.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>0 reconciled, 1 verified and wrong, 2 could not verify.</returns>
    public static async Task<int> RunAsync(
        IAnalyticsIngestionStore analytics, BackfillTarget target, bool truncate,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(analytics);
        ArgumentNullException.ThrowIfNull(target);

        Console.WriteLine($"backfilling sqm.{target.Table} from sqm.binding_current");

        // Merges are paused throughout, and the in-flight ones are waited out. A single merge of a
        // multi-gigabyte part can hold 4 GiB on a 6 GiB node, and the OvercommitTracker then stops
        // whichever query is running - which is how the first attempt at the IMSI copy died after
        // 43.68M of 295M rows, reporting a memory limit against a query using 1.53 GiB. STOP
        // MERGES alone is not enough: it stops new merges and lets the running ones continue,
        // which is how a later run lost its ninth chunk. See 11-clickhouse-memory.md section 6.
        await MergeControl.PauseAsync(analytics, ct).ConfigureAwait(false);

        try
        {
            if (truncate)
            {
                await analytics.ExecuteMartStatementAsync(
                    $"TRUNCATE TABLE sqm.{target.Table}", 0, ct).ConfigureAwait(false);
                Console.WriteLine("  destination truncated");
            }

            var started = DateTimeOffset.UtcNow;
            var failures = 0;

            for (ulong low = 0; low < ChunkedRangeEnd; low += ChunkSize)
            {
                var high = low + ChunkSize;
                failures += await CopyAsync(
                    analytics, target,
                    $"msisdn >= {low} AND msisdn < {high}",
                    $"[{low,11} , {high,11})",
                    ct).ConfigureAwait(false);
            }

            // The tail: everything the chunk loop could not reach.
            failures += await CopyAsync(
                analytics, target,
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
            await MergeControl.ResumeAsync(analytics, ct).ConfigureAwait(false);
        }

        // The copy and the check want opposite things from the server, and this ordering is what
        // three failed runs taught. Each phase has one requirement:
        //
        //   COPY    needs merges paused. A merge of a multi-gigabyte part holds around 4 GiB and
        //           the OvercommitTracker stops whichever query is running.
        //   COMPACT needs merges RUNNING. Two count() FINAL queries hold one read buffer per
        //           active part; over 301 fresh parts that reached 4.97 GiB of genuine RSS.
        //   CHECK   needs merges paused again. At 18 parts and 1.22 GiB of RSS the check still
        //           died reporting 5.21 GiB - the tracker was full of a concurrent merge, not of
        //           anything this query had allocated.
        //
        // So the collision was never really the part count; it was whatever else was running.
        // Compaction brings the parts down, and pausing again keeps the check to itself.
        try
        {
            await MergeControl.WaitForCompactionAsync(analytics, target.Table, ct)
                .ConfigureAwait(false);
            await MergeControl.PauseAsync(analytics, ct).ConfigureAwait(false);
            return await ReconcileAsync(analytics, target, ct).ConfigureAwait(false);
        }
        finally
        {
            await MergeControl.ResumeAsync(analytics, ct).ConfigureAwait(false);
        }
    }

    private static async Task<int> CopyAsync(
        IAnalyticsIngestionStore analytics, BackfillTarget target, string predicate, string label,
        CancellationToken ct)
    {
        // FINAL on the source, because a partially merged ReplacingMergeTree still holds the
        // superseded rows. Copying those would put rows in the destination that the source does
        // not consider current - and the reconciliation below would then fail, correctly, with no
        // hint as to why.
        var sql = $"""
            INSERT INTO sqm.{target.Table}
                ({target.Columns})
            SELECT {target.Columns}
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
    /// This is the step that earns the command's existence. The first run of the IMSI backfill
    /// reported every chunk as succeeding and was short by five rows, because the chunk loop
    /// stopped at ten digits and five MSISDNs in the data have twelve, thirteen and fifteen. A
    /// job that says "done" without checking is a job that will be believed.
    /// </remarks>
    private static async Task<int> ReconcileAsync(
        IAnalyticsIngestionStore analytics, BackfillTarget target, CancellationToken ct)
    {
        long sourceRows, copyRows, sourceActive, copyActive;

        try
        {
            sourceRows = await analytics.ScalarAsync(
                "SELECT count() FROM sqm.binding_current AS b FINAL", ct).ConfigureAwait(false) ?? -1;
            copyRows = await analytics.ScalarAsync(
                $"SELECT count() FROM sqm.{target.Table} AS b FINAL", ct).ConfigureAwait(false) ?? -2;

            sourceActive = await analytics.ScalarAsync(
                "SELECT countIf(active = 1) FROM sqm.binding_current AS b FINAL", ct)
                .ConfigureAwait(false) ?? -1;
            copyActive = await analytics.ScalarAsync(
                $"SELECT countIf(active = 1) FROM sqm.{target.Table} AS b FINAL", ct)
                .ConfigureAwait(false) ?? -2;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Reported, never thrown. The copy may well have succeeded; what failed is the check,
            // and an operator who gets a stack trace cannot tell those apart. Exit 2 distinguishes
            // "could not verify" from exit 1's "verified, and wrong".
            Console.Error.WriteLine();
            Console.Error.WriteLine($"copy finished, but it could not be verified: {ex.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine("The rows are probably there. Re-run once the server is quiet:");
            Console.Error.WriteLine($"  dotnet run -- {target.Command}     (the copy is idempotent)");
            Console.Error.WriteLine("or check by hand:");
            Console.Error.WriteLine("  SELECT count() FROM sqm.binding_current AS b FINAL;");
            Console.Error.WriteLine($"  SELECT count() FROM sqm.{target.Table} AS b FINAL;");
            return 2;
        }

        var name = target.Table.PadRight(17);

        Console.WriteLine();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  binding_current   {sourceRows,14:N0} rows   {sourceActive,14:N0} active"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {name} {copyRows,14:N0} rows   {copyActive,14:N0} active"));

        var rowGap = sourceRows - copyRows;
        var activeGap = sourceActive - copyActive;

        if (rowGap == 0 && activeGap == 0)
        {
            Console.WriteLine("  difference                     0                     0");
            Console.WriteLine();
            Console.WriteLine($"reconciled. {target.Purpose} is serving complete data.");
            return 0;
        }

        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  difference        {rowGap,14:N0}        {activeGap,14:N0}"));
        Console.Error.WriteLine();
        Console.Error.WriteLine(
            $"NOT reconciled. {target.Purpose} would under-report. Find the missing rows with:");
        Console.Error.WriteLine("  SELECT msisdn, imsi, imei FROM sqm.binding_current AS b FINAL");
        Console.Error.WriteLine(
            $"  WHERE ({target.KeyTuple}) NOT IN (SELECT {target.KeyTuple} FROM sqm.{target.Table})");
        Console.Error.WriteLine("  LIMIT 20");
        return 1;
    }
}
