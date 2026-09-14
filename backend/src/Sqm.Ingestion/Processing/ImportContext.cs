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
    private const long ProgressIntervalTicks = TimeSpan.TicksPerSecond;

    // Progress is reported from the stream's read path, which is not the thread that drives the
    // stages, so both of these are touched concurrently. A Stopwatch would not survive that; a
    // timestamp moved with CompareExchange does, and it also makes the throttle a real
    // test-and-set rather than a read followed by a write that another caller can slip between.
    private long _lastProgressTicks = DateTime.UtcNow.Ticks;

    private volatile string _stage = ImportJobStatus.Validating.ToDatabaseValue();

    public async Task EnterStageAsync(ImportJobStatus stage, string message, CancellationToken ct)
    {
        var label = stage.ToDatabaseValue();
        _stage = label;

        await repository.SetStageAsync(job.JobId, stage, ct).ConfigureAwait(false);
        await repository.AppendEventAsync(job.JobId, "info", label, message, null, ct)
            .ConfigureAwait(false);
    }

    public async Task ReportProgressAsync(long rowsProcessed, long? rowsExpected, CancellationToken ct)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastProgressTicks);

        if (now - last < ProgressIntervalTicks)
        {
            return;
        }

        // Only the caller that wins the exchange writes. Without it, a burst of concurrent
        // reports would all see the same stale timestamp and all write.
        if (Interlocked.CompareExchange(ref _lastProgressTicks, now, last) != last)
        {
            return;
        }

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
