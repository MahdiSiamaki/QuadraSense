using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sqm.Application.DataImport;
using Sqm.Application.Risk;

namespace Sqm.Ingestion.Processing;

/// <summary>What one step of the risk snapshot did.</summary>
internal enum RiskSnapshotStep
{
    /// <summary>The snapshot tables do not exist (analytics migration 024).</summary>
    NotDeployed,

    /// <summary>The binding history is incomplete, or an SQM import is running; nothing was done.</summary>
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
/// Keeps the risk snapshot (analytics migration 024) current: one step per idle moment of the worker.
/// </summary>
/// <remarks>
/// <para>
/// <b>One chunk at a time.</b> A whole run is 72 statements of about a minute each. Built in one go
/// it would hold the worker's only slot for over an hour; built a chunk per idle moment, a file that
/// arrives waits at most for the chunk in progress, and the run resumes where it stopped - after the
/// file, after a restart, after a crash.
/// </para>
/// <para>
/// <b>Never beside an SQM import.</b> The snapshot reads the binding history the import writes, and a
/// run would be stale the moment the day lands. A run overtaken by a new day is abandoned and planned
/// again from the new inputs; one that is published while the inputs still match is what readers see.
/// </para>
/// <para>
/// <b>A chunk that will not build stops the run</b> after <see cref="MaxAttempts"/> tries, recorded as
/// failed with the error. The next day's data plans a fresh run; a person can retry sooner with
/// <c>Sqm.Ingestion --refresh-risk --force</c>.
/// </para>
/// <para>
/// <b>Quiet when there is nothing to do.</b> The worker is idle every five seconds; after an answer of
/// "current", "waiting" or "failed" the inputs are not read again for <see cref="QuietFor"/>. A day
/// that lands is noticed within that, which is nothing beside a run of an hour.
/// </para>
/// </remarks>
internal sealed partial class RiskSnapshot(
    IRiskSnapshotStore store,
    IImportJobRepository repository,
    IOptionsMonitor<RiskOptions> options,
    TimeProvider clock,
    ILogger<RiskSnapshot> logger) : IIdleTask
{
    /// <summary>Consecutive failures of one chunk before its run is marked failed.</summary>
    internal const int MaxAttempts = 3;

    /// <summary>How long nothing to do stays the answer before the inputs are read again.</summary>
    internal static readonly TimeSpan QuietFor = TimeSpan.FromMinutes(5);

    private DateTimeOffset _quietUntil = DateTimeOffset.MinValue;

    private const string SqmSource = "SQM";

    private string? _failingChunk;
    private int _failures;
    private string? _reportedProblems;

    [LoggerMessage(EventId = 3520, Level = LogLevel.Warning,
        Message = "Risk snapshot not built: the Risk settings are invalid. {Problems}")]
    private partial void LogInvalid(string problems);

    [LoggerMessage(EventId = 3521, Level = LogLevel.Warning,
        Message = "Risk snapshot run {RunId}: {Chunk} failed (attempt {Attempt} of {MaxAttempts})")]
    private partial void LogChunkFailed(Exception exception, ulong runId, string chunk, int attempt, int maxAttempts);

    /// <inheritdoc />
    public async Task RunAsync(CancellationToken ct)
    {
        var settings = options.CurrentValue;
        var problems = settings.Problems();
        if (problems.Count > 0)
        {
            // Said once per distinct problem, not every five seconds.
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
        if (step is RiskSnapshotStep.Current or RiskSnapshotStep.Waiting or RiskSnapshotStep.Failed or RiskSnapshotStep.NotDeployed)
        {
            _quietUntil = clock.GetUtcNow() + QuietFor;
        }
    }

    /// <summary>Does the next piece of work: plan a run, write one chunk, or publish.</summary>
    /// <param name="settings">The rule set, whose floors shape the stored rows.</param>
    /// <param name="force">Plan a run even when the inputs failed before or the published run is current.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<RiskSnapshotStep> StepAsync(RiskOptions settings, bool force, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!await store.DeployedAsync(ct).ConfigureAwait(false))
        {
            return RiskSnapshotStep.NotDeployed;
        }

        if (await repository.IsSourceRunningAsync(SqmSource, ct).ConfigureAwait(false))
        {
            return RiskSnapshotStep.Waiting;
        }

        var inputs = await store.ReadInputsAsync(settings, ct).ConfigureAwait(false);
        if (!inputs.Ready)
        {
            return RiskSnapshotStep.Waiting;
        }

        var run = await store.LatestRunAsync("running", ct).ConfigureAwait(false);
        if (run is not null && run.Fingerprint != inputs.Fingerprint)
        {
            await store.AbandonAsync(run, "its inputs changed before it was finished", ct).ConfigureAwait(false);
            return RiskSnapshotStep.Abandoned;
        }

        if (run is null)
        {
            if (!force)
            {
                var published = await store.LatestRunAsync("published", ct).ConfigureAwait(false);
                if (published?.Fingerprint == inputs.Fingerprint)
                {
                    return RiskSnapshotStep.Current;
                }

                var failed = await store.LatestRunAsync("failed", ct).ConfigureAwait(false);
                if (failed?.Fingerprint == inputs.Fingerprint)
                {
                    return RiskSnapshotStep.Failed;
                }
            }

            await store.PlanRunAsync(inputs, settings, ct).ConfigureAwait(false);
            return RiskSnapshotStep.Planned;
        }

        if (run.Next() is { } next)
        {
            var key = $"{run.RunId}/{RiskRun.Key(next.Table, next.Chunk)}";
            try
            {
                await store.BuildChunkAsync(run, next.Table, next.Chunk, settings, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _failures = string.Equals(key, _failingChunk, StringComparison.Ordinal) ? _failures + 1 : 1;
                _failingChunk = key;
                LogChunkFailed(ex, run.RunId, RiskRun.Key(next.Table, next.Chunk), _failures, MaxAttempts);

                if (_failures < MaxAttempts)
                {
                    throw;
                }

                await store.FailAsync(run,
                    $"{RiskRun.Key(next.Table, next.Chunk)} failed {MaxAttempts} times: {ex.Message}", ct).ConfigureAwait(false);
                _failingChunk = null;
                _failures = 0;
                return RiskSnapshotStep.Failed;
            }

            _failingChunk = null;
            _failures = 0;
            return RiskSnapshotStep.Built;
        }

        var refused = await store.TryPublishAsync(run, settings, ct).ConfigureAwait(false);
        if (refused is not null)
        {
            await store.AbandonAsync(run, refused, ct).ConfigureAwait(false);
            return RiskSnapshotStep.Abandoned;
        }

        await store.DropOldRunsAsync(ct).ConfigureAwait(false);
        return RiskSnapshotStep.Published;
    }
}
