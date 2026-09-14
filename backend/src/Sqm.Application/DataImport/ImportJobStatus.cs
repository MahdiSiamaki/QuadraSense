namespace Sqm.Application.DataImport;

/// <summary>
/// Where a job is in its life. Mirrors the <c>imports.job_status</c> enum in PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// The names are the same in both places on purpose: a status the operator reads in the UI, the
/// value in the database and the symbol in the code are one vocabulary, so nobody has to hold a
/// translation table in their head while reading a failed import.
/// </para>
/// <para>
/// The working stages between <see cref="Queued"/> and <see cref="Completed"/> are distinct
/// statuses rather than a single RUNNING with a stage field, because "failed while importing"
/// and "failed while validating" mean entirely different things operationally - the first may
/// have written rows, the second cannot have.
/// </para>
/// </remarks>
public enum ImportJobStatus
{
    /// <summary>The file is stored; nothing has been queued yet.</summary>
    Uploaded,

    /// <summary>Waiting for a worker.</summary>
    Queued,

    /// <summary>Structural checks: encoding, header, column contract.</summary>
    Validating,

    /// <summary>Reading rows out of the file.</summary>
    Parsing,

    /// <summary>Type conversion and canonicalisation.</summary>
    Normalizing,

    /// <summary>Row-level duplicate handling within the file.</summary>
    Deduplicating,

    /// <summary>Joining reference data.</summary>
    Enriching,

    /// <summary>Writing to the analytics store.</summary>
    Importing,

    /// <summary>Rebuilding the marts that depend on what was just written.</summary>
    Aggregating,

    /// <summary>Bookkeeping after a successful write.</summary>
    Finalizing,

    /// <summary>Everything landed.</summary>
    Completed,

    /// <summary>Landed, but some rows were quarantined.</summary>
    PartiallyCompleted,

    /// <summary>The identical file content was already imported. Not an error.</summary>
    Duplicate,

    /// <summary>Stopped by an error. See <c>error_summary</c> and the event timeline.</summary>
    Failed,

    /// <summary>Rejected wholesale by validation; nothing was written.</summary>
    Quarantined,

    /// <summary>Stopped because an operator asked for it.</summary>
    Cancelled,

    /// <summary>Failed, and waiting on the backoff before the next attempt.</summary>
    Retrying,
}

/// <summary>Helpers over <see cref="ImportJobStatus"/>.</summary>
public static class ImportJobStatusExtensions
{
    /// <summary>The PostgreSQL enum label, e.g. <c>PARTIALLY_COMPLETED</c>.</summary>
    public static string ToDatabaseValue(this ImportJobStatus status) => status switch
    {
        ImportJobStatus.PartiallyCompleted => "PARTIALLY_COMPLETED",
        _ => status.ToString().ToUpperInvariant(),
    };

    /// <summary>Parses the PostgreSQL enum label.</summary>
    public static ImportJobStatus FromDatabaseValue(string value) => value switch
    {
        "PARTIALLY_COMPLETED" => ImportJobStatus.PartiallyCompleted,
        _ => Enum.Parse<ImportJobStatus>(value, ignoreCase: true),
    };

    /// <summary>True when the job has stopped for good and a worker will not touch it again.</summary>
    public static bool IsTerminal(this ImportJobStatus status) => status is
        ImportJobStatus.Completed or
        ImportJobStatus.PartiallyCompleted or
        ImportJobStatus.Duplicate or
        ImportJobStatus.Failed or
        ImportJobStatus.Quarantined or
        ImportJobStatus.Cancelled;

    /// <summary>True when a worker currently holds the job.</summary>
    public static bool IsRunning(this ImportJobStatus status) => status is
        ImportJobStatus.Validating or
        ImportJobStatus.Parsing or
        ImportJobStatus.Normalizing or
        ImportJobStatus.Deduplicating or
        ImportJobStatus.Enriching or
        ImportJobStatus.Importing or
        ImportJobStatus.Aggregating or
        ImportJobStatus.Finalizing;
}
