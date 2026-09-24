using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Sqm.Application.DataImport;
using Sqm.Ingestion.Processing;

namespace Sqm.Ingestion.Tests;

/// <summary>
/// The dashboard snapshot is rebuilt once per run of files, and can never be left owed.
/// </summary>
/// <remarks>
/// Against the real <see cref="DashboardSnapshot"/> and <see cref="MartRefresh"/>, with the
/// stores stubbed: what is under test is the decision - rebuild, defer, or settle - and the order
/// of the calls that record it, not the SQL.
/// </remarks>
public sealed class DashboardSnapshotTests
{
    private static readonly DateTimeOffset OwedAt = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public int Waiting { get; init; }

        public bool SourceRunning { get; init; }

        public DateTimeOffset? OwedSince { get; init; }

        public IReadOnlyList<string> Owed { get; init; } = [];

        public bool StatementsFail { get; init; }

        /// <summary>Every store call, in order, with its interesting argument.</summary>
        public List<string> Calls { get; } = [];

        public List<string> Notes { get; } = [];

        public DashboardSnapshot Build()
        {
            var analytics = Stub.Create<IAnalyticsIngestionStore>(Analytics);
            var repository = Stub.Create<IImportJobRepository>(Repository);

            return new DashboardSnapshot(
                analytics,
                repository,
                new MartRefresh(analytics, NullLogger<MartRefresh>.Instance)
                {
                    BetweenStatements = TimeSpan.Zero,
                    BetweenPasses = TimeSpan.Zero,
                },
                NullLogger<DashboardSnapshot>.Instance);
        }

        public IImportContext Context() => Stub.Create<IImportContext>((m, args) =>
        {
            if (m.Name == "NoteAsync")
            {
                Notes.Add((string)args[1]!);
            }

            return Stub.Default(m);
        });

        private object? Analytics(MethodInfo method, object?[] args)
        {
            switch (method.Name)
            {
                // The latest day is delivery 7.
                case "GetBusinessDatesAsync":
                    return Task.FromResult<IReadOnlyList<DateOnly>>([new DateOnly(2026, 8, 31)]);
                case "GetSequenceForDateAsync":
                    return Task.FromResult<int?>(7);
                case "ExecuteMartStatementAsync":
                    Calls.Add("mart statement");
                    return StatementsFail
                        ? Task.FromException(new InvalidOperationException("memory limit"))
                        : Task.CompletedTask;
                case "SetMartsReadyAsync":
                    Calls.Add($"marts ready {args[0]} {args[1]}");
                    return Task.CompletedTask;
                default:
                    return Stub.Default(method);
            }
        }

        private object? Repository(MethodInfo method, object?[] args)
        {
            switch (method.Name)
            {
                case "CountWaitingJobsAsync":
                    return Task.FromResult(Waiting);
                case "IsSourceRunningAsync":
                    return Task.FromResult(SourceRunning);
                case "GetDashboardOwedSinceAsync":
                    Calls.Add("read owed");
                    return Task.FromResult(OwedSince);
                case "ListDashboardOwedAsync":
                    return Task.FromResult(Owed);
                case "MarkDashboardOwedAsync":
                    Calls.Add($"owed {args[0]}");
                    return Task.CompletedTask;
                case "SettleDashboardOwedAsync":
                    Calls.Add($"settle {args[0]} {(DateTimeOffset)args[1]!:O}");
                    return Task.FromResult(true);
                default:
                    return Stub.Default(method);
            }
        }
    }

    private static readonly ClaimedJob Job = new(
        42, "SQM", 1, "stored", "daily_subs_device_sim_info_2026-05-08.csv", "sha",
        100, new DateOnly(2026, 5, 8), 1, 3, 0, null);

    [Fact]
    public async Task A_file_with_more_queued_behind_it_defers_the_rebuild_and_records_the_debt()
    {
        var run = new Harness { Waiting = 26 };

        await run.Build().AfterDayAsync(Job, run.Context(), CancellationToken.None);

        // Not one mart statement, and the snapshot is not withdrawn either: the dashboard keeps
        // the figures it had rather than falling back to an older delivery.
        Assert.DoesNotContain("mart statement", run.Calls);
        Assert.DoesNotContain(run.Calls, c => c.StartsWith("marts ready", StringComparison.Ordinal));
        Assert.Equal(["owed SQM"], run.Calls);
        Assert.Contains(run.Notes, n => n.Contains("26 more SQM file(s) are queued", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_last_file_of_a_run_rebuilds_once_and_settles_the_debt_it_saw()
    {
        var run = new Harness { Waiting = 0, OwedSince = OwedAt };

        await run.Build().AfterDayAsync(Job, run.Context(), CancellationToken.None);

        // The debt is read BEFORE the rebuild, and settled at that exact time AFTER it.
        Assert.Equal("read owed", run.Calls[0]);
        Assert.Equal("marts ready 7 False", run.Calls[1]);
        Assert.Contains("mart statement", run.Calls);
        Assert.Equal("marts ready 7 True", run.Calls[^2]);
        Assert.Equal($"settle SQM {OwedAt:O}", run.Calls[^1]);
    }

    [Fact]
    public async Task A_file_with_nothing_owed_and_nothing_queued_rebuilds_as_it_always_did()
    {
        var run = new Harness { Waiting = 0, OwedSince = null };

        await run.Build().AfterDayAsync(Job, run.Context(), CancellationToken.None);

        Assert.Equal("marts ready 7 True", run.Calls[^1]);
        Assert.DoesNotContain(run.Calls, c => c.StartsWith("settle", StringComparison.Ordinal));
        Assert.DoesNotContain(run.Calls, c => c.StartsWith("owed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_rebuild_that_fails_leaves_the_snapshot_owed_rather_than_forgotten()
    {
        var run = new Harness { Waiting = 0, OwedSince = OwedAt, StatementsFail = true };

        await run.Build().AfterDayAsync(Job, run.Context(), CancellationToken.None);

        Assert.DoesNotContain("marts ready 7 True", run.Calls);
        Assert.DoesNotContain(run.Calls, c => c.StartsWith("settle", StringComparison.Ordinal));
        Assert.Equal("owed SQM", run.Calls[^1]);
    }

    [Fact]
    public async Task An_idle_worker_rebuilds_what_a_stopped_run_left_owed()
    {
        var run = new Harness { Owed = ["SQM"], OwedSince = OwedAt, SourceRunning = false };

        await run.Build().RunAsync(CancellationToken.None);

        Assert.Contains("mart statement", run.Calls);
        Assert.Equal("marts ready 7 True", run.Calls[^2]);
        Assert.Equal($"settle SQM {OwedAt:O}", run.Calls[^1]);
    }

    [Fact]
    public async Task An_idle_worker_does_not_rebuild_under_a_job_another_worker_is_running()
    {
        var run = new Harness { Owed = ["SQM"], OwedSince = OwedAt, SourceRunning = true };

        await run.Build().RunAsync(CancellationToken.None);

        Assert.Empty(run.Calls);
    }

    [Fact]
    public async Task An_idle_worker_with_nothing_owed_touches_nothing()
    {
        var run = new Harness { Owed = [] };

        await run.Build().RunAsync(CancellationToken.None);

        Assert.Empty(run.Calls);
    }
}

/// <summary>The worker gives its idle tasks the moments when there is nothing to claim.</summary>
public sealed class ImportWorkerIdleTests
{
    private sealed class Signal(TaskCompletionSource ran) : IIdleTask
    {
        public Task RunAsync(CancellationToken ct)
        {
            ran.TrySetResult();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_worker_with_nothing_to_claim_runs_its_idle_tasks()
    {
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = Stub.Create<IImportJobRepository>((m, _) => m.Name == "ClaimNextAsync"
            ? Task.FromResult<ClaimedJob?>(null)
            : Stub.Default(m));

        var worker = new ImportWorker(
            repository,
            Stub.Create<Sqm.Application.DataImport.IImportFileStore>((m, _) => Stub.Default(m)),
            [],
            [new Signal(ran)],
            Microsoft.Extensions.Options.Options.Create(new ImportWorkerOptions
            {
                WorkerId = "test",
                PollInterval = TimeSpan.FromMilliseconds(20),
            }),
            NullLogger<ImportWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);
    }
}
