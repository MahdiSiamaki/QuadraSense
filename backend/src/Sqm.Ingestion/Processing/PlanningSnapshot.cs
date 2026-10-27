using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sqm.Application.DataImport;
using Sqm.Application.Planning;
using Sqm.Domain.Devices;

namespace Sqm.Ingestion.Processing;

/// <summary>What one step of the planning snapshot did.</summary>
internal enum PlanningSnapshotStep
{
    /// <summary>The snapshot tables do not exist (analytics migrations 029 and 030).</summary>
    NotDeployed,

    /// <summary>An SQM import is running, or something a run reads is incomplete; nothing was done.</summary>
    Waiting,

    /// <summary>The published run was built from today's inputs; nothing to do.</summary>
    Current,

    /// <summary>A run failed on these inputs; nothing is retried until they change.</summary>
    Failed,

    /// <summary>A new run was planned.</summary>
    Planned,

    /// <summary>One chunk was written.</summary>
    Built,

    /// <summary>The run was refused at publication, or overtaken by new inputs, and abandoned.</summary>
    Abandoned,

    /// <summary>The run was published.</summary>
    Published,
}

/// <summary>
/// Keeps the planning snapshot (analytics migration 030) current: one step per idle moment of the
/// worker, in the way and for the reasons <see cref="RiskSnapshot"/> does - one chunk at a time, never
/// beside an SQM import, abandoned when a new delivery overtakes it, quiet when there is nothing to do.
/// </summary>
/// <remarks>
/// A run describes one delivery's state, so it is planned only once the radio profiles of the active
/// GSMA version are parsed by the current parser; the task parses them first if they are not, as the
/// dashboard rebuild does.
/// </remarks>
internal sealed partial class PlanningSnapshot(
    IPlanningSnapshotStore store,
    ITacVersionStore tacStore,
    IImportJobRepository repository,
    IOptionsMonitor<PlanningOptions> options,
    TimeProvider clock,
    ILogger<PlanningSnapshot> logger) : IIdleTask
{
    /// <summary>Consecutive failures of one chunk before its run is marked failed.</summary>
    internal const int MaxAttempts = 3;

    /// <summary>How long nothing to do stays the answer before the inputs are read again.</summary>
    internal static readonly TimeSpan QuietFor = TimeSpan.FromMinutes(5);

    private const string SqmSource = "SQM";

    private DateTimeOffset _quietUntil = DateTimeOffset.MinValue;
    private string? _failingChunk;
    private int _failures;
    private string? _reportedProblems;

    [LoggerMessage(EventId = 3530, Level = LogLevel.Warning,
        Message = "Planning snapshot not built: the Planning settings are invalid. {Problems}")]
    private partial void LogInvalid(string problems);

    [LoggerMessage(EventId = 3531, Level = LogLevel.Warning,
        Message = "Planning snapshot run {RunId}: {Chunk} failed (attempt {Attempt} of {MaxAttempts})")]
    private partial void LogChunkFailed(Exception exception, ulong runId, string chunk, int attempt, int maxAttempts);

    /// <inheritdoc />
    public async Task RunAsync(CancellationToken ct)
    {
        var settings = options.CurrentValue;
        var problems = settings.Problems();
        if (problems.Count > 0)
        {
            var text = string.Join(" ", problems);
            if (!string.Equals(text, _reportedProblems, StringComparison.Ordinal))
            {
                _reportedProblems = text;
                LogInvalid(text);
            }

            return;
        }

        _reportedProblems = null;
        if (clock.GetUtcNow() < _quietUntil)
        {
            return;
        }

        var step = await StepAsync(settings, force: false, ct).ConfigureAwait(false);
        if (step is PlanningSnapshotStep.Current or PlanningSnapshotStep.Waiting or PlanningSnapshotStep.Failed or PlanningSnapshotStep.NotDeployed)
        {
            _quietUntil = clock.GetUtcNow() + QuietFor;
        }
    }

    /// <summary>Does the next piece of work: plan a run, write one chunk, or publish.</summary>
    /// <param name="settings">How the snapshot is computed.</param>
    /// <param name="force">Plan a run even when the inputs failed before or the published run is current.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<PlanningSnapshotStep> StepAsync(PlanningOptions settings, bool force, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!await store.PlanningDeployedAsync(ct).ConfigureAwait(false))
        {
            return PlanningSnapshotStep.NotDeployed;
        }

        if (await repository.IsSourceRunningAsync(SqmSource, ct).ConfigureAwait(false))
        {
            return PlanningSnapshotStep.Waiting;
        }

        await EnsureRadioProfilesAsync(ct).ConfigureAwait(false);

        var inputs = await store.ReadPlanningInputsAsync(ct).ConfigureAwait(false);
        if (!inputs.Ready)
        {
            return PlanningSnapshotStep.Waiting;
        }

        var run = await store.LatestPlanningRunAsync("running", ct).ConfigureAwait(false);
        if (run is not null && run.Fingerprint != inputs.Fingerprint)
        {
            await store.EndPlanningRunAsync(run, "abandoned", "its inputs changed before it was finished", ct).ConfigureAwait(false);
            return PlanningSnapshotStep.Abandoned;
        }

        if (run is null)
        {
            if (!force)
            {
                var published = await store.LatestPlanningRunAsync("published", ct).ConfigureAwait(false);
                if (published?.Fingerprint == inputs.Fingerprint)
                {
                    return PlanningSnapshotStep.Current;
                }

                var failed = await store.LatestPlanningRunAsync("failed", ct).ConfigureAwait(false);
                if (failed?.Fingerprint == inputs.Fingerprint)
                {
                    return PlanningSnapshotStep.Failed;
                }
            }

            await store.PlanPlanningRunAsync(inputs, settings, ct).ConfigureAwait(false);
            return PlanningSnapshotStep.Planned;
        }

        if (run.Next() is { } next)
        {
            var key = $"{run.RunId}/{PlanningRun.Key(next.Table, next.Chunk)}";
            try
            {
                await store.BuildPlanningChunkAsync(run, next.Table, next.Chunk, settings, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _failures = string.Equals(key, _failingChunk, StringComparison.Ordinal) ? _failures + 1 : 1;
                _failingChunk = key;
                LogChunkFailed(ex, run.RunId, PlanningRun.Key(next.Table, next.Chunk), _failures, MaxAttempts);

                if (_failures < MaxAttempts)
                {
                    throw;
                }

                await store.EndPlanningRunAsync(run, "failed",
                    $"{PlanningRun.Key(next.Table, next.Chunk)} failed {MaxAttempts} times: {ex.Message}", ct).ConfigureAwait(false);
                _failingChunk = null;
                _failures = 0;
                return PlanningSnapshotStep.Failed;
            }

            _failingChunk = null;
            _failures = 0;
            return PlanningSnapshotStep.Built;
        }

        var refused = await store.TryPublishPlanningAsync(run, ct).ConfigureAwait(false);
        if (refused is not null)
        {
            await store.EndPlanningRunAsync(run, "abandoned", refused, ct).ConfigureAwait(false);
            return PlanningSnapshotStep.Abandoned;
        }

        await store.DropOldPlanningRunsAsync(ct).ConfigureAwait(false);
        return PlanningSnapshotStep.Published;
    }

    private async Task EnsureRadioProfilesAsync(CancellationToken ct)
    {
        if (await tacStore.GetActiveVersionIdAsync(ct).ConfigureAwait(false) is not { } active)
        {
            return;
        }

        var parsed = await tacStore.GetRadioParseAsync(active, ct).ConfigureAwait(false);
        if (parsed?.ParserVersion != RadioProfileParser.Version)
        {
            await tacStore.ParseRadioAsync(active, ct).ConfigureAwait(false);
        }
    }
}
