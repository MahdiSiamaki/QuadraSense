using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Application.DataImport;
using Sqm.Application.Risk;
using Sqm.Ingestion.Processing;

namespace Sqm.Ingestion.Tests;

/// <summary>
/// The risk snapshot's idle step: one piece of work per idle moment, never beside an import, never
/// publishing a run its inputs have moved past, and never retrying a broken chunk forever.
/// </summary>
/// <remarks>
/// Against the real <see cref="RiskSnapshot"/>, with the store stubbed: what is under test is the
/// decision and the calls that record it. The SQL is proven in Sqm.Integration.Tests.RiskSnapshotTests.
/// </remarks>
public sealed class RiskSnapshotTests
{
    private const ulong Today = 42;

    private sealed class Harness
    {
        public bool Deployed { get; init; } = true;

        public bool SqmRunning { get; init; }

        public bool Ready { get; init; } = true;

        public RiskRun? Running { get; set; }

        public RiskRun? Published { get; init; }

        public RiskRun? Failed { get; init; }

        public string? PublishRefusal { get; init; }

        public int ChunkFailures { get; set; }

        public List<string> Calls { get; } = [];

        public ManualClock Clock { get; } = new();

        public RiskSnapshot Build(RiskOptions? settings = null)
        {
            var store = Stub.Create<IRiskSnapshotStore>(Store);
            var repository = Stub.Create<IImportJobRepository>((m, args) =>
                m.Name == "IsSourceRunningAsync" ? Task.FromResult(SqmRunning && (string)args[0]! == "SQM") : Stub.Default(m));
            var options = Stub.Create<IOptionsMonitor<RiskOptions>>((m, _) =>
                m.Name == "get_CurrentValue" ? settings ?? new RiskOptions() : Stub.Default(m));

            return new RiskSnapshot(store, repository, options, Clock, NullLogger<RiskSnapshot>.Instance);
        }

        private object? Store(MethodInfo method, object?[] args)
        {
            switch (method.Name)
            {
                case "DeployedAsync":
                    return Task.FromResult(Deployed);
                case "ReadInputsAsync":
                    Calls.Add("read inputs");
                    return Task.FromResult(new RiskInputs(new DateOnly(2026, 9, 26), Today, 1, Ready, Ready ? null : "pending"));
                case "LatestRunAsync":
                    return Task.FromResult((string)args[0]! switch
                    {
                        "running" => Running,
                        "published" => Published,
                        "failed" => Failed,
                        _ => null,
                    });
                case "PlanRunAsync":
                    Calls.Add("plan");
                    return Task.FromResult(Run(Today));
                case "BuildChunkAsync":
                    Calls.Add($"build {RiskRun.Key((RiskTable)args[1]!, (int)args[2]!)}");
                    if (ChunkFailures > 0)
                    {
                        ChunkFailures--;
                        return Task.FromException<RiskRun>(new InvalidOperationException("memory limit"));
                    }

                    return Task.FromResult(Running!);
                case "TryPublishAsync":
                    Calls.Add("publish");
                    return Task.FromResult(PublishRefusal);
                case "AbandonAsync":
                    Calls.Add($"abandon: {args[1]}");
                    return Task.CompletedTask;
                case "FailAsync":
                    Calls.Add($"fail: {args[1]}");
                    return Task.CompletedTask;
                case "DropOldRunsAsync":
                    Calls.Add("drop old");
                    return Task.FromResult(0);
                default:
                    return Stub.Default(method);
            }
        }
    }

    private static RiskRun Run(ulong fingerprint, params string[] done) =>
        new(1, new DateOnly(2026, 9, 26), fingerprint, 1, "running", 2, done.ToHashSet(StringComparer.Ordinal), "", DateTimeOffset.UnixEpoch);

    private static readonly string[] AllChunks =
        ["SimWindow:0", "SimWindow:1", "ImeiWindow:0", "ImeiWindow:1", "ImeiLifetime:0", "ImeiLifetime:1", "Quality:0", "Quality:1"];

    private static Task<RiskSnapshotStep> Step(Harness h, bool force = false) =>
        h.Build().StepAsync(new RiskOptions(), force, CancellationToken.None);

    [Fact]
    public async Task Nothing_is_done_beside_an_SQM_import_or_while_the_history_is_incomplete()
    {
        var importing = new Harness { SqmRunning = true };
        Assert.Equal(RiskSnapshotStep.Waiting, await Step(importing));
        Assert.Empty(importing.Calls);

        var incomplete = new Harness { Ready = false };
        Assert.Equal(RiskSnapshotStep.Waiting, await Step(incomplete));
        Assert.Equal(["read inputs"], incomplete.Calls);

        Assert.Equal(RiskSnapshotStep.NotDeployed, await Step(new Harness { Deployed = false }));
    }

    [Fact]
    public async Task A_run_is_planned_only_when_the_published_one_is_stale_and_no_failure_stands()
    {
        Assert.Equal(RiskSnapshotStep.Current, await Step(new Harness { Published = Run(Today) }));

        var stale = new Harness { Published = Run(Today - 1) };
        Assert.Equal(RiskSnapshotStep.Planned, await Step(stale));
        Assert.Equal(["read inputs", "plan"], stale.Calls);

        // A run that failed on today's inputs is not retried until they change, unless asked.
        var failed = new Harness { Failed = Run(Today) };
        Assert.Equal(RiskSnapshotStep.Failed, await Step(failed));
        Assert.DoesNotContain("plan", failed.Calls);
        Assert.Equal(RiskSnapshotStep.Planned, await Step(failed, force: true));

        Assert.Equal(RiskSnapshotStep.Planned, await Step(new Harness { Published = Run(Today) }, force: true));
    }

    [Fact]
    public async Task A_running_run_overtaken_by_new_inputs_is_abandoned_not_finished()
    {
        var h = new Harness { Running = Run(Today - 1, "SimWindow:0") };
        Assert.Equal(RiskSnapshotStep.Abandoned, await Step(h));
        Assert.Equal(["read inputs", "abandon: its inputs changed before it was finished"], h.Calls);
    }

    [Fact]
    public async Task One_chunk_per_step_in_order_then_publication()
    {
        var h = new Harness { Running = Run(Today, "SimWindow:0") };
        Assert.Equal(RiskSnapshotStep.Built, await Step(h));
        Assert.Equal(["read inputs", "build SimWindow:1"], h.Calls);

        var complete = new Harness { Running = Run(Today, AllChunks) };
        Assert.Equal(RiskSnapshotStep.Published, await Step(complete));
        Assert.Equal(["read inputs", "publish", "drop old"], complete.Calls);

        var refused = new Harness { Running = Run(Today, AllChunks), PublishRefusal = "risk_sim_window holds 2 duplicate key(s)" };
        Assert.Equal(RiskSnapshotStep.Abandoned, await Step(refused));
        Assert.Equal(["read inputs", "publish", "abandon: risk_sim_window holds 2 duplicate key(s)"], refused.Calls);
    }

    [Fact]
    public async Task A_chunk_that_keeps_failing_fails_its_run_after_three_tries()
    {
        var h = new Harness { Running = Run(Today, "SimWindow:0"), ChunkFailures = RiskSnapshot.MaxAttempts };
        var snapshot = h.Build();
        var ct = CancellationToken.None;

        for (var attempt = 1; attempt < RiskSnapshot.MaxAttempts; attempt++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => snapshot.StepAsync(new RiskOptions(), false, ct));
        }

        Assert.Equal(RiskSnapshotStep.Failed, await snapshot.StepAsync(new RiskOptions(), false, ct));
        Assert.Equal("fail: SimWindow:1 failed 3 times: memory limit", h.Calls[^1]);
        Assert.Equal(RiskSnapshot.MaxAttempts, h.Calls.Count(c => c == "build SimWindow:1"));
    }

    [Fact]
    public async Task Nothing_to_do_is_not_asked_again_for_five_minutes_but_work_in_progress_is()
    {
        var current = new Harness { Published = Run(Today) };
        var snapshot = current.Build();

        await snapshot.RunAsync(CancellationToken.None);
        await snapshot.RunAsync(CancellationToken.None);
        Assert.Equal(["read inputs"], current.Calls);

        current.Clock.Advance(RiskSnapshot.QuietFor);
        await snapshot.RunAsync(CancellationToken.None);
        Assert.Equal(["read inputs", "read inputs"], current.Calls);

        var building = new Harness { Running = Run(Today) };
        var builder = building.Build();
        await builder.RunAsync(CancellationToken.None);
        await builder.RunAsync(CancellationToken.None);
        Assert.Equal(2, building.Calls.Count(c => c.StartsWith("build", StringComparison.Ordinal)));
    }

    /// <summary>A clock a test moves by hand.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public async Task Invalid_settings_build_nothing()
    {
        var h = new Harness { Running = Run(Today) };
        var settings = new RiskOptions();
        settings.Thresholds.HighDeviceCount30 = 2;   // below the floor of 6: values 3-5 were never stored

        await h.Build(settings).RunAsync(CancellationToken.None);
        Assert.Empty(h.Calls);

        await h.Build().RunAsync(CancellationToken.None);
        Assert.Equal(["read inputs", "build SimWindow:0"], h.Calls);
    }
}
