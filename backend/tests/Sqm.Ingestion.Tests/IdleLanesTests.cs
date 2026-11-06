using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Application.DataImport;
using Sqm.Ingestion.Processing;

namespace Sqm.Ingestion.Tests;

/// <summary>Lanes for a test: what a worker gets from an engine with so much to give.</summary>
internal static class Lanes
{
    private const long GiB = 1L << 30;

    /// <summary>Reads what it is told to, and counts.</summary>
    internal sealed class Reader(Func<EngineCapacity> read) : IEngineCapacityReader
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public Task<EngineCapacity> ReadCapacityAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _reads);
            return Task.FromResult(read());
        }
    }

    /// <summary>A small engine: room for one step.</summary>
    public static IdleLanes One() => Of(new EngineCapacity(GiB, 2, GiB));

    /// <summary>A large engine: room for as many as the worker allows.</summary>
    public static IdleLanes Two() => Of(new EngineCapacity(64 * GiB, 16, GiB));

    public static IdleLanes Of(
        EngineCapacity capacity, ImportWorkerOptions? options = null, TimeProvider? clock = null) =>
        Of(new Reader(() => capacity), options, clock);

    public static IdleLanes Of(
        IEngineCapacityReader reader, ImportWorkerOptions? options = null, TimeProvider? clock = null) =>
        new(reader, Options.Create(options ?? new ImportWorkerOptions()), clock ?? TimeProvider.System,
            NullLogger<IdleLanes>.Instance);
}

/// <summary>The number of lanes comes from what the engine says, and falls back to one.</summary>
public sealed class IdleLanesCountTests
{
    private const long GiB = 1L << 30;

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 6, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [Theory]
    // The real readings of 2026-11-06.
    [InlineData(5_583_457_484, 12, 1_178_910_709, 2)] // the laptop
    [InlineData(6_871_947_673, 6, 1_708_270_289, 2)] // the department VM
    // The VM before any run to learn from: its 2.5 GB per-statement limit is all there is to go by.
    [InlineData(6_871_947_673, 6, 2_500_000_000, 1)]
    // A small engine, and a single core however much memory.
    [InlineData(2 * GiB, 4, 1_700_000_000, 1)]
    [InlineData(64 * GiB, 1, GiB, 1)]
    // Cores limit a large machine before memory does.
    [InlineData(64 * GiB, 8, GiB, 4)]
    // Nothing known about a step is never a licence.
    [InlineData(64 * GiB, 16, 0, 1)]
    public void The_engine_decides_how_many_steps_fit(long memory, int cores, long peak, int lanes) =>
        Assert.Equal(lanes, new EngineCapacity(memory, cores, peak).Lanes);

    [Fact]
    public async Task No_more_lanes_than_the_worker_allows()
    {
        var lanes = Lanes.Of(new EngineCapacity(64 * GiB, 16, GiB), new ImportWorkerOptions { MaxIdleLanes = 3 });

        Assert.Equal(3, await lanes.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task One_allowed_turns_lanes_off_without_asking_the_engine()
    {
        var reader = new Lanes.Reader(() => new EngineCapacity(64 * GiB, 16, GiB));
        var lanes = Lanes.Of(reader, new ImportWorkerOptions { MaxIdleLanes = 1 });

        Assert.Equal(1, await lanes.CountAsync(CancellationToken.None));
        Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public async Task A_reading_stands_until_it_is_old()
    {
        var clock = new ManualClock();
        var reader = new Lanes.Reader(() => new EngineCapacity(64 * GiB, 16, GiB));
        var lanes = Lanes.Of(reader, clock: clock);

        for (var i = 0; i < 5; i++)
        {
            await lanes.CountAsync(CancellationToken.None);
        }

        Assert.Equal(1, reader.Reads);

        clock.Advance(IdleLanes.ReadEvery);
        await lanes.CountAsync(CancellationToken.None);

        Assert.Equal(2, reader.Reads);
    }

    [Fact]
    public async Task An_engine_that_cannot_be_read_gets_one_lane_until_it_can()
    {
        var clock = new ManualClock();
        var reachable = false;
        var reader = new Lanes.Reader(() => reachable
            ? new EngineCapacity(64 * GiB, 16, GiB)
            : throw new HttpRequestException("no route"));
        var lanes = Lanes.Of(reader, clock: clock);

        Assert.Equal(1, await lanes.CountAsync(CancellationToken.None));

        reachable = true;
        clock.Advance(IdleLanes.ReadEvery);

        Assert.Equal(2, await lanes.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_failed_step_leaves_one_lane_until_the_brake_lets_go()
    {
        var clock = new ManualClock();
        var options = new ImportWorkerOptions { IdleBrake = TimeSpan.FromMinutes(30) };
        var lanes = Lanes.Of(new EngineCapacity(64 * GiB, 16, GiB), options, clock);

        Assert.Equal(2, await lanes.CountAsync(CancellationToken.None));

        lanes.StepFailed();
        Assert.Equal(1, await lanes.CountAsync(CancellationToken.None));

        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(1, await lanes.CountAsync(CancellationToken.None));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2, await lanes.CountAsync(CancellationToken.None));
    }
}

/// <summary>
/// The worker's idle round: what does not conflict runs at once, and nothing runs beside a job.
/// </summary>
/// <remarks>
/// Risk and Planning took turns on one lane, a short step waiting behind a long one, and the worker
/// slept its poll interval after every round: half an hour of a 384-step run.
/// </remarks>
public sealed class IdleLanesTests
{
    private static readonly ClaimedJob Job = new(
        7, "SQM", 1, "stored", "daily_subs_device_sim_info_2026-10-24.csv", "sha",
        100, new DateOnly(2026, 10, 24), 1, 3, 0, null);

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>Long enough for what must not happen to have happened.</summary>
    private static readonly TimeSpan AWhile = TimeSpan.FromMilliseconds(300);

    private sealed class Idle(Func<int, CancellationToken, Task<bool>> step, bool alone = false) : IIdleTask
    {
        private int _steps;

        public int Steps => Volatile.Read(ref _steps);

        public bool RunsAlone => alone;

        public Task<bool> RunAsync(CancellationToken ct) => step(Interlocked.Increment(ref _steps), ct);
    }

    private sealed class Processor(Action started) : IImportProcessor
    {
        public string SourceCode => "SQM";

        public Task<ImportOutcome> ProcessAsync(ClaimedJob job, IImportContext context, CancellationToken ct)
        {
            started();
            return Task.FromResult(new ImportOutcome(ImportJobStatus.Completed, new ImportCounters(), true, "done"));
        }
    }

    /// <summary>A worker with nothing to claim, unless it is told otherwise.</summary>
    private static ImportWorker Worker(
        IEnumerable<IIdleTask> tasks,
        IdleLanes lanes,
        TimeSpan? poll = null,
        TimeSpan? round = null,
        Func<ClaimedJob?>? claim = null,
        IImportProcessor? processor = null) =>
        new(
            Stub.Create<IImportJobRepository>((m, _) => m.Name switch
            {
                "ClaimNextAsync" => Task.FromResult(claim?.Invoke()),
                "RenewLeaseAsync" or "CompleteAsync" => Task.FromResult(true),
                _ => Stub.Default(m),
            }),
            Stub.Create<IImportFileStore>((m, _) =>
                m.Name == "IsReachableAsync" ? Task.FromResult(true) : Stub.Default(m)),
            processor is null ? [] : [processor],
            tasks,
            lanes,
            Options.Create(new ImportWorkerOptions
            {
                WorkerId = "test",
                PollInterval = poll ?? TimeSpan.FromMilliseconds(20),
                IdleRound = round ?? TimeSpan.FromMilliseconds(50),
                CancelPollInterval = TimeSpan.FromMilliseconds(20),
            }),
            NullLogger<ImportWorker>.Instance);

    [Fact]
    public async Task Two_tasks_run_at_once_when_the_engine_has_room()
    {
        var inside = 0;
        var together = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<bool> Step(int _, CancellationToken ct)
        {
            if (Interlocked.Increment(ref inside) == 2)
            {
                together.TrySetResult();
            }

            await Task.WhenAny(together.Task, Task.Delay(AWhile, ct)).ConfigureAwait(false);
            Interlocked.Decrement(ref inside);
            return false;
        }

        var worker = Worker([new Idle(Step), new Idle(Step)], Lanes.Two());

        await worker.StartAsync(CancellationToken.None);
        await together.Task.WaitAsync(Patience);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task One_lane_takes_the_tasks_in_turn()
    {
        var inside = 0;
        var most = 0;
        var enough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<bool> Step(int steps, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref inside);
            InterlockedMax(ref most, now);
            await Task.Delay(2, ct).ConfigureAwait(false);
            Interlocked.Decrement(ref inside);

            if (steps >= 6)
            {
                enough.TrySetResult();
            }

            return true;
        }

        var first = new Idle(Step);
        var second = new Idle(Step);
        var worker = Worker([first, second], Lanes.One());

        await worker.StartAsync(CancellationToken.None);
        await enough.Task.WaitAsync(Patience);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, Volatile.Read(ref most));
        Assert.InRange(Math.Abs(first.Steps - second.Steps), 0, 1);
    }

    [Fact]
    public async Task A_lane_goes_on_stepping_while_the_other_is_in_a_long_step()
    {
        // The reason for lanes: a 9 s Risk step does not wait for a 60 s Planning one.
        var shortSteps = new Idle((_, _) => Task.FromResult(true));
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var longStep = new Idle(async (_, ct) =>
        {
            while (shortSteps.Steps < 3)
            {
                await Task.Delay(1, ct).ConfigureAwait(false);
            }

            finished.TrySetResult();
            return false;
        });

        var worker = Worker([longStep, shortSteps], Lanes.Two(), round: TimeSpan.FromHours(1));

        await worker.StartAsync(CancellationToken.None);
        await finished.Task.WaitAsync(Patience);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_stopping_worker_leaves_a_lane_between_steps()
    {
        // A task that never looks at the token must not keep the worker for the rest of the round.
        var stepping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = new Idle((_, _) =>
        {
            stepping.TrySetResult();
            return Task.FromResult(true);
        });

        var worker = Worker([task, new Idle((_, _) => Task.FromResult(false))], Lanes.Two(), round: TimeSpan.FromHours(1));

        await worker.StartAsync(CancellationToken.None);
        await stepping.Task.WaitAsync(Patience);

        await worker.StopAsync(CancellationToken.None).WaitAsync(Patience);
    }

    [Fact]
    public async Task What_runs_alone_goes_first_and_has_nothing_beside_it()
    {
        var alone = 0;
        var beside = 0;
        var overlapped = 0;
        var enough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var rebuild = new Idle(async (_, ct) =>
        {
            Interlocked.Increment(ref alone);
            if (Volatile.Read(ref beside) > 0)
            {
                Interlocked.Increment(ref overlapped);
            }

            await Task.Delay(5, ct).ConfigureAwait(false);
            Interlocked.Decrement(ref alone);
            return false;
        }, alone: true);

        async Task<bool> Step(int steps, CancellationToken ct)
        {
            Interlocked.Increment(ref beside);
            if (Volatile.Read(ref alone) > 0)
            {
                Interlocked.Increment(ref overlapped);
            }

            await Task.Delay(5, ct).ConfigureAwait(false);
            Interlocked.Decrement(ref beside);

            if (steps >= 4)
            {
                enough.TrySetResult();
            }

            return false;
        }

        var worker = Worker([new Idle(Step), rebuild, new Idle(Step)], Lanes.Two());

        await worker.StartAsync(CancellationToken.None);
        await enough.Task.WaitAsync(Patience);
        await worker.StopAsync(CancellationToken.None);

        Assert.True(rebuild.Steps >= 4);
        Assert.Equal(0, Volatile.Read(ref overlapped));
    }

    [Fact]
    public async Task A_job_gets_through_tasks_that_always_have_work_and_nothing_runs_beside_it()
    {
        var inside = 0;
        var claims = 0;
        var besideTheJob = -1;
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<bool> Step(int _, CancellationToken ct)
        {
            Interlocked.Increment(ref inside);
            await Task.Delay(5, ct).ConfigureAwait(false);
            Interlocked.Decrement(ref inside);
            return true;
        }

        var worker = Worker(
            [new Idle(Step), new Idle(Step)],
            Lanes.Two(),
            claim: () => Interlocked.Increment(ref claims) == 3 ? Job : null,
            processor: new Processor(() =>
            {
                Volatile.Write(ref besideTheJob, Volatile.Read(ref inside));
                processed.TrySetResult();
            }));

        await worker.StartAsync(CancellationToken.None);
        await processed.Task.WaitAsync(Patience);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, Volatile.Read(ref besideTheJob));
    }

    [Fact]
    public async Task Steps_go_on_without_waiting_for_the_poll_interval()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = new Idle((steps, _) =>
        {
            if (steps >= 5)
            {
                done.TrySetResult();
            }

            return Task.FromResult(steps < 5);
        });

        var worker = Worker([task], Lanes.One(), poll: TimeSpan.FromHours(1));

        await worker.StartAsync(CancellationToken.None);
        await done.Task.WaitAsync(Patience);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task With_nothing_to_do_the_worker_waits_its_poll_interval()
    {
        var task = new Idle((_, _) => Task.FromResult(false));
        var worker = Worker([task], Lanes.One(), poll: TimeSpan.FromHours(1));

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(AWhile);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, task.Steps);
    }

    [Fact]
    public async Task A_step_that_fails_brakes_the_lanes_and_the_other_task_goes_on()
    {
        var lanes = Lanes.Two();
        var goneOn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var failing = new Idle((steps, _) => steps == 1
            ? throw new InvalidOperationException("memory limit exceeded")
            : Task.FromResult(false));

        var other = new Idle((steps, _) =>
        {
            if (steps >= 3)
            {
                goneOn.TrySetResult();
            }

            return Task.FromResult(false);
        });

        var worker = Worker([failing, other], lanes);

        await worker.StartAsync(CancellationToken.None);
        await goneOn.Task.WaitAsync(Patience);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, await lanes.CountAsync(CancellationToken.None));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref target);
            if (value <= seen)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, seen) != seen);
    }
}
