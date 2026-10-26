using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Application.DataImport;
using Sqm.Ingestion.Processing;

namespace Sqm.Ingestion.Tests;

/// <summary>An operator's cancel reaches a running job, and a shutdown is not mistaken for one.</summary>
/// <remarks>
/// The flag used to be read at one place only - a daily file just after validation, a TAC file
/// never - so a cancel during the load was accepted and then ignored. The worker now reads it every
/// few seconds and cancels the processor's token; the processor is told whether that was the
/// operator, because only then does it remove what it loaded.
/// </remarks>
public sealed class ImportWorkerCancelTests
{
    private static readonly ClaimedJob Job = new(
        7, "SQM", 1, "stored", "daily_subs_device_sim_info_2026-06-15.csv", "sha",
        100, new DateOnly(2026, 6, 15), 1, 3, 0, null);

    /// <summary>A processor that works until stopped, and says how it was stopped.</summary>
    private sealed class LongLoad(bool cleansUp) : IImportProcessor
    {
        public string SourceCode => "SQM";

        public TaskCompletionSource<bool> Stopped { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ImportOutcome> ProcessAsync(ClaimedJob job, IImportContext context, CancellationToken ct)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("not reached");
            }
            catch (OperationCanceledException)
            {
                Stopped.TrySetResult(context.CancelledByOperator);
                if (cleansUp && context.CancelledByOperator)
                {
                    return new ImportOutcome(ImportJobStatus.Cancelled, new ImportCounters(), false, "removed");
                }

                throw;
            }
        }
    }

    private sealed class Run
    {
        private int _claims;

        public Run(bool cancelRequested, IImportProcessor processor)
        {
            Repository = Stub.Create<IImportJobRepository>((m, _) => m.Name switch
            {
                "ClaimNextAsync" => Task.FromResult(Interlocked.Increment(ref _claims) == 1 ? Job : null),
                "IsCancellationRequestedAsync" => Task.FromResult(cancelRequested),
                "RenewLeaseAsync" => Task.FromResult(true),
                "MarkCancelledAsync" => MarkCancelled(),
                "CompleteAsync" => Task.FromResult(true),
                _ => Stub.Default(m),
            });

            Worker = new ImportWorker(
                Repository,
                Stub.Create<IImportFileStore>((m, _) => Stub.Default(m)),
                [processor],
                [],
                Options.Create(new ImportWorkerOptions
                {
                    WorkerId = "test",
                    PollInterval = TimeSpan.FromMilliseconds(20),
                    CancelPollInterval = TimeSpan.FromMilliseconds(20),
                }),
                NullLogger<ImportWorker>.Instance);
        }

        public IImportJobRepository Repository { get; }

        public ImportWorker Worker { get; }

        public TaskCompletionSource MarkedCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Task<bool> MarkCancelled()
        {
            MarkedCancelled.TrySetResult();
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task An_operators_cancel_stops_a_running_job_and_says_it_was_the_operator()
    {
        var processor = new LongLoad(cleansUp: true);
        var run = new Run(cancelRequested: true, processor);

        await run.Worker.StartAsync(CancellationToken.None);
        var byOperator = await processor.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await run.MarkedCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await run.Worker.StopAsync(CancellationToken.None);

        Assert.True(byOperator);
    }

    [Fact]
    public async Task A_cancel_that_stops_a_processor_with_nothing_loaded_still_ends_cancelled()
    {
        // Validating: nothing to remove, so the processor lets the cancellation through.
        var processor = new LongLoad(cleansUp: false);
        var run = new Run(cancelRequested: true, processor);

        await run.Worker.StartAsync(CancellationToken.None);
        await run.MarkedCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await run.Worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_shutdown_is_not_an_operators_cancel()
    {
        var processor = new LongLoad(cleansUp: true);
        var run = new Run(cancelRequested: false, processor);

        await run.Worker.StartAsync(CancellationToken.None);
        await Task.Delay(200); // claimed, and several cancel checks gone by
        await run.Worker.StopAsync(CancellationToken.None);

        Assert.False(await processor.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(run.MarkedCancelled.Task.IsCompleted);
    }
}
