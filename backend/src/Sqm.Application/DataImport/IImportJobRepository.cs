namespace Sqm.Application.DataImport;

/// <summary>
/// The import platform's operational store: the queue, the job records, and their history.
/// </summary>
/// <remarks>
/// <para>
/// This is both a repository and a queue, which is deliberate. A separate broker would put the
/// queue and the job's own record in two systems that can disagree - a message delivered for a
/// job the database says is cancelled, a job the database says is queued with no message behind
/// it. PostgreSQL's <c>FOR UPDATE SKIP LOCKED</c> makes claiming a job and recording that it was
/// claimed the same transaction, so the two cannot drift.
/// </para>
/// <para>
/// The volume justifies it: measured deliveries are roughly one file a day per source. A broker
/// is the right answer at thousands of messages a second and the wrong one at one a day.
/// </para>
/// </remarks>
public interface IImportJobRepository
{
    // -----------------------------------------------------------------------
    // Intake
    // -----------------------------------------------------------------------

    /// <summary>
    /// Records an uploaded file, or reports that its content is already known.
    /// </summary>
    /// <remarks>
    /// Identity is the content hash, enforced by a unique index. Doing the check in application
    /// code would leave a window in which two concurrent uploads of the same file both see
    /// "not present" and both insert; letting the database refuse the second insert closes it.
    /// </remarks>
    Task<RegisteredFile> RegisterFileAsync(
        string sourceCode,
        string originalFileName,
        StoredFile stored,
        string uploadedBy,
        CancellationToken ct);

    /// <summary>Creates a job for a registered file and puts it in the queue.</summary>
    /// <param name="sourceCode">Data source.</param>
    /// <param name="fileId">The file to import.</param>
    /// <param name="businessDate">The day it describes, when known at upload time.</param>
    /// <param name="createdBy">Who asked for it.</param>
    /// <param name="priority">Higher runs first. Default 0.</param>
    /// <param name="reprocessOfJobId">Set when re-running an earlier job.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<long> EnqueueAsync(
        string sourceCode,
        long fileId,
        DateOnly? businessDate,
        string createdBy,
        int priority = 0,
        long? reprocessOfJobId = null,
        CancellationToken ct = default);

    // -----------------------------------------------------------------------
    // Worker loop
    // -----------------------------------------------------------------------

    /// <summary>
    /// Takes the next runnable job, or returns <see langword="null"/> when there is none.
    /// </summary>
    /// <param name="workerId">Identifies the claiming worker in the job record.</param>
    /// <param name="leaseDuration">
    /// How long the claim is good for. A worker that dies stops renewing; once the lease expires
    /// the job becomes claimable again. This is what makes crash recovery automatic rather than a
    /// manual clean-up.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<ClaimedJob?> ClaimNextAsync(string workerId, TimeSpan leaseDuration, CancellationToken ct);

    /// <summary>
    /// Extends the lease on a job this worker holds. Returns <see langword="false"/> when the
    /// claim is gone - the worker lost the job and must stop working on it.
    /// </summary>
    Task<bool> RenewLeaseAsync(
        long jobId, string workerId, TimeSpan leaseDuration, CancellationToken ct);

    /// <summary>Moves a job to a new stage.</summary>
    Task SetStageAsync(long jobId, ImportJobStatus status, CancellationToken ct);

    /// <summary>Records that this worker is alive and polling.</summary>
    /// <param name="workerId">Stable per worker, so a restart replaces its row.</param>
    /// <param name="hostname">Where it runs.</param>
    /// <param name="maxConcurrent">How many jobs it will run at once.</param>
    /// <param name="hostedInApi">Whether it shares a process with the API.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Called on every poll, which is cheap and self-correcting: a worker that stops polling for
    /// any reason - crashed, killed, deployed over - stops updating its row, and that absence is
    /// the signal. Nothing has to notice the death and report it.
    /// </remarks>
    Task RecordHeartbeatAsync(
        string workerId, string hostname, int maxConcurrent, bool hostedInApi, CancellationToken ct);

    /// <summary>Reports how far along the current stage is, measured in bytes of the file.</summary>
    /// <remarks>
    /// Bytes and not rows, and the names say so now: the row count is not known until the file
    /// has been read, so a byte-based bar can run from zero to a hundred without ever revising
    /// its own estimate. It was always bytes; the columns simply used to be called rows, and the
    /// Import Center printed "274,726,912 of 335,100,378" for a file it also described as having
    /// 7,066,140 rows.
    /// </remarks>
    Task ReportProgressAsync(
        long jobId, string stage, long bytesProcessed, long? bytesExpected, CancellationToken ct);

    /// <summary>Appends one entry to the job's timeline.</summary>
    /// <remarks>
    /// The timeline is written by the worker as it works, not reconstructed from status changes
    /// afterwards. A reconstructed timeline can only ever show what the schema recorded; a
    /// written one shows what happened, including the parts that did not change any status.
    /// </remarks>
    Task AppendEventAsync(
        long jobId, string severity, string? stage, string message, object? detail, CancellationToken ct);

    /// <summary>
    /// Matches a file's header against the known column contracts for its source.
    /// </summary>
    /// <remarks>
    /// Registers a new version when the header is a compatible extension, so schema evolution is
    /// recorded as it happens rather than discovered later from a column that stopped being
    /// populated. Attaches the resolved version to the job either way.
    /// </remarks>
    Task<SchemaResolution> ResolveSchemaAsync(
        long jobId, string sourceCode, IReadOnlyList<string> columns, CancellationToken ct);

    /// <summary>Records validation failures, grouped by rule, with a capped sample per group.</summary>
    Task RecordQuarantineAsync(
        long jobId, IReadOnlyList<QuarantineWrite> groups, CancellationToken ct);

    /// <summary>
    /// Marks a job finished and, for a source that replaces by date, makes it the effective
    /// import for its business date.
    /// </summary>
    /// <remarks>
    /// Becoming effective and demoting the previous holder happen in one transaction, so a
    /// business date never has two effective imports and never has none. A partial unique index
    /// enforces the first half of that even if this method is wrong.
    /// </remarks>
    /// <param name="jobId">The job.</param>
    /// <param name="status">How it ended.</param>
    /// <param name="counters">What it moved.</param>
    /// <param name="makeEffective">Whether it becomes the effective import for its day.</param>
    /// <param name="businessDate">
    /// The day the job turned out to describe, when the processor determined it. A file uploaded
    /// through the browser is enqueued without one; this is where it gets recorded.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task CompleteAsync(
        long jobId,
        ImportJobStatus status,
        ImportCounters counters,
        bool makeEffective,
        DateOnly? businessDate,
        CancellationToken ct);

    /// <summary>
    /// Records a failure and decides what happens next: another attempt after a backoff, or a
    /// terminal failure once the attempts are used up.
    /// </summary>
    /// <returns><see langword="true"/> when the job was scheduled for another attempt.</returns>
    Task<bool> FailAsync(
        long jobId, string errorSummary, bool isRetryable, TimeSpan retryDelay, CancellationToken ct);

    /// <summary>Marks a job cancelled after a worker noticed the cancellation request.</summary>
    Task MarkCancelledAsync(long jobId, CancellationToken ct);

    /// <summary>Whether an operator has asked for this job to stop.</summary>
    Task<bool> IsCancellationRequestedAsync(long jobId, CancellationToken ct);

    /// <summary>
    /// Returns jobs whose lease has expired to the queue.
    /// </summary>
    /// <returns>How many jobs were recovered.</returns>
    Task<int> RecoverExpiredLeasesAsync(CancellationToken ct);

    // -----------------------------------------------------------------------
    // Reads
    // -----------------------------------------------------------------------

    /// <summary>A page of import history, newest first.</summary>
    Task<IReadOnlyList<ImportJobSummary>> ListJobsAsync(
        ImportHistoryFilter filter, int limit, int offset, CancellationToken ct);

    /// <summary>How many jobs match a filter.</summary>
    Task<long> CountJobsAsync(ImportHistoryFilter filter, CancellationToken ct);

    /// <summary>Everything the detail page needs, or <see langword="null"/> when no such job.</summary>
    Task<ImportJobDetail?> GetJobAsync(long jobId, CancellationToken ct);

    /// <summary>Retained example rows for one quarantine group.</summary>
    Task<IReadOnlyList<QuarantineSample>> GetQuarantineSamplesAsync(
        long summaryId, int limit, CancellationToken ct);

    /// <summary>How current each enabled source is.</summary>
    Task<IReadOnlyList<SourceFreshness>> GetFreshnessAsync(DateOnly today, CancellationToken ct);

    /// <summary>Queue depth, running jobs, stale leases.</summary>
    Task<WorkerHealth> GetWorkerHealthAsync(CancellationToken ct);

    // -----------------------------------------------------------------------
    // Operator actions
    // -----------------------------------------------------------------------

    /// <summary>
    /// Asks a running job to stop. Does not stop it directly: the worker checks the flag at
    /// stage boundaries, so a cancellation never interrupts a write halfway through.
    /// </summary>
    Task<bool> RequestCancellationAsync(long jobId, string actor, CancellationToken ct);

    // -----------------------------------------------------------------------
    // TAC versions
    // -----------------------------------------------------------------------

    /// <summary>Records a loaded TAC version as READY for review.</summary>
    /// <remarks>
    /// READY rather than DRAFT: by the time this is called the worker has loaded, checked and
    /// diffed the version, so there is nothing left to do but decide. A DRAFT status would
    /// suggest work is still outstanding.
    /// </remarks>
    Task CreateTacVersionAsync(
        long jobId,
        int analyticsVersionId,
        string versionLabel,
        DateOnly? datasetDate,
        int rowCount,
        int? diffAgainstAnalyticsVersionId,
        TacVersionDiff? diff,
        CancellationToken ct);

    /// <summary>Every TAC version, newest first.</summary>
    Task<IReadOnlyList<TacVersion>> ListTacVersionsAsync(CancellationToken ct);

    /// <summary>
    /// Makes one version active and demotes the one it replaces.
    /// </summary>
    /// <returns>
    /// What changed, or <see langword="null"/> when there is no such version. The analytics
    /// version number comes back so the caller can point the analytics store at it.
    /// </returns>
    Task<TacActivationResult?> ActivateTacVersionAsync(
        long tacVersionId, string actor, CancellationToken ct);

    /// <summary>Undoes an activation whose analytics-side switch then failed.</summary>
    Task RevertTacActivationAsync(
        long tacVersionId, long? previouslyActiveId, string actor, string reason, CancellationToken ct);

    /// <summary>Records an action in the append-only audit log.</summary>
    Task WriteAuditAsync(
        string actor,
        string action,
        long? jobId,
        long? fileId,
        long? tacVersionId,
        string? correlationId,
        object? detail,
        CancellationToken ct);
}

/// <summary>One rule's worth of validation failures, ready to be recorded.</summary>
/// <param name="RuleCode">Stable code for the rule.</param>
/// <param name="ColumnName">Column at fault, when applicable.</param>
/// <param name="Severity">One of <c>warning</c> or <c>error</c>.</param>
/// <param name="OccurrenceCount">How many rows hit it.</param>
/// <param name="FirstRowNumber">Where the first one was.</param>
/// <param name="Message">What the rule checks.</param>
/// <param name="Samples">A capped set of example rows.</param>
public sealed record QuarantineWrite(
    string RuleCode,
    string? ColumnName,
    string Severity,
    long OccurrenceCount,
    long? FirstRowNumber,
    string Message,
    IReadOnlyList<QuarantineSample> Samples);
