namespace Sqm.Application.Planning;

/// <summary>One of the planning snapshot's tables, built chunk by chunk.</summary>
public enum PlanningTable
{
    /// <summary>Per number: its sunset, eSIM and IoT classes, and who holds which model (N1, N3).</summary>
    Numbers,

    /// <summary>Per IoT / M2M device, all time: the SIMs and numbers it has carried (N5).</summary>
    IotDevices,
}

/// <summary>What a planning run is built from, and whether it can be built at all.</summary>
/// <param name="AsOfSequence">The delivery whose state current state holds: the latest by date.</param>
/// <param name="AsOfDate">That delivery's business date; null for the initial dump.</param>
/// <param name="Fingerprint">
/// A hash of everything a run reads that can change: the delivery, the marts' readiness, the binding
/// history's day ledger, the active GSMA version, the radio parser and the measures' definition.
/// </param>
/// <param name="TacVersionId">The active GSMA version.</param>
/// <param name="Ready">False while something a run reads is incomplete; nothing is built then.</param>
/// <param name="NotReadyReason">Why not, when it is not.</param>
public sealed record PlanningInputs(
    int AsOfSequence, DateOnly? AsOfDate, ulong Fingerprint, int TacVersionId, bool Ready, string? NotReadyReason);

/// <summary>A run of the planning snapshot, as its ledger row records it.</summary>
/// <param name="RunId">Unix milliseconds when it was planned.</param>
/// <param name="AsOfSequence">The delivery it describes.</param>
/// <param name="AsOfDate">That delivery's business date.</param>
/// <param name="Fingerprint">The inputs it was planned from.</param>
/// <param name="TacVersionId">The GSMA version it was planned under.</param>
/// <param name="State">running, published, abandoned or failed.</param>
/// <param name="Chunks">Chunks per table.</param>
/// <param name="Done">The chunks written so far, as "Table:chunk".</param>
/// <param name="Note">Why it was abandoned or failed, or what publication checked.</param>
/// <param name="UpdatedAt">When the ledger row was last written.</param>
public sealed record PlanningRun(
    ulong RunId,
    int AsOfSequence,
    DateOnly? AsOfDate,
    ulong Fingerprint,
    int TacVersionId,
    string State,
    int Chunks,
    IReadOnlySet<string> Done,
    string Note,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Every chunk of every table, in the order they are built.</summary>
    public IEnumerable<(PlanningTable Table, int Chunk)> Plan() =>
        Enum.GetValues<PlanningTable>().SelectMany(t => Enumerable.Range(0, Chunks).Select(c => (t, c)));

    /// <summary>The ledger key of one chunk.</summary>
    public static string Key(PlanningTable table, int chunk) => $"{table}:{chunk}";

    /// <summary>The next chunk to build, or null when all are written.</summary>
    public (PlanningTable Table, int Chunk)? Next() =>
        Plan().Cast<(PlanningTable, int)?>().FirstOrDefault(p => !Done.Contains(Key(p!.Value.Item1, p.Value.Item2)));
}

/// <summary>
/// Builds and publishes the planning snapshot (analytics migration 030), the way the risk snapshot is
/// built: a run planned from fixed inputs, written one key-range chunk per idle moment of the worker,
/// published only when every chunk is in, no key appears twice and nothing it read has changed.
/// </summary>
public interface IPlanningSnapshotStore
{
    /// <summary>False when migration 030 (or the radio profiles of 029) has not been applied.</summary>
    Task<bool> PlanningDeployedAsync(CancellationToken ct);

    /// <summary>What a run would be built from now.</summary>
    Task<PlanningInputs> ReadPlanningInputsAsync(CancellationToken ct);

    /// <summary>The newest run in the given state, or null.</summary>
    Task<PlanningRun?> LatestPlanningRunAsync(string state, CancellationToken ct);

    /// <summary>Plans a run: fixes its key-range cut points and records it as running.</summary>
    Task<PlanningRun> PlanPlanningRunAsync(PlanningInputs inputs, PlanningOptions options, CancellationToken ct);

    /// <summary>Writes one chunk of one table, replacing any earlier attempt, and records it done.</summary>
    Task<PlanningRun> BuildPlanningChunkAsync(PlanningRun run, PlanningTable table, int chunk, PlanningOptions options, CancellationToken ct);

    /// <summary>
    /// Publishes a run whose every chunk is written, if no key appears twice and its inputs are unchanged,
    /// and writes its finished figures to the summary. Returns null when published, otherwise why not.
    /// </summary>
    Task<string?> TryPublishPlanningAsync(PlanningRun run, CancellationToken ct);

    /// <summary>Records a run as abandoned or failed, with the reason.</summary>
    Task EndPlanningRunAsync(PlanningRun run, string state, string reason, CancellationToken ct);

    /// <summary>Drops every run's rows except the newest published one, its predecessor and any running one.</summary>
    Task<int> DropOldPlanningRunsAsync(CancellationToken ct);
}
