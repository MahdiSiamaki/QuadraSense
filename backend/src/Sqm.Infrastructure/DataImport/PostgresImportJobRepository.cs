using System.Data;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Sqm.Application.DataImport;

namespace Sqm.Infrastructure.DataImport;

/// <summary>
/// PostgreSQL-backed job store and queue.
/// </summary>
/// <remarks>
/// <para>
/// Enum values cross the boundary as text with an explicit <c>::imports.job_status</c> cast
/// rather than through Npgsql's CLR enum mapping. The mapping is convenient but couples the
/// driver's global type registry to this one schema, and a mismatch surfaces as a startup-time
/// failure in unrelated code. Casting is explicit and local.
/// </para>
/// <para>
/// Every statement here is parameterised. There is no string concatenation of values anywhere in
/// this class; the filter builder in <c>ListJobsAsync</c> concatenates clause *fragments* whose
/// text is fixed at compile time, and binds every value.
/// </para>
/// </remarks>
public sealed partial class PostgresImportJobRepository : IImportJobRepository
{
    [LoggerMessage(EventId = 3000, Level = LogLevel.Information,
        Message = "Worker {WorkerId} claimed job {JobId} ({SourceCode}, attempt {Attempt})")]
    private partial void LogClaimed(string workerId, long jobId, string sourceCode, int attempt);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Warning,
        Message = "Recovered {Count} job(s) whose worker lease had expired")]
    private partial void LogLeasesRecovered(int count);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Information,
        Message = "File {FileName} is already known by content hash; existing job {JobId}")]
    private partial void LogDuplicateFile(string fileName, long? jobId);

    /// <summary>
    /// Teaches Dapper how to bind a <see cref="DateOnly"/>.
    /// </summary>
    /// <remarks>
    /// Dapper does not know the type and throws "cannot be used as a parameter value" for every
    /// business date, which is most statements in this class. Npgsql maps it natively, so the
    /// handler only has to name the DbType and hand the value straight through.
    ///
    /// Registered in a static constructor rather than at startup, so a caller cannot construct
    /// this repository without it. It was found by an integration test against a real database;
    /// a mocked repository would have been perfectly happy.
    /// </remarks>
    private sealed class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override void SetValue(System.Data.IDbDataParameter parameter, DateOnly value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value;
        }

        public override DateOnly Parse(object value) => value switch
        {
            DateOnly date => date,
            DateTime timestamp => DateOnly.FromDateTime(timestamp),
            string text => DateOnly.Parse(text, CultureInfo.InvariantCulture),
            _ => throw new InvalidCastException(
                $"cannot read a date from {value?.GetType().Name ?? "null"}"),
        };
    }

    /// <summary>
    /// Teaches Dapper how to read a <c>timestamptz</c> into a <see cref="DateTimeOffset"/>.
    /// </summary>
    /// <remarks>
    /// Npgsql surfaces <c>timestamptz</c> as a UTC <see cref="DateTime"/>, and Dapper matches a
    /// record's constructor by comparing parameter types against the reader's field types - so
    /// every model using DateTimeOffset failed to materialise with "a parameterless default
    /// constructor ... is required", which says nothing about the actual mismatch.
    ///
    /// The models keep DateTimeOffset rather than bending to the driver: these values cross an
    /// HTTP boundary and reach a browser in another timezone, and a DateTime that merely
    /// promises to be UTC is one careless conversion away from being wrong by hours.
    /// </remarks>
    private sealed class DateTimeOffsetTypeHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override void SetValue(System.Data.IDbDataParameter parameter, DateTimeOffset value)
        {
            parameter.DbType = DbType.DateTimeOffset;
            parameter.Value = value;
        }

        public override DateTimeOffset Parse(object value) => value switch
        {
            DateTimeOffset offset => offset,
            DateTime timestamp => new DateTimeOffset(
                DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)),
            string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture),
            _ => throw new InvalidCastException(
                $"cannot read a timestamp from {value?.GetType().Name ?? "null"}"),
        };
    }

    static PostgresImportJobRepository()
    {
        // Dapper registers the nullable form alongside the value type, so the nullable
        // counterparts are covered too.
        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
        SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
    }

    private readonly NpgsqlDataSource _dataSource;
    private readonly int _commandTimeout;
    private readonly ILogger<PostgresImportJobRepository> _logger;

    public PostgresImportJobRepository(
        IOptions<PostgresOptions> options,
        ILogger<PostgresImportJobRepository> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = options.Value;

        if (string.IsNullOrWhiteSpace(settings.ConnectionString))
        {
            throw new InvalidOperationException(
                $"{PostgresOptions.SectionName}:ConnectionString is not configured.");
        }

        // A data source, not bare connection strings: it owns the pool and the type mappings, so
        // every connection in the process shares one warmed-up configuration.
        _dataSource = new NpgsqlDataSourceBuilder(settings.ConnectionString).Build();
        _commandTimeout = settings.CommandTimeoutSeconds;
        _logger = logger;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct) =>
        await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

    private CommandDefinition Command(
        string sql, object? parameters, CancellationToken ct, IDbTransaction? transaction = null) =>
        new(sql, parameters, transaction, _commandTimeout, cancellationToken: ct);

    // =======================================================================
    // Intake
    // =======================================================================

    public async Task<RegisteredFile> RegisterFileAsync(
        string sourceCode,
        string originalFileName,
        StoredFile stored,
        string uploadedBy,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stored);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        // ON CONFLICT DO NOTHING rather than a SELECT-then-INSERT. Two operators uploading the
        // same file at the same moment both pass a prior existence check; only one can win a
        // unique index. The loser gets an empty result here and falls through to the lookup.
        const string InsertSql = """
            INSERT INTO imports.import_file
                (source_code, original_file_name, stored_path, file_bytes, sha256, uploaded_by)
            VALUES (@source, @name, @path, @bytes, @sha, @by)
            ON CONFLICT (source_code, sha256) DO NOTHING
            RETURNING id
            """;

        var inserted = await connection.ExecuteScalarAsync<long?>(Command(InsertSql, new
        {
            source = sourceCode,
            name = originalFileName,
            path = stored.StoredPath,
            bytes = stored.SizeBytes,
            sha = stored.Sha256,
            by = uploadedBy,
        }, ct)).ConfigureAwait(false);

        if (inserted is { } newId)
        {
            return new RegisteredFile(newId, stored.Sha256, IsNew: true, null, null);
        }

        const string ExistingSql = """
            SELECT f.id                                      AS FileId,
                   f.original_file_name                      AS OriginalName,
                   (SELECT j.id
                      FROM imports.import_job j
                     WHERE j.file_id = f.id
                     ORDER BY j.created_at DESC
                     LIMIT 1)                                AS ExistingJobId
              FROM imports.import_file f
             WHERE f.source_code = @source AND f.sha256 = @sha
            """;

        var existing = await connection.QuerySingleAsync<ExistingFileRow>(
            Command(ExistingSql, new { source = sourceCode, sha = stored.Sha256 }, ct))
            .ConfigureAwait(false);

        LogDuplicateFile(originalFileName, existing.ExistingJobId);

        return new RegisteredFile(
            existing.FileId, stored.Sha256, IsNew: false,
            existing.ExistingJobId, existing.OriginalName);
    }

    private sealed record ExistingFileRow(long FileId, string OriginalName, long? ExistingJobId);

    public async Task<long> EnqueueAsync(
        string sourceCode,
        long fileId,
        DateOnly? businessDate,
        string createdBy,
        int priority = 0,
        long? reprocessOfJobId = null,
        CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        const string Sql = """
            INSERT INTO imports.import_job
                (source_code, file_id, status, business_date, priority, created_by,
                 reprocess_of_job_id, revision)
            VALUES (@source, @file, 'QUEUED', @date, @priority, @by, @reprocess,
                    COALESCE((SELECT MAX(revision) + 1
                                FROM imports.import_job
                               WHERE source_code = @source AND business_date = @date), 1))
            RETURNING id
            """;

        var jobId = await connection.ExecuteScalarAsync<long>(Command(Sql, new
        {
            source = sourceCode,
            file = fileId,
            date = businessDate,
            priority,
            by = createdBy,
            reprocess = reprocessOfJobId,
        }, ct, transaction)).ConfigureAwait(false);

        await AppendEventCoreAsync(
            connection, transaction, jobId, "info", null,
            reprocessOfJobId is null
                ? "Queued for import"
                : $"Queued as a reprocess of job {reprocessOfJobId}",
            null, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return jobId;
    }

    // =======================================================================
    // Worker loop
    // =======================================================================

    public async Task<ClaimedJob?> ClaimNextAsync(
        string workerId, TimeSpan leaseDuration, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        // SKIP LOCKED is the whole point: a second worker running this statement at the same
        // instant steps over the row the first one locked instead of blocking behind it. Without
        // it, N workers serialise into one.
        //
        // Ordering, in order of precedence:
        //   priority DESC     - an operator can push a job to the front
        //   business_date     - oldest day first, so a backlog drains in calendar order
        //   created_at        - stable tiebreak
        //
        // AND THE ORDER IS NOW A CONSTRAINT, NOT A PREFERENCE. The comment that used to sit here
        // said calendar order mattered "for presentation rather than correctness", because the
        // fold depends only on the last event per binding and converges whatever order days
        // arrive in. That part is true and still is.
        //
        // What it missed is that the FOLD is not the only thing built per delivery. Every
        // dashboard mart is "the state of the network at delivery N", aggregated from
        // binding_current as it stands when the mart runs - so it is only delivery N's state
        // while no other delivery has been folded around it. Three days proved it: 06-16 and
        // 06-17 were interrupted, retried after 06-18 had folded, and all three deliveries ended
        // up holding the identical figure of 114,230,645 active bindings while the per-day change
        // marts said 06-16 had gained 167,111.
        //
        // ORDER BY alone could not prevent that, because SKIP LOCKED is doing its job: an earlier
        // day held by another worker - or by a worker that died still holding it - is stepped
        // over, and the next day starts. The NOT EXISTS below is what makes the preference a
        // rule.
        const string Sql = """
            WITH claimed AS (
                SELECT j.id
                  FROM imports.import_job j
                 WHERE j.status IN ('QUEUED', 'RETRYING')
                   AND (j.run_after IS NULL OR j.run_after <= now())
                   AND NOT j.cancel_requested
                   -- No earlier day for this source may still be unlanded.
                   --
                   -- "Landed" is COMPLETED or PARTIALLY_COMPLETED: both mean the day's events are
                   -- in the analytics store. Everything else blocks, including the terminal
                   -- failures - a day that FAILED or was CANCELLED leaves a hole, and running the
                   -- next day over a hole produces a snapshot of a network that never existed.
                   -- Blocking is visible and has an exit: reprocess the failed day, or delete it.
                   -- Silently skipping it would not.
                   --
                   -- Scoped to jobs that HAVE a business date, so the initial dump and the TAC
                   -- snapshots - which describe a range or no day at all - neither block nor are
                   -- blocked. A day the source never delivered has no job row and cannot block
                   -- anything either; you cannot wait for something nobody queued.
                   -- The condition is about earlier DAYS, not earlier JOBS, and the difference
                   -- is what jammed the queue once.
                   --
                   -- 2026-07-11 failed as job 593 and was re-imported successfully as job 682.
                   -- The day had landed; the failed attempt had not. Written as "no earlier job
                   -- that did not succeed", job 593 blocked 07-12 through 07-15 indefinitely, and
                   -- nothing short of editing the row would have released them - a reprocess adds
                   -- a NEW job and leaves the old one exactly as it was.
                   --
                   -- So the inner NOT EXISTS asks the question that actually matters: is there an
                   -- earlier day whose events are not in the analytics store? A failed attempt for
                   -- a day that some other job landed is history, not a blockage.
                   AND (j.business_date IS NULL OR NOT EXISTS (
                           SELECT 1
                             FROM imports.import_job earlier
                            WHERE earlier.source_code = j.source_code
                              AND earlier.business_date IS NOT NULL
                              AND earlier.business_date < j.business_date
                              AND earlier.status NOT IN ('COMPLETED', 'PARTIALLY_COMPLETED')
                              AND NOT EXISTS (
                                      SELECT 1
                                        FROM imports.import_job landed
                                       WHERE landed.source_code = earlier.source_code
                                         AND landed.business_date = earlier.business_date
                                         AND landed.status IN ('COMPLETED', 'PARTIALLY_COMPLETED')
                                  )
                       ))
                 ORDER BY j.priority DESC, j.business_date NULLS LAST, j.created_at
                   FOR UPDATE SKIP LOCKED
                 LIMIT 1
            )
            UPDATE imports.import_job j
               SET status           = 'VALIDATING',
                   current_stage    = 'VALIDATING',
                   worker_id        = @worker,
                   lease_expires_at = now() + @lease,
                   attempt          = j.attempt + 1,
                   started_at       = COALESCE(j.started_at, now()),
                   stage_started_at = now()
              FROM claimed c, imports.import_file f
             WHERE j.id = c.id
               AND f.id = j.file_id
            RETURNING j.id                  AS JobId,
                      j.source_code         AS SourceCode,
                      j.file_id             AS FileId,
                      f.stored_path         AS StoredPath,
                      f.original_file_name  AS OriginalFileName,
                      f.sha256              AS Sha256,
                      f.file_bytes          AS FileBytes,
                      j.business_date       AS BusinessDate,
                      j.attempt::int        AS Attempt,
                      j.max_attempts::int   AS MaxAttempts,
                      j.priority::int       AS Priority,
                      j.reprocess_of_job_id AS ReprocessOfJobId
            """;

        var job = await connection.QuerySingleOrDefaultAsync<ClaimedJob>(
            Command(Sql, new { worker = workerId, lease = leaseDuration }, ct)).ConfigureAwait(false);

        if (job is not null)
        {
            LogClaimed(workerId, job.JobId, job.SourceCode, job.Attempt);
            await AppendEventAsync(
                job.JobId, "info", "VALIDATING",
                $"Claimed by worker {workerId} (attempt {job.Attempt} of {job.MaxAttempts})",
                null, ct).ConfigureAwait(false);
        }

        return job;
    }

    public async Task<bool> RenewLeaseAsync(
        long jobId, string workerId, TimeSpan leaseDuration, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        // The worker_id predicate is what makes this safe. If the lease expired and another
        // worker took the job over, this update matches nothing and the caller learns it has
        // lost the job - rather than two workers renewing the same lease forever.
        const string Sql = """
            UPDATE imports.import_job
               SET lease_expires_at = now() + @lease
             WHERE id = @job AND worker_id = @worker AND lease_expires_at IS NOT NULL
            """;

        var rows = await connection.ExecuteAsync(
            Command(Sql, new { job = jobId, worker = workerId, lease = leaseDuration }, ct))
            .ConfigureAwait(false);

        return rows == 1;
    }

    public async Task SetStageAsync(long jobId, ImportJobStatus status, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        const string Sql = """
            UPDATE imports.import_job
               SET status           = @status::imports.job_status,
                   current_stage    = @stage,
                   stage_started_at = now()
             WHERE id = @job
            """;

        var label = status.ToDatabaseValue();
        await connection.ExecuteAsync(
            Command(Sql, new { job = jobId, status = label, stage = label }, ct)).ConfigureAwait(false);
    }

    public async Task RecordHeartbeatAsync(
        string workerId, string hostname, int maxConcurrent, bool hostedInApi, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        // started_at is preserved on conflict, so "running since" means what it says across the
        // hundreds of heartbeats a long-lived worker writes. Everything else is refreshed,
        // because concurrency and hosting can change when a worker is restarted with new options.
        const string Sql = """
            INSERT INTO imports.worker_heartbeat
                (worker_id, hostname, max_concurrent, hosted_in_api, started_at, last_seen_at)
            VALUES (@id, @host, @concurrent, @inApi, now(), now())
            ON CONFLICT (worker_id) DO UPDATE
               SET hostname       = EXCLUDED.hostname,
                   max_concurrent = EXCLUDED.max_concurrent,
                   hosted_in_api  = EXCLUDED.hosted_in_api,
                   last_seen_at   = now()
            """;

        await connection.ExecuteAsync(Command(Sql, new
        {
            id = workerId,
            host = hostname,
            concurrent = maxConcurrent,
            inApi = hostedInApi,
        }, ct)).ConfigureAwait(false);

        // Sweep away workers that are never coming back. Without this the table grows by one row
        // for every worker process ever started - which on a development machine is every restart
        // - and "how many workers are there" slowly becomes a count of history.
        //
        // An hour, not thirty seconds: the liveness window decides who is ALIVE, and this decides
        // who is FORGOTTEN. Deleting a worker the moment it misses a beat would erase the
        // evidence that it existed at the exact moment somebody is trying to work out why it
        // stopped.
        const string SweepSql = """
            DELETE FROM imports.worker_heartbeat WHERE last_seen_at < now() - interval '1 hour'
            """;

        await connection.ExecuteAsync(Command(SweepSql, null, ct)).ConfigureAwait(false);
    }

    public async Task ReportProgressAsync(
        long jobId, string stage, long bytesProcessed, long? bytesExpected, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        // Percent is computed here rather than by the UI so that every consumer - the progress
        // bar, an alert, a log line - agrees on one number. Capped at 100: a row estimate from
        // file size can be low, and a progress bar that reads 104% destroys confidence in the
        // rest of the screen.
        const string Sql = """
            INSERT INTO imports.import_progress
                (job_id, stage, bytes_processed, bytes_expected, percent, updated_at)
            VALUES (@job, @stage, @processed, @expected,
                    CASE WHEN @expected IS NULL OR @expected = 0 THEN NULL
                         ELSE LEAST(100, ROUND(@processed::numeric * 100 / @expected, 2)) END,
                    now())
            ON CONFLICT (job_id) DO UPDATE
               SET stage          = EXCLUDED.stage,
                   bytes_processed = EXCLUDED.bytes_processed,
                   bytes_expected  = EXCLUDED.bytes_expected,
                   percent        = EXCLUDED.percent,
                   updated_at     = EXCLUDED.updated_at
            """;

        await connection.ExecuteAsync(Command(Sql, new
        {
            job = jobId,
            stage,
            processed = bytesProcessed,
            expected = bytesExpected,
        }, ct)).ConfigureAwait(false);
    }

    public async Task AppendEventAsync(
        long jobId, string severity, string? stage, string message, object? detail, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await AppendEventCoreAsync(connection, null, jobId, severity, stage, message, detail, ct)
            .ConfigureAwait(false);
    }

    private async Task AppendEventCoreAsync(
        NpgsqlConnection connection,
        IDbTransaction? transaction,
        long jobId,
        string severity,
        string? stage,
        string message,
        object? detail,
        CancellationToken ct)
    {
        const string Sql = """
            INSERT INTO imports.import_event (job_id, severity, stage, message, detail)
            VALUES (@job, @severity, @stage, @message, @detail::jsonb)
            """;

        await connection.ExecuteAsync(Command(Sql, new
        {
            job = jobId,
            severity,
            stage,
            message,
            detail = detail is null ? null : JsonSerializer.Serialize(detail),
        }, ct, transaction)).ConfigureAwait(false);
    }

    public async Task RecordQuarantineAsync(
        long jobId, IReadOnlyList<QuarantineWrite> groups, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (groups.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        const string GroupSql = """
            INSERT INTO imports.quarantine_rule_summary
                (job_id, rule_code, column_name, severity, occurrence_count, first_row_number, message)
            VALUES (@job, @rule, @column, @severity, @count, @firstRow, @message)
            ON CONFLICT (job_id, rule_code, column_name) DO UPDATE
               SET occurrence_count = EXCLUDED.occurrence_count,
                   first_row_number = LEAST(quarantine_rule_summary.first_row_number,
                                            EXCLUDED.first_row_number)
            RETURNING id
            """;

        const string SampleSql = """
            INSERT INTO imports.quarantine_sample (summary_id, row_number, raw_line, offending_value)
            VALUES (@summary, @row, @line, @value)
            """;

        foreach (var group in groups)
        {
            var summaryId = await connection.ExecuteScalarAsync<long>(Command(GroupSql, new
            {
                job = jobId,
                rule = group.RuleCode,
                column = group.ColumnName,
                severity = group.Severity,
                count = group.OccurrenceCount,
                firstRow = group.FirstRowNumber,
                message = group.Message,
            }, ct, transaction)).ConfigureAwait(false);

            foreach (var sample in group.Samples)
            {
                await connection.ExecuteAsync(Command(SampleSql, new
                {
                    summary = summaryId,
                    row = sample.RowNumber,
                    line = sample.RawLine,
                    value = sample.OffendingValue,
                }, ct, transaction)).ConfigureAwait(false);
            }
        }

        // Counted from distinct rules, not from rows. "31,209 rows failed one rule" and
        // "31,209 rows failed 400 different rules" are very different situations, and the second
        // number is the one that tells an operator the file is structurally wrong.
        const string CountSql = """
            UPDATE imports.import_job
               SET warning_count = (SELECT COUNT(*) FROM imports.quarantine_rule_summary
                                     WHERE job_id = @job AND severity = 'warning'),
                   error_count   = (SELECT COUNT(*) FROM imports.quarantine_rule_summary
                                     WHERE job_id = @job AND severity = 'error')
             WHERE id = @job
            """;

        await connection.ExecuteAsync(Command(CountSql, new { job = jobId }, ct, transaction))
            .ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> CompleteAsync(
        long jobId,
        ImportJobStatus status,
        ImportCounters counters,
        bool makeEffective,
        DateOnly? businessDate,
        string? workerId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(counters);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Only while this worker still holds the job - checked under a row lock, so the recovery
        // sweep cannot hand it on between the check and the writes below. Every other write in
        // the state machine already asked this; the three that end a job did not, so a worker
        // that had lost its lease could overwrite the result of the one that took the job over.
        if (workerId is not null && !await HoldsAsync(connection, transaction, jobId, workerId, ct)
                .ConfigureAwait(false))
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }

        // The day this job turned out to describe, written BEFORE effectiveness is decided.
        //
        // The processor is the only thing that knows it: a file uploaded through the browser is
        // enqueued with no date, and the date is read from the file's own name when the job is
        // claimed. Nothing used to write it back, and the consequences were quiet and bad - every
        // browser-uploaded job kept business_date NULL, the demotion below matches on
        // `IS NOT DISTINCT FROM` so all of them counted as the same day, each new daily import
        // demoted the previous one, and the initial dump lost its effective flag to a daily file.
        // The freshness card reads this column, so the dashboard also went on reporting the last
        // date a script-loaded job had recorded, however many days were imported after it.
        //
        // In this transaction, and first, so the demotion below matches on the real day.
        if (businessDate is not null)
        {
            const string DateSql = """
                UPDATE imports.import_job
                   SET business_date = @date
                 WHERE id = @job AND business_date IS DISTINCT FROM @date
                """;

            await connection.ExecuteAsync(
                Command(DateSql, new { job = jobId, date = businessDate.Value }, ct, transaction))
                .ConfigureAwait(false);
        }

        long? superseded = null;

        if (makeEffective)
        {
            // Demote first, promote second, in one transaction. A partial unique index allows at
            // most one effective job per (source, business_date), so doing this in the other
            // order would violate it rather than silently produce two.
            const string DemoteSql = """
                UPDATE imports.import_job
                   SET is_effective = false
                 WHERE is_effective
                   AND source_code = (SELECT source_code FROM imports.import_job WHERE id = @job)
                   AND business_date IS NOT DISTINCT FROM
                       (SELECT business_date FROM imports.import_job WHERE id = @job)
                   AND id <> @job
                RETURNING id
                """;

            superseded = await connection.ExecuteScalarAsync<long?>(
                Command(DemoteSql, new { job = jobId }, ct, transaction)).ConfigureAwait(false);
        }

        const string CompleteSql = """
            UPDATE imports.import_job
               SET status          = @status::imports.job_status,
                   current_stage   = NULL,
                   finished_at     = now(),
                   lease_expires_at= NULL,
                   worker_id       = NULL,
                   is_effective    = @effective,
                   supersedes_job_id = COALESCE(@superseded, supersedes_job_id),
                   rows_input      = @input,
                   rows_valid      = @valid,
                   rows_invalid    = @invalid,
                   rows_inserted   = @inserted,
                   rows_updated    = @updated,
                   rows_duplicate  = @duplicate,
                   rows_rejected   = @rejected,
                   rows_committed  = @inserted
             WHERE id = @job
            """;

        await connection.ExecuteAsync(Command(CompleteSql, new
        {
            job = jobId,
            status = status.ToDatabaseValue(),
            effective = makeEffective,
            superseded,
            input = counters.RowsInput,
            valid = counters.RowsValid,
            invalid = counters.RowsInvalid,
            inserted = counters.RowsInserted,
            updated = counters.RowsUpdated,
            duplicate = counters.RowsDuplicate,
            rejected = counters.RowsRejected,
        }, ct, transaction)).ConfigureAwait(false);

        var message = superseded is { } previous
            ? $"Finished as {status.ToDatabaseValue()}; now the effective import for this day, "
              + $"replacing job {previous}"
            : $"Finished as {status.ToDatabaseValue()}";

        await AppendEventCoreAsync(
            connection, transaction, jobId,
            status is ImportJobStatus.Completed ? "info" : "warning",
            null, message, counters, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Whether <paramref name="workerId"/> holds the job, locking its row if so.</summary>
    private async Task<bool> HoldsAsync(
        Npgsql.NpgsqlConnection connection, System.Data.IDbTransaction transaction,
        long jobId, string workerId, CancellationToken ct)
    {
        const string Sql = """
            SELECT id FROM imports.import_job
             WHERE id = @job AND worker_id = @worker
               FOR UPDATE
            """;

        return await connection.ExecuteScalarAsync<long?>(
            Command(Sql, new { job = jobId, worker = workerId }, ct, transaction))
            .ConfigureAwait(false) is not null;
    }

    public async Task<bool> FailAsync(
        long jobId, string errorSummary, bool isRetryable, TimeSpan retryDelay, string? workerId,
        CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Whether another attempt happens is decided in SQL, against the row's own attempt count,
        // rather than in the worker. The worker that just failed is the least trustworthy place
        // to make that decision - it may be the thing that is broken.
        // A pending cancellation wins over a retry. The claim skips cancel_requested jobs, so a
        // job set to RETRYING with one pending was never claimed again and never finished - and
        // RETRYING is not "landed", so every later day of its source waited behind it.
        const string Sql = """
            UPDATE imports.import_job
               SET status = CASE
                       WHEN cancel_requested THEN 'CANCELLED'::imports.job_status
                       WHEN @retryable AND attempt < max_attempts THEN 'RETRYING'::imports.job_status
                       ELSE 'FAILED'::imports.job_status
                   END,
                   run_after = CASE
                       WHEN NOT cancel_requested AND @retryable AND attempt < max_attempts
                       THEN now() + @delay
                       ELSE NULL
                   END,
                   finished_at = CASE
                       WHEN NOT cancel_requested AND @retryable AND attempt < max_attempts THEN NULL
                       ELSE now()
                   END,
                   current_stage    = NULL,
                   worker_id        = NULL,
                   lease_expires_at = NULL,
                   error_summary    = @error
             WHERE id = @job
               AND (@worker::text IS NULL OR worker_id = @worker)
            RETURNING status::text
            """;

        var outcome = await connection.ExecuteScalarAsync<string?>(Command(Sql, new
        {
            job = jobId,
            retryable = isRetryable,
            delay = retryDelay,
            error = errorSummary,
            worker = workerId,
        }, ct, transaction)).ConfigureAwait(false);

        if (outcome is null)
        {
            // Not this worker's any more: whoever holds it now decides how it ends.
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }

        var willRetry = outcome == "RETRYING";
        var cancelled = outcome == "CANCELLED";

        await AppendEventCoreAsync(
            connection, transaction, jobId, "error", null,
            // Three endings, not two. "No attempts remain" on attempt 1 of 3 is a contradiction
            // on screen, and it sends the reader looking for a retry that was never going to
            // happen: a rejected file is not a failed attempt, it is an answer.
            cancelled
                ? $"Failed: {errorSummary}. Cancelled, as requested, rather than retried."
                : willRetry
                ? $"Failed: {errorSummary}. Retrying in {retryDelay.TotalSeconds:F0}s."
                : isRetryable
                    ? $"Failed: {errorSummary}. No attempts remain."
                    : $"Failed: {errorSummary}. Not retried - the file itself is the problem, so "
                      + "another attempt would fail the same way.",
            null, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return willRetry;
    }

    public async Task<bool> MarkCancelledAsync(long jobId, string? workerId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        const string Sql = """
            UPDATE imports.import_job
               SET status = 'CANCELLED', finished_at = now(), current_stage = NULL,
                   worker_id = NULL, lease_expires_at = NULL
             WHERE id = @job
               AND (@worker::text IS NULL OR worker_id = @worker)
            """;

        var affected = await connection.ExecuteAsync(
            Command(Sql, new { job = jobId, worker = workerId }, ct, transaction))
            .ConfigureAwait(false);

        if (affected == 0)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }
        await AppendEventCoreAsync(
            connection, transaction, jobId, "warning", null,
            "Cancelled at a stage boundary; nothing partial was left behind", null, ct)
            .ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> IsCancellationRequestedAsync(long jobId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<bool>(Command(
            "SELECT cancel_requested FROM imports.import_job WHERE id = @job",
            new { job = jobId }, ct)).ConfigureAwait(false);
    }

    public async Task<int> RecoverExpiredLeasesAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        // A worker that died mid-job left its row in a working status with a lease that stopped
        // being renewed. Returning it to RETRYING rather than QUEUED keeps the distinction
        // visible: this job has been attempted, and the attempt counter already reflects it.
        const string Sql = """
            UPDATE imports.import_job
               SET status = CASE WHEN cancel_requested
                                 THEN 'CANCELLED'::imports.job_status
                                 WHEN attempt < max_attempts
                                 THEN 'RETRYING'::imports.job_status
                                 ELSE 'FAILED'::imports.job_status END,
                   worker_id = NULL,
                   lease_expires_at = NULL,
                   current_stage = NULL,
                   run_after = now(),
                   finished_at = CASE WHEN NOT cancel_requested AND attempt < max_attempts
                                      THEN NULL ELSE now() END,
                   error_summary = COALESCE(error_summary,
                       'Worker stopped responding; the lease expired while the job was ' || status)
             WHERE lease_expires_at IS NOT NULL
               AND lease_expires_at < now()
            RETURNING id
            """;

        var recovered = (await connection.QueryAsync<long>(Command(Sql, null, ct))
            .ConfigureAwait(false)).ToList();

        if (recovered.Count > 0)
        {
            LogLeasesRecovered(recovered.Count);
            foreach (var jobId in recovered)
            {
                await AppendEventAsync(
                    jobId, "error", null,
                    "Worker lease expired; the job was returned to the queue", null, ct)
                    .ConfigureAwait(false);
            }
        }

        return recovered.Count;
    }

    // =======================================================================
    // Operator actions
    // =======================================================================

    public async Task<bool> RequestCancellationAsync(long jobId, string actor, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        // A queued job is cancelled outright - no worker holds it, so there is nothing to ask.
        // A running job only gets the flag set; the worker acts on it at the next stage boundary,
        // which is what keeps a cancellation from tearing a write in half.
        const string Sql = """
            UPDATE imports.import_job
               SET cancel_requested = true,
                   status = CASE WHEN status IN ('QUEUED', 'RETRYING', 'UPLOADED')
                                 THEN 'CANCELLED'::imports.job_status ELSE status END,
                   finished_at = CASE WHEN status IN ('QUEUED', 'RETRYING', 'UPLOADED')
                                      THEN now() ELSE finished_at END
             WHERE id = @job
               AND status NOT IN ('COMPLETED', 'PARTIALLY_COMPLETED', 'DUPLICATE',
                                  'FAILED', 'QUARANTINED', 'CANCELLED')
            RETURNING id
            """;

        var affected = await connection.ExecuteScalarAsync<long?>(
            Command(Sql, new { job = jobId }, ct, transaction)).ConfigureAwait(false);

        if (affected is null)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }

        await AppendEventCoreAsync(
            connection, transaction, jobId, "warning", null,
            $"Cancellation requested by {actor}", null, ct).ConfigureAwait(false);

        await WriteAuditCoreAsync(
            connection, transaction, actor, "import.cancel", jobId, null, null, null, null, ct)
            .ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task WriteAuditAsync(
        string actor,
        string action,
        long? jobId,
        long? fileId,
        long? tacVersionId,
        string? correlationId,
        object? detail,
        CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await WriteAuditCoreAsync(
            connection, null, actor, action, jobId, fileId, tacVersionId, correlationId, detail, ct)
            .ConfigureAwait(false);
    }

    private async Task WriteAuditCoreAsync(
        NpgsqlConnection connection,
        IDbTransaction? transaction,
        string actor,
        string action,
        long? jobId,
        long? fileId,
        long? tacVersionId,
        string? correlationId,
        object? detail,
        CancellationToken ct)
    {
        const string Sql = """
            INSERT INTO imports.import_audit
                (actor, action, job_id, file_id, tac_version_id, correlation_id, detail)
            VALUES (@actor, @action, @job, @file, @tac, @correlation, @detail::jsonb)
            """;

        await connection.ExecuteAsync(Command(Sql, new
        {
            actor,
            action,
            job = jobId,
            file = fileId,
            tac = tacVersionId,
            correlation = correlationId,
            detail = detail is null ? null : JsonSerializer.Serialize(detail),
        }, ct, transaction)).ConfigureAwait(false);
    }
}
