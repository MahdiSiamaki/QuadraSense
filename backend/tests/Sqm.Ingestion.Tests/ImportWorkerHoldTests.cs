using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Application.DataImport;
using Sqm.Ingestion.Processing;

namespace Sqm.Ingestion.Tests;

/// <summary>A file store that leaves pauses the queue; it does not fail a day.</summary>
/// <remarks>
/// On the laptop the store is a USB disk. Twice it left while imports ran (2026-10-26 and
/// 2026-11-03), and each time the next day failed for good with a path not found, over a file that
/// was intact, and stopped every day behind it until someone queued it by hand.
/// </remarks>
public sealed class ImportWorkerHoldTests
{
    private static readonly ClaimedJob Job = new(
        7, "SQM", 1, "stored", "daily_subs_device_sim_info_2026-10-24.csv", "sha",
        100, new DateOnly(2026, 10, 24), 1, 3, 0, null);

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>Several polls at the test's 20 ms: long enough for a wrong claim to happen.</summary>
    private static readonly TimeSpan SeveralPolls = TimeSpan.FromMilliseconds(200);

    /// <summary>Does what it is told when it reaches the file.</summary>
    private sealed class Processor(Func<Exception?> outcome) : IImportProcessor
    {
        public string SourceCode => "SQM";

        public Task<ImportOutcome> ProcessAsync(ClaimedJob job, IImportContext context, CancellationToken ct) =>
            outcome() is { } failure
                ? Task.FromException<ImportOutcome>(failure)
                : Task.FromResult(new ImportOutcome(ImportJobStatus.Completed, new ImportCounters(), true, "done"));
    }

    private sealed class Counting : IIdleTask
    {
        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        public Task<bool> RunAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _runs);
            return Task.FromResult(false);
        }
    }

    private sealed class Run
    {
        private int _claims;
        private volatile bool _reachable;

        public Run(bool reachable, Func<Run, Exception?> outcome, IIdleTask? idle = null, bool offersJob = true)
        {
            _reachable = reachable;

            var repository = Stub.Create<IImportJobRepository>((m, args) =>
            {
                switch (m.Name)
                {
                    case "ClaimNextAsync":
                        var first = Interlocked.Increment(ref _claims) == 1;
                        Claimed.TrySetResult();
                        return Task.FromResult(first && offersJob ? Job : null);
                    case "RenewLeaseAsync":
                    case "CompleteAsync":
                        return Task.FromResult(true);
                    case "FailAsync":
                        Failed.TrySetResult(((string)args[1]!, (bool)args[2]!));
                        return Task.CompletedTask;
                    default:
                        return Stub.Default(m);
                }
            });

            Worker = new ImportWorker(
                repository,
                Stub.Create<IImportFileStore>((m, _) =>
                    m.Name == "IsReachableAsync" ? Task.FromResult(_reachable) : Stub.Default(m)),
                [new Processor(() => outcome(this))],
                idle is null ? [] : [idle],
                Lanes.One(),
                Options.Create(new ImportWorkerOptions
                {
                    WorkerId = "test",
                    PollInterval = TimeSpan.FromMilliseconds(20),
                    CancelPollInterval = TimeSpan.FromMilliseconds(20),
                }),
                NullLogger<ImportWorker>.Instance);
        }

        public ImportWorker Worker { get; }

        public int Claims => Volatile.Read(ref _claims);

        public TaskCompletionSource Claimed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<(string Summary, bool Retryable)> Failed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Reachable { set => _reachable = value; }
    }

    [Fact]
    public async Task Nothing_is_claimed_while_the_store_is_away_and_the_queue_resumes_when_it_returns()
    {
        var run = new Run(reachable: false, _ => null);

        await run.Worker.StartAsync(CancellationToken.None);
        await Task.Delay(SeveralPolls);

        Assert.Equal(0, run.Claims);

        // Nobody does anything: the disk comes back and the queue carries on.
        run.Reachable = true;
        await run.Claimed.Task.WaitAsync(Patience);
        await run.Worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Idle_tasks_wait_for_the_store_too()
    {
        // The queued days are about to change what an idle task computes, and the dashboard rebuild
        // cannot be interrupted: on 2026-11-05 a job waited 50 minutes behind one the next days made
        // stale.
        var idle = new Counting();
        var run = new Run(reachable: false, _ => null, idle, offersJob: false);

        await run.Worker.StartAsync(CancellationToken.None);
        await Task.Delay(SeveralPolls);

        Assert.Equal(0, idle.Runs);

        run.Reachable = true;
        await run.Claimed.Task.WaitAsync(Patience);
        await Task.Delay(SeveralPolls);
        await run.Worker.StopAsync(CancellationToken.None);

        Assert.True(idle.Runs > 0);
    }

    [Fact]
    public async Task A_file_missing_because_the_store_left_is_tried_again()
    {
        // The disk leaves after the claim and before the file is opened, where the hold cannot see
        // it. The message is what the laptop's 2026-11-03 failure said.
        var run = new Run(reachable: true, r =>
        {
            r.Reachable = false;
            return new DirectoryNotFoundException(@"Could not find a part of the path 'E:\job\stored'.");
        });

        await run.Worker.StartAsync(CancellationToken.None);
        var (summary, retryable) = await run.Failed.Task.WaitAsync(Patience);
        await run.Worker.StopAsync(CancellationToken.None);

        Assert.True(retryable);
        Assert.StartsWith("The file store could not be reached", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_missing_from_a_store_that_is_there_is_gone_for_good()
    {
        // The other half: holding must not turn a real loss into a job retried for ever.
        var run = new Run(reachable: true, _ => new FileNotFoundException("gone", "stored"));

        await run.Worker.StartAsync(CancellationToken.None);
        var (_, retryable) = await run.Failed.Task.WaitAsync(Patience);
        await run.Worker.StopAsync(CancellationToken.None);

        Assert.False(retryable);
    }

    [Fact]
    public async Task A_failure_that_says_nothing_about_a_file_is_classified_as_it_was()
    {
        // A rejected file stays rejected with the disk gone: its columns are still wrong.
        var run = new Run(reachable: true, r =>
        {
            r.Reachable = false;
            return new ImportRejectedException("The header is wrong.");
        });

        await run.Worker.StartAsync(CancellationToken.None);
        var (_, retryable) = await run.Failed.Task.WaitAsync(Patience);
        await run.Worker.StopAsync(CancellationToken.None);

        Assert.False(retryable);
    }
}
