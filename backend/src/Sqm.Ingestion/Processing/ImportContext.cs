using Sqm.Application.DataImport;

namespace Sqm.Ingestion.Processing;

/// <summary>
/// The worker's side of <see cref="IImportContext"/>: routes a processor's reports to the store.
/// </summary>
/// <remarks>
/// Progress is throttled here rather than in each processor. A bulk insert reports bytes read on
/// every buffer, which is thousands of times a second; writing each one to PostgreSQL would make
/// the progress bar the most expensive part of the import. One write a second is well past what
/// a human can read and far below what the database notices.
/// </remarks>
internal sealed class ImportContext(
    IImportJobRepository repository,
    IImportFileStore fileStore,
    ClaimedJob job) : IImportContext
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(1);

    private readonly System.Diagnostics.Stopwatch _sinceLastProgress =
        System.Diagnostics.Stopwatch.StartNew();

    private string _stage = ImportJobStatus.Validating.ToDatabaseValue();

    public async Task EnterStageAsync(ImportJobStatus stage, string message, CancellationToken ct)
    {
        _stage = stage.ToDatabaseValue();
        await repository.SetStageAsync(job.JobId, stage, ct).ConfigureAwait(false);
        await repository.AppendEventAsync(job.JobId, "info", _stage, message, null, ct)
            .ConfigureAwait(false);
    }

    public async Task ReportProgressAsync(long rowsProcessed, long? rowsExpected, CancellationToken ct)
    {
        if (_sinceLastProgress.Elapsed < ProgressInterval)
        {
            return;
        }

        _sinceLastProgress.Restart();
        await repository.ReportProgressAsync(job.JobId, _stage, rowsProcessed, rowsExpected, ct)
            .ConfigureAwait(false);
    }

    public Task NoteAsync(string severity, string message, object? detail, CancellationToken ct) =>
        repository.AppendEventAsync(job.JobId, severity, _stage, message, detail, ct);

    public Task QuarantineAsync(IReadOnlyList<QuarantineWrite> groups, CancellationToken ct) =>
        repository.RecordQuarantineAsync(job.JobId, groups, ct);

    public Task<bool> IsCancellationRequestedAsync(CancellationToken ct) =>
        repository.IsCancellationRequestedAsync(job.JobId, ct);

    public Task<Stream> OpenFileAsync(CancellationToken ct) =>
        fileStore.OpenReadAsync(job.StoredPath, ct);
}
