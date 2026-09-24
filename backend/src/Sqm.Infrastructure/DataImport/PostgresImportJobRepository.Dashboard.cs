using Dapper;

namespace Sqm.Infrastructure.DataImport;

/// <summary>
/// The dashboard snapshot owed after a run of files, and the queue facts that decide it.
/// </summary>
/// <remarks>
/// See migration 011. A daily import with more files of its source queued behind it defers the
/// ~31-minute snapshot rebuild and records the debt here; the last file of the run, or the worker
/// once it has nothing to claim, rebuilds and clears it.
/// </remarks>
public sealed partial class PostgresImportJobRepository
{
    /// <summary>The statuses in which a worker is actively holding a job.</summary>
    private const string RunningStatuses =
        "('VALIDATING', 'PARSING', 'NORMALIZING', 'DEDUPLICATING', 'ENRICHING', 'IMPORTING', "
        + "'AGGREGATING', 'FINALIZING')";

    public async Task<int> CountWaitingJobsAsync(string sourceCode, long exceptJobId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        // Queued or waiting to retry: both will run again without anyone asking. A cancel request
        // on a queued job means it will not, so it does not count.
        return await connection.ExecuteScalarAsync<int>(Command("""
            SELECT COUNT(*)::int FROM imports.import_job
             WHERE source_code = @sourceCode
               AND id <> @exceptJobId
               AND status IN ('QUEUED', 'RETRYING')
               AND NOT cancel_requested
            """, new { sourceCode, exceptJobId }, ct)).ConfigureAwait(false);
    }

    public async Task<bool> IsSourceRunningAsync(string sourceCode, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<bool>(Command($"""
            SELECT EXISTS (SELECT 1 FROM imports.import_job
                            WHERE source_code = @sourceCode AND status IN {RunningStatuses})
            """, new { sourceCode }, ct)).ConfigureAwait(false);
    }

    public async Task MarkDashboardOwedAsync(
        string sourceCode, long? jobId, string reason, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        // owed_since moves forward every time. That is what lets a rebuild clear only the debt it
        // saw before it began: one recorded while it ran carries a later time and survives.
        await connection.ExecuteAsync(Command("""
            INSERT INTO imports.dashboard_refresh_owed (source_code, owed_since, last_job_id, reason)
            VALUES (@sourceCode, clock_timestamp(), @jobId, @reason)
            ON CONFLICT (source_code) DO UPDATE
               SET owed_since  = clock_timestamp(),
                   last_job_id = COALESCE(EXCLUDED.last_job_id, imports.dashboard_refresh_owed.last_job_id),
                   reason      = EXCLUDED.reason
            """, new { sourceCode, jobId, reason }, ct)).ConfigureAwait(false);
    }

    public async Task<DateTimeOffset?> GetDashboardOwedSinceAsync(string sourceCode, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<DateTimeOffset?>(Command("""
            SELECT owed_since FROM imports.dashboard_refresh_owed WHERE source_code = @sourceCode
            """, new { sourceCode }, ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ListDashboardOwedAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        return [.. await connection.QueryAsync<string>(Command(
            "SELECT source_code FROM imports.dashboard_refresh_owed ORDER BY owed_since", null, ct))
            .ConfigureAwait(false)];
    }

    public async Task<bool> SettleDashboardOwedAsync(
        string sourceCode, DateTimeOffset owedSince, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        // Compare and delete. If another file deferred while the rebuild ran, owed_since has moved
        // on, this deletes nothing, and the newer debt is rebuilt for.
        return await connection.ExecuteAsync(Command("""
            DELETE FROM imports.dashboard_refresh_owed
             WHERE source_code = @sourceCode AND owed_since = @owedSince
            """, new { sourceCode, owedSince }, ct)).ConfigureAwait(false) == 1;
    }
}
