using System.Globalization;
using System.Text;
using Dapper;
using Sqm.Application.DataImport;

namespace Sqm.Infrastructure.DataImport;

/// <summary>Query side of the import job repository.</summary>
public sealed partial class PostgresImportJobRepository
{
    /// <summary>
    /// Columns shared by the history list and the detail page, so the two can never disagree
    /// about what a field means.
    /// </summary>
    private const string JobSummaryColumns = """
                   j.id                 AS JobId,
                   j.source_code        AS SourceCode,
                   f.original_file_name AS OriginalFileName,
                   f.file_bytes         AS FileBytes,
                   j.status::text       AS StatusText,
                   j.business_date      AS BusinessDate,
                   j.revision           AS Revision,
                   j.is_effective       AS IsEffective,
                   j.attempt            AS Attempt,
                   j.rows_input         AS RowsInput,
                   j.rows_inserted      AS RowsInserted,
                   j.rows_invalid       AS RowsInvalid,
                   j.warning_count      AS WarningCount,
                   j.error_count        AS ErrorCount,
                   j.created_at         AS CreatedAt,
                   j.started_at         AS StartedAt,
                   j.finished_at        AS FinishedAt,
                   j.duration_ms        AS DurationMs,
                   j.created_by         AS CreatedBy,
                   j.error_summary      AS ErrorSummary
        """;

    private sealed record JobSummaryRow(
        long JobId,
        string SourceCode,
        string OriginalFileName,
        long FileBytes,
        string StatusText,
        DateOnly? BusinessDate,
        int Revision,
        bool IsEffective,
        int Attempt,
        long RowsInput,
        long RowsInserted,
        long RowsInvalid,
        int WarningCount,
        int ErrorCount,
        DateTimeOffset CreatedAt,
        DateTimeOffset? StartedAt,
        DateTimeOffset? FinishedAt,
        long? DurationMs,
        string CreatedBy,
        string? ErrorSummary)
    {
        public ImportJobSummary ToSummary() => new(
            JobId, SourceCode, OriginalFileName, FileBytes,
            ImportJobStatusExtensions.FromDatabaseValue(StatusText),
            BusinessDate, Revision, IsEffective, Attempt,
            RowsInput, RowsInserted, RowsInvalid, WarningCount, ErrorCount,
            CreatedAt, StartedAt, FinishedAt, DurationMs, CreatedBy, ErrorSummary);
    }

    /// <summary>
    /// Builds the WHERE clause for a history query.
    /// </summary>
    /// <remarks>
    /// Every fragment appended here is a string literal in this file; every value goes into
    /// <paramref name="parameters"/>. That split is the invariant that keeps this from being SQL
    /// injection, and it is why the method takes the parameter bag rather than returning
    /// interpolated text.
    /// </remarks>
    private static string BuildWhere(ImportHistoryFilter filter, DynamicParameters parameters)
    {
        var clauses = new StringBuilder(" WHERE 1 = 1");

        if (!string.IsNullOrWhiteSpace(filter.SourceCode))
        {
            clauses.Append(" AND j.source_code = @source");
            parameters.Add("source", filter.SourceCode);
        }

        if (filter.Statuses is { Count: > 0 })
        {
            clauses.Append(" AND j.status::text = ANY(@statuses)");
            parameters.Add("statuses", filter.Statuses.Select(s => s.ToDatabaseValue()).ToArray());
        }

        if (filter.BusinessDateFrom is { } from)
        {
            clauses.Append(" AND j.business_date >= @dateFrom");
            parameters.Add("dateFrom", from);
        }

        if (filter.BusinessDateTo is { } to)
        {
            clauses.Append(" AND j.business_date <= @dateTo");
            parameters.Add("dateTo", to);
        }

        if (!string.IsNullOrWhiteSpace(filter.FileNameContains))
        {
            // Bound as a value, so a name containing % or _ is matched literally rather than
            // turning into a wildcard the user did not ask for.
            clauses.Append(" AND f.original_file_name ILIKE '%' || @nameLike || '%'");
            parameters.Add("nameLike", filter.FileNameContains);
        }

        if (filter.EffectiveOnly)
        {
            clauses.Append(" AND j.is_effective");
        }

        return clauses.ToString();
    }

    public async Task<IReadOnlyList<ImportJobSummary>> ListJobsAsync(
        ImportHistoryFilter filter, int limit, int offset, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var parameters = new DynamicParameters();
        var where = BuildWhere(filter, parameters);
        parameters.Add("limit", Math.Clamp(limit, 1, 500));
        parameters.Add("offset", Math.Max(0, offset));

        var sql = $"""
            SELECT
            {JobSummaryColumns}
              FROM imports.import_job j
              JOIN imports.import_file f ON f.id = j.file_id
            {where}
             ORDER BY j.created_at DESC, j.id DESC
             LIMIT @limit OFFSET @offset
            """;

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        var rows = await connection.QueryAsync<JobSummaryRow>(Command(sql, parameters, ct))
            .ConfigureAwait(false);

        return [.. rows.Select(r => r.ToSummary())];
    }

    public async Task<long> CountJobsAsync(ImportHistoryFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var parameters = new DynamicParameters();
        var where = BuildWhere(filter, parameters);

        var sql = $"""
            SELECT COUNT(*)
              FROM imports.import_job j
              JOIN imports.import_file f ON f.id = j.file_id
            {where}
            """;

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<long>(Command(sql, parameters, ct))
            .ConfigureAwait(false);
    }

    public async Task<ImportJobDetail?> GetJobAsync(long jobId, CancellationToken ct)
    {
        var sql = $"""
            SELECT
            {JobSummaryColumns},
                   j.file_id         AS FileId,
                   f.sha256          AS Sha256,
                   f.stored_path     AS StoredPath,
                   f.is_blob_present AS IsBlobPresent,
                   sv.version        AS SchemaVersion,
                   j.supersedes_job_id   AS SupersedesJobId,
                   j.reprocess_of_job_id AS ReprocessOfJobId,
                   (SELECT s.id FROM imports.import_job s
                     WHERE s.supersedes_job_id = j.id
                     ORDER BY s.created_at DESC LIMIT 1) AS SupersededByJobId
              FROM imports.import_job j
              JOIN imports.import_file f ON f.id = j.file_id
              LEFT JOIN imports.schema_version sv ON sv.id = j.schema_version_id
             WHERE j.id = @job
            """;

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        var row = await connection.QuerySingleOrDefaultAsync<JobDetailRow>(
            Command(sql, new { job = jobId }, ct)).ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        const string EventSql = """
            SELECT occurred_at AS OccurredAt, severity AS Severity, stage AS Stage,
                   message AS Message, detail::text AS DetailJson
              FROM imports.import_event
             WHERE job_id = @job
             ORDER BY occurred_at, id
            """;

        const string ProgressSql = """
            SELECT stage AS Stage, rows_processed AS RowsProcessed, rows_expected AS RowsExpected,
                   percent AS Percent, updated_at AS UpdatedAt
              FROM imports.import_progress
             WHERE job_id = @job
            """;

        const string QuarantineSql = """
            SELECT id AS SummaryId, rule_code AS RuleCode, column_name AS ColumnName,
                   severity AS Severity, occurrence_count AS OccurrenceCount,
                   first_row_number AS FirstRowNumber, message AS Message
              FROM imports.quarantine_rule_summary
             WHERE job_id = @job
             ORDER BY occurrence_count DESC
            """;

        var events = await connection.QueryAsync<ImportEvent>(
            Command(EventSql, new { job = jobId }, ct)).ConfigureAwait(false);
        var progress = await connection.QuerySingleOrDefaultAsync<ImportProgress>(
            Command(ProgressSql, new { job = jobId }, ct)).ConfigureAwait(false);
        var quarantine = await connection.QueryAsync<QuarantineGroup>(
            Command(QuarantineSql, new { job = jobId }, ct)).ConfigureAwait(false);

        return new ImportJobDetail(
            row.ToSummary(),
            row.FileId,
            row.Sha256,
            row.StoredPath,
            row.IsBlobPresent,
            row.SchemaVersion,
            progress,
            [.. events],
            [.. quarantine],
            row.SupersededByJobId,
            row.SupersedesJobId,
            row.ReprocessOfJobId);
    }

    private sealed record JobDetailRow(
        long JobId, string SourceCode, string OriginalFileName, long FileBytes, string StatusText,
        DateOnly? BusinessDate, int Revision, bool IsEffective, int Attempt,
        long RowsInput, long RowsInserted, long RowsInvalid, int WarningCount, int ErrorCount,
        DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
        long? DurationMs, string CreatedBy, string? ErrorSummary,
        long FileId, string Sha256, string StoredPath, bool IsBlobPresent, string? SchemaVersion,
        long? SupersedesJobId, long? ReprocessOfJobId, long? SupersededByJobId)
    {
        public ImportJobSummary ToSummary() => new(
            JobId, SourceCode, OriginalFileName, FileBytes,
            ImportJobStatusExtensions.FromDatabaseValue(StatusText),
            BusinessDate, Revision, IsEffective, Attempt,
            RowsInput, RowsInserted, RowsInvalid, WarningCount, ErrorCount,
            CreatedAt, StartedAt, FinishedAt, DurationMs, CreatedBy, ErrorSummary);
    }

    public async Task<IReadOnlyList<QuarantineSample>> GetQuarantineSamplesAsync(
        long summaryId, int limit, CancellationToken ct)
    {
        const string Sql = """
            SELECT row_number AS RowNumber, raw_line AS RawLine, offending_value AS OffendingValue
              FROM imports.quarantine_sample
             WHERE summary_id = @summary
             ORDER BY row_number
             LIMIT @limit
            """;

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        var rows = await connection.QueryAsync<QuarantineSample>(
            Command(Sql, new { summary = summaryId, limit = Math.Clamp(limit, 1, 100) }, ct))
            .ConfigureAwait(false);

        return [.. rows];
    }

    public async Task<IReadOnlyList<SourceFreshness>> GetFreshnessAsync(
        DateOnly today, CancellationToken ct)
    {
        // Freshness is measured against the business date, not the import time. A file that
        // imported an hour ago and describes last month is stale data however recently it
        // arrived, and an operator reading "imported 1 hour ago" would conclude the opposite.
        const string LatestSql = """
            SELECT ds.code AS SourceCode,
                   latest.business_date AS LatestBusinessDate,
                   latest.finished_at   AS LatestImportedAt,
                   (SELECT COUNT(*) FROM imports.import_job j
                     WHERE j.source_code = ds.code
                       AND j.status = 'FAILED'
                       AND j.finished_at > now() - interval '7 days') AS FailedLast7Days
              FROM imports.data_source ds
              LEFT JOIN LATERAL (
                    SELECT j.business_date, j.finished_at
                      FROM imports.import_job j
                     WHERE j.source_code = ds.code
                       AND j.status IN ('COMPLETED', 'PARTIALLY_COMPLETED')
                     ORDER BY j.business_date DESC NULLS LAST, j.finished_at DESC
                     LIMIT 1
              ) latest ON true
             WHERE ds.is_enabled
             ORDER BY ds.code
            """;

        // A gap is only a gap against a calendar of what was expected. Inferring it from the
        // absence of a file cannot distinguish "the delivery failed" from "there was no delivery
        // that day", and only the first is a problem.
        const string MissingSql = """
            SELECT e.source_code AS SourceCode, e.business_date AS BusinessDate
              FROM imports.expected_business_date e
             WHERE NOT EXISTS (
                   SELECT 1 FROM imports.import_job j
                    WHERE j.source_code = e.source_code
                      AND j.business_date = e.business_date
                      AND j.is_effective)
             ORDER BY e.business_date DESC
            """;

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        var latest = (await connection.QueryAsync<FreshnessRow>(Command(LatestSql, null, ct))
            .ConfigureAwait(false)).ToList();
        var missing = (await connection.QueryAsync<MissingRow>(Command(MissingSql, null, ct))
            .ConfigureAwait(false)).ToList();

        return
        [
            .. latest.Select(row => new SourceFreshness(
                row.SourceCode,
                row.LatestBusinessDate,
                row.LatestImportedAt,
                row.LatestBusinessDate is { } date ? today.DayNumber - date.DayNumber : null,
                [.. missing.Where(m => m.SourceCode == row.SourceCode).Select(m => m.BusinessDate)],
                row.FailedLast7Days)),
        ];
    }

    private sealed record FreshnessRow(
        string SourceCode,
        DateOnly? LatestBusinessDate,
        DateTimeOffset? LatestImportedAt,
        int FailedLast7Days);

    private sealed record MissingRow(string SourceCode, DateOnly BusinessDate);

    public async Task<WorkerHealth> GetWorkerHealthAsync(CancellationToken ct)
    {
        const string Sql = """
            SELECT
                COUNT(*) FILTER (WHERE status = 'QUEUED')                       AS Queued,
                COUNT(*) FILTER (WHERE worker_id IS NOT NULL)                   AS Running,
                COUNT(*) FILTER (WHERE status = 'RETRYING')                     AS Retrying,
                COUNT(*) FILTER (WHERE status = 'FAILED'
                                   AND finished_at > now() - interval '24 hours') AS FailedLast24Hours,
                MIN(created_at) FILTER (WHERE status = 'QUEUED')                AS OldestQueuedAt,
                COUNT(DISTINCT worker_id) FILTER (WHERE lease_expires_at > now()) AS ActiveWorkers,
                COUNT(*) FILTER (WHERE lease_expires_at IS NOT NULL
                                   AND lease_expires_at < now())                AS StaleLeases
              FROM imports.import_job
            """;

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        return await connection.QuerySingleAsync<WorkerHealth>(Command(Sql, null, ct))
            .ConfigureAwait(false);
    }

    /// <summary>Formats a byte count for a log or event message.</summary>
    internal static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " KB",
        < 1024L * 1024 * 1024 => (bytes / (1024.0 * 1024)).ToString("F1", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / (1024.0 * 1024 * 1024)).ToString("F2", CultureInfo.InvariantCulture) + " GB",
    };
}
