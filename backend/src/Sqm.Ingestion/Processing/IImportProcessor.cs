using Sqm.Application.DataImport;

namespace Sqm.Ingestion.Processing;

/// <summary>Knows how to import one kind of file.</summary>
/// <remarks>
/// One implementation per data source. The worker owns the parts that are the same for every
/// source - claiming, leasing, retrying, reporting - and a processor owns only what is specific
/// to its file format. Adding a third source should mean writing a processor, not touching the
/// worker.
/// </remarks>
public interface IImportProcessor
{
    /// <summary>The <c>imports.data_source</c> code this processor handles.</summary>
    string SourceCode { get; }

    /// <summary>Imports one file.</summary>
    /// <param name="job">The claimed job.</param>
    /// <param name="context">How to report progress, events and quarantine back to the platform.</param>
    /// <param name="ct">
    /// Cancelled when the process is shutting down or the worker has lost its lease. A processor
    /// should observe it at stage boundaries rather than mid-write.
    /// </param>
    Task<ImportOutcome> ProcessAsync(ClaimedJob job, IImportContext context, CancellationToken ct);
}

/// <summary>What a processor reports back while it works.</summary>
public interface IImportContext
{
    /// <summary>Moves the job to a new stage and records it on the timeline.</summary>
    Task EnterStageAsync(ImportJobStatus stage, string message, CancellationToken ct);

    /// <summary>Reports progress within the current stage.</summary>
    Task ReportProgressAsync(long rowsProcessed, long? rowsExpected, CancellationToken ct);

    /// <summary>Adds a line to the job's timeline.</summary>
    Task NoteAsync(string severity, string message, object? detail, CancellationToken ct);

    /// <summary>Records validation failures, grouped by rule.</summary>
    Task QuarantineAsync(IReadOnlyList<QuarantineWrite> groups, CancellationToken ct);

    /// <summary>
    /// True when an operator has asked the job to stop. Checked at stage boundaries, so
    /// cancelling never leaves a half-written day.
    /// </summary>
    Task<bool> IsCancellationRequestedAsync(CancellationToken ct);

    /// <summary>Opens the file this job is importing.</summary>
    Task<Stream> OpenFileAsync(CancellationToken ct);
}

/// <summary>The result of an import.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Counters">Row counts.</param>
/// <param name="MakeEffective">
/// Whether this job's data should become the effective import for its business date. False for a
/// source that appends rather than replaces, and false for a run that wrote nothing.
/// </param>
/// <param name="Message">A sentence for the timeline and the history list.</param>
public sealed record ImportOutcome(
    ImportJobStatus Status,
    ImportCounters Counters,
    bool MakeEffective,
    string Message);
