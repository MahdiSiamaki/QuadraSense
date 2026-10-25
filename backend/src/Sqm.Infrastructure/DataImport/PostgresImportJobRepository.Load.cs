using Dapper;
using Sqm.Application.DataImport;

namespace Sqm.Infrastructure.DataImport;

/// <summary>Loads that did not come through the queue, recorded as the imports they were.</summary>
public sealed partial class PostgresImportJobRepository
{
    public async Task<RecordedLoad> RecordCompletedLoadAsync(
        string sourceCode,
        long fileId,
        long rows,
        DateTimeOffset? startedAt,
        DateTimeOffset? finishedAt,
        string createdBy,
        string message,
        CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        // The file's row is locked first, so two runs recording the same file cannot both find no
        // job and both create one.
        await connection.ExecuteAsync(Command(
            "SELECT id FROM imports.import_file WHERE id = @file FOR UPDATE",
            new { file = fileId }, ct, transaction)).ConfigureAwait(false);

        var existing = await connection.ExecuteScalarAsync<long?>(Command(
            "SELECT id FROM imports.import_job WHERE file_id = @file ORDER BY created_at LIMIT 1",
            new { file = fileId }, ct, transaction)).ConfigureAwait(false);

        if (existing is { } kept)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return new RecordedLoad(kept, Created: false);
        }

        // Every counter is the one figure there is: the rows the store holds. Rejections and
        // duplicates are a daily file's vocabulary; a load is reconciled as a whole or undone.
        // Effective with no business date: the unique index on (source, business_date) treats
        // NULLs as distinct, so this can never displace a day's import.
        const string Sql = """
            INSERT INTO imports.import_job
                (source_code, file_id, status, business_date, revision, is_effective, created_by,
                 started_at, finished_at, rows_input, rows_valid, rows_inserted, rows_committed)
            VALUES (@source, @file, 'COMPLETED', NULL, 1, true, @by,
                    @started, @finished, @rows, @rows, @rows, @rows)
            RETURNING id
            """;

        var jobId = await connection.ExecuteScalarAsync<long>(Command(Sql, new
        {
            source = sourceCode,
            file = fileId,
            by = createdBy,
            started = startedAt?.ToUniversalTime(),
            finished = finishedAt?.ToUniversalTime(),
            rows,
        }, ct, transaction)).ConfigureAwait(false);

        await AppendEventCoreAsync(
            connection, transaction, jobId, "info", null, message,
            new { recorded = true, rows }, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new RecordedLoad(jobId, Created: true);
    }
}
