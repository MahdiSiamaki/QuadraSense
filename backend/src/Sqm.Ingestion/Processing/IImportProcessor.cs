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
    /// Cancelled when the process is shutting down, the worker has lost its lease, or an operator
    /// cancelled the job (<see cref="IImportContext.CancelledByOperator"/> tells which). Only in
    /// the last case does the processor remove what it had loaded: after a shutdown or a lost
    /// lease the job runs again, and its next attempt clears the day itself.
    /// </param>
    Task<ImportOutcome> ProcessAsync(ClaimedJob job, IImportContext context, CancellationToken ct);
}

/// <summary>What a processor reports back while it works.</summary>
public interface IImportContext
{
    /// <summary>Moves the job to a new stage and records it on the timeline.</summary>
    Task EnterStageAsync(ImportJobStatus stage, string message, CancellationToken ct);

    /// <summary>
    /// Moves the job into the stage where stopping would leave work half-done - unless a cancel
    /// came first. From here on a cancel is refused.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when a cancel came first: the stage is not entered, and the
    /// processor removes what it loaded and returns <see cref="ImportJobStatus.Cancelled"/>.
    /// </returns>
    Task<bool> EnterStageUnlessCancelledAsync(ImportJobStatus stage, string message, CancellationToken ct);

    /// <summary>
    /// True once an operator's cancel has stopped this job - and only that, not a shutdown or a
    /// lost lease, after which the job runs again and must find its partial day still there.
    /// </summary>
    bool CancelledByOperator { get; }

    /// <summary>Reports progress within the current stage.</summary>
    Task ReportProgressAsync(long bytesProcessed, long? bytesExpected, CancellationToken ct);

    /// <summary>
    /// Reports which step of a stage that reads no file is starting - "3 of 7: updating the binding
    /// history" - so the page says what is happening instead of sweeping for half an hour.
    /// </summary>
    Task ReportStepAsync(string stepName, int stepIndex, int stepCount, CancellationToken ct);

    /// <summary>Adds a line to the job's timeline.</summary>
    Task NoteAsync(string severity, string message, object? detail, CancellationToken ct);

    /// <summary>Records validation failures, grouped by rule.</summary>
    Task QuarantineAsync(IReadOnlyList<QuarantineWrite> groups, CancellationToken ct);

    /// <summary>
    /// True when an operator has asked the job to stop. The worker also watches this and cancels
    /// the processor's token, so it is only worth asking where nothing is running to be cancelled.
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
/// <param name="BusinessDate">
/// The day the file turned out to describe, when the processor determined it. A file
/// uploaded through the browser is enqueued without one, and this is what gets it recorded
/// against the job - which is what makes the freshness card and "effective import for this
/// day" correct for browser uploads.
/// </param>
public sealed record ImportOutcome(
    ImportJobStatus Status,
    ImportCounters Counters,
    bool MakeEffective,
    string Message,
    DateOnly? BusinessDate = null);
