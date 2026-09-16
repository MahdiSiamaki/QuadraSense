namespace Sqm.Application.DataImport;

/// <summary>A registered file, whether it was just stored or already known.</summary>
/// <param name="FileId">Primary key in <c>imports.import_file</c>.</param>
/// <param name="Sha256">Content hash.</param>
/// <param name="IsNew">
/// <see langword="false"/> when a file with this content hash was already registered for this
/// source. The caller then has a decision to make rather than an error to report.
/// </param>
/// <param name="ExistingJobId">
/// When <paramref name="IsNew"/> is <see langword="false"/>, the most recent job for that file.
/// </param>
/// <param name="ExistingOriginalName">The name the earlier copy arrived under, which may differ.</param>
public sealed record RegisteredFile(
    long FileId,
    string Sha256,
    bool IsNew,
    long? ExistingJobId,
    string? ExistingOriginalName);

/// <summary>A job handed to a worker, with everything needed to process it.</summary>
/// <param name="JobId">Job primary key.</param>
/// <param name="SourceCode">Data source, e.g. <c>SQM</c> or <c>TAC</c>.</param>
/// <param name="FileId">The file being imported.</param>
/// <param name="StoredPath">Where the bytes are.</param>
/// <param name="OriginalFileName">The name it arrived under. Business dates are read from it.</param>
/// <param name="Sha256">Content hash, carried so the worker can verify before reading.</param>
/// <param name="FileBytes">Size, for progress estimation.</param>
/// <param name="BusinessDate">The day the file describes, when the source dates its files.</param>
/// <param name="Attempt">Which attempt this is, starting at 1.</param>
/// <param name="MaxAttempts">How many attempts the job is allowed.</param>
/// <param name="Priority">Higher runs first.</param>
/// <param name="ReprocessOfJobId">Set when this job re-runs an earlier one.</param>
public sealed record ClaimedJob(
    long JobId,
    string SourceCode,
    long FileId,
    string StoredPath,
    string OriginalFileName,
    string Sha256,
    long FileBytes,
    DateOnly? BusinessDate,
    int Attempt,
    int MaxAttempts,
    int Priority,
    long? ReprocessOfJobId);

/// <summary>Row counts a worker reports when a job finishes.</summary>
/// <param name="RowsInput">Rows read from the file, excluding the header.</param>
/// <param name="RowsValid">Rows that passed validation.</param>
/// <param name="RowsInvalid">Rows that failed validation and were quarantined.</param>
/// <param name="RowsInserted">Rows written to the analytics store.</param>
/// <param name="RowsUpdated">Rows that replaced an existing row.</param>
/// <param name="RowsDuplicate">Rows dropped as duplicates of another row in the same file.</param>
/// <param name="RowsRejected">Rows dropped outright.</param>
public sealed record ImportCounters(
    long RowsInput = 0,
    long RowsValid = 0,
    long RowsInvalid = 0,
    long RowsInserted = 0,
    long RowsUpdated = 0,
    long RowsDuplicate = 0,
    long RowsRejected = 0);

/// <summary>One entry in a job's timeline.</summary>
/// <param name="OccurredAt">When the worker recorded it.</param>
/// <param name="Severity">One of <c>info</c>, <c>warning</c>, <c>error</c>.</param>
/// <param name="Stage">The stage that produced it, when there was one.</param>
/// <param name="Message">Human-readable text.</param>
/// <param name="DetailJson">Structured detail, as JSON, when there is any.</param>
public sealed record ImportEvent(
    DateTimeOffset OccurredAt,
    string Severity,
    string? Stage,
    string Message,
    string? DetailJson);

/// <summary>A group of rows that failed the same rule.</summary>
/// <param name="SummaryId">Primary key, for fetching samples.</param>
/// <param name="RuleCode">Stable code for the rule, e.g. <c>IMEI_NOT_NUMERIC</c>.</param>
/// <param name="ColumnName">The column at fault, when the rule is column-specific.</param>
/// <param name="Severity">Whether the rows were warned about or rejected.</param>
/// <param name="OccurrenceCount">How many rows hit this rule.</param>
/// <param name="FirstRowNumber">Line number of the first offender, for locating it in the file.</param>
/// <param name="Message">What the rule checks, in plain words.</param>
public sealed record QuarantineGroup(
    long SummaryId,
    string RuleCode,
    string? ColumnName,
    string Severity,
    long OccurrenceCount,
    long? FirstRowNumber,
    string Message);

/// <summary>One retained example of a quarantined row.</summary>
/// <param name="RowNumber">Line number in the source file.</param>
/// <param name="RawLine">The line as it appeared, truncated if very long.</param>
/// <param name="OffendingValue">The specific value that failed, when identifiable.</param>
public sealed record QuarantineSample(long RowNumber, string RawLine, string? OffendingValue);

/// <summary>How far along a running job is.</summary>
/// <param name="Stage">Current stage.</param>
/// <param name="BytesProcessed">Bytes of the source file consumed so far.</param>
/// <param name="BytesExpected">The file size, which is what percent is measured against.</param>
/// <param name="Percent">Completion percentage, when it can be computed.</param>
/// <param name="UpdatedAt">When the worker last reported.</param>
public sealed record ImportProgress(
    string Stage,
    long BytesProcessed,
    long? BytesExpected,
    decimal? Percent,
    DateTimeOffset UpdatedAt);

/// <summary>A row in the import history list.</summary>
/// <param name="JobId">Job primary key.</param>
/// <param name="SourceCode">Data source.</param>
/// <param name="OriginalFileName">Name the file arrived under.</param>
/// <param name="FileBytes">File size.</param>
/// <param name="Status">Current status.</param>
/// <param name="BusinessDate">The day the file describes.</param>
/// <param name="Revision">Which revision of that business date this is.</param>
/// <param name="IsEffective">Whether this job's data is the one currently in effect for its day.</param>
/// <param name="Attempt">Attempt number.</param>
/// <param name="RowsInput">Rows read.</param>
/// <param name="RowsInserted">Rows written.</param>
/// <param name="RowsInvalid">Rows quarantined.</param>
/// <param name="WarningCount">Distinct warning rules triggered.</param>
/// <param name="ErrorCount">Distinct error rules triggered.</param>
/// <param name="CreatedAt">When the job was created.</param>
/// <param name="StartedAt">When a worker picked it up.</param>
/// <param name="FinishedAt">When it reached a terminal status.</param>
/// <param name="DurationMs">Wall-clock duration, when finished.</param>
/// <param name="CreatedBy">Who uploaded it.</param>
/// <param name="ErrorSummary">First line of the failure, when it failed.</param>
public sealed record ImportJobSummary(
    long JobId,
    string SourceCode,
    string OriginalFileName,
    long FileBytes,
    ImportJobStatus Status,
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
    string? ErrorSummary);

/// <summary>Everything the detail page shows about one job.</summary>
/// <param name="Summary">The history-row fields.</param>
/// <param name="FileId">The stored file, so a reprocess can queue the same bytes again.</param>
/// <param name="Sha256">Content hash of the file.</param>
/// <param name="StoredPath">Where the original is kept.</param>
/// <param name="IsBlobPresent">Whether the original bytes are still on disk.</param>
/// <param name="SchemaVersion">Which column contract the file matched.</param>
/// <param name="Progress">Live progress, for a running job.</param>
/// <param name="Events">The timeline.</param>
/// <param name="Quarantine">Validation failures, grouped by rule.</param>
/// <param name="SupersededByJobId">The later job that replaced this one, when there is one.</param>
/// <param name="SupersedesJobId">The earlier job this one replaced.</param>
/// <param name="ReprocessOfJobId">The job this one re-runs.</param>
public sealed record ImportJobDetail(
    ImportJobSummary Summary,
    long FileId,
    string Sha256,
    string StoredPath,
    bool IsBlobPresent,
    string? SchemaVersion,
    ImportProgress? Progress,
    IReadOnlyList<ImportEvent> Events,
    IReadOnlyList<QuarantineGroup> Quarantine,
    long? SupersededByJobId,
    long? SupersedesJobId,
    long? ReprocessOfJobId);

/// <summary>Filters for the import history list.</summary>
/// <param name="SourceCode">Restrict to one data source.</param>
/// <param name="Statuses">Restrict to these statuses.</param>
/// <param name="BusinessDateFrom">Inclusive lower bound on business date.</param>
/// <param name="BusinessDateTo">Inclusive upper bound on business date.</param>
/// <param name="FileNameContains">Substring match on the original file name.</param>
/// <param name="EffectiveOnly">Only jobs whose data is currently in effect.</param>
public sealed record ImportHistoryFilter(
    string? SourceCode = null,
    IReadOnlyList<ImportJobStatus>? Statuses = null,
    DateOnly? BusinessDateFrom = null,
    DateOnly? BusinessDateTo = null,
    string? FileNameContains = null,
    bool EffectiveOnly = false);

/// <summary>Status of a TAC dataset version.</summary>
public enum TacVersionStatus
{
    /// <summary>Created, not yet processed.</summary>
    Draft,

    /// <summary>Being parsed and diffed.</summary>
    Processing,

    /// <summary>Processed and diffed, waiting for a human to activate it.</summary>
    Ready,

    /// <summary>The version the application resolves TACs against. Exactly one at a time.</summary>
    Active,

    /// <summary>Was active; a later version replaced it.</summary>
    Superseded,

    /// <summary>Processing failed.</summary>
    Failed,
}

/// <summary>A TAC dataset version and its diff against the one before it.</summary>
/// <param name="Id">Primary key.</param>
/// <param name="JobId">The import job that produced it.</param>
/// <param name="VersionLabel">Human label, e.g. <c>v2026.09.01</c>.</param>
/// <param name="DatasetDate">The dataset's own date.</param>
/// <param name="Status">Lifecycle status.</param>
/// <param name="RowCount">Rows in this version.</param>
/// <param name="DiffAgainstId">Which version the counts below compare against.</param>
/// <param name="TacsAdded">TACs present here and not in the compared version.</param>
/// <param name="TacsUpdated">TACs present in both with at least one attribute changed.</param>
/// <param name="TacsRemoved">TACs present in the compared version and not here.</param>
/// <param name="TacsUnchanged">TACs identical in both.</param>
/// <param name="CreatedAt">When the version was created.</param>
/// <param name="ActivatedAt">When it was activated, if it ever was.</param>
/// <param name="ActivatedBy">Who activated it.</param>
/// <param name="SupersededAt">When it stopped being active.</param>
public sealed record TacVersion(
    long Id,
    long JobId,
    string VersionLabel,
    DateOnly? DatasetDate,
    TacVersionStatus Status,
    int? RowCount,
    long? DiffAgainstId,
    int? TacsAdded,
    int? TacsUpdated,
    int? TacsRemoved,
    int? TacsUnchanged,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ActivatedAt,
    string? ActivatedBy,
    DateTimeOffset? SupersededAt);

/// <summary>How current a data source is.</summary>
/// <param name="SourceCode">Data source.</param>
/// <param name="LatestBusinessDate">Newest business date that imported successfully.</param>
/// <param name="LatestImportedAt">When that import finished.</param>
/// <param name="DaysBehind">
/// Days between <paramref name="LatestBusinessDate"/> and today. Measured against the business
/// date, not the import time: a file imported an hour ago that describes last month is stale data
/// however recently it arrived.
/// </param>
/// <param name="MissingBusinessDates">Expected days with no effective import, newest first.</param>
/// <param name="FailedLast7Days">Jobs that failed in the last week.</param>
public sealed record SourceFreshness(
    string SourceCode,
    DateOnly? LatestBusinessDate,
    DateTimeOffset? LatestImportedAt,
    int? DaysBehind,
    IReadOnlyList<DateOnly> MissingBusinessDates,
    int FailedLast7Days);

/// <summary>Live view of the worker fleet.</summary>
/// <param name="Queued">Jobs waiting for a worker.</param>
/// <param name="Running">Jobs a worker currently holds.</param>
/// <param name="Retrying">Jobs failed and waiting on backoff.</param>
/// <param name="FailedLast24Hours">Jobs that gave up in the last day.</param>
/// <param name="OldestQueuedAt">When the longest-waiting job was queued.</param>
/// <param name="ActiveWorkers">
/// Workers that have polled recently, whether or not they hold a job.
/// </param>
/// <param name="StaleLeases">
/// Jobs whose lease has expired. Nonzero means a worker died; the recovery sweep will
/// return them to the queue.
/// </param>
/// <param name="WorkerHostedInApi">
/// True when a live worker shares a process with the API. Worth showing, because restarting the
/// API then stops the running import too.
/// </param>
/// <remarks>
/// <b>ActiveWorkers used to mean something narrower, and the difference is the whole point.</b>
/// It was <c>COUNT(DISTINCT worker_id) WHERE lease_expires_at &gt; now()</c> - workers holding a
/// live lease on a job. An idle worker, polling happily with nothing to do, holds no lease and
/// counted as zero.
/// <para>
/// So the two states an operator most needs to tell apart were displayed identically: "a worker
/// is running and there is nothing to do" and "no worker is running at all" both read
/// <c>0 workers</c>. A 319.6 MB file sat at Queued behind that zero, with nothing on the page
/// suggesting why. It now counts workers that have reported a heartbeat recently, which is the
/// question being asked.
/// </para>
/// </remarks>
public sealed record WorkerHealth(
    int Queued,
    int Running,
    int Retrying,
    int FailedLast24Hours,
    DateTimeOffset? OldestQueuedAt,
    int ActiveWorkers,
    int StaleLeases,
    bool WorkerHostedInApi = false)
{
    /// <summary>Work is waiting and nothing is alive to take it.</summary>
    /// <remarks>
    /// Neither number means anything alone. Queued jobs with live workers is a busy queue; no
    /// queued jobs and no workers is an idle system. This combination is the one that needs
    /// saying out loud.
    /// </remarks>
    public bool Stalled => Queued > 0 && ActiveWorkers == 0;
}

/// <summary>What an activation changed.</summary>
/// <param name="TacVersionId">The operational row that is now active.</param>
/// <param name="AnalyticsVersionId">The version number the analytics store must point at.</param>
/// <param name="VersionLabel">Its human label.</param>
/// <param name="PreviousTacVersionId">The row that was demoted, if any.</param>
/// <param name="PreviousVersionLabel">Its label.</param>
public sealed record TacActivationResult(
    long TacVersionId,
    int AnalyticsVersionId,
    string VersionLabel,
    long? PreviousTacVersionId,
    string? PreviousVersionLabel);
