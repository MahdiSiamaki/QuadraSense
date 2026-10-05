namespace Sqm.Application.Risk;

/// <summary>One of the snapshot's tables, built chunk by chunk.</summary>
public enum RiskTable
{
    /// <summary>Per SIM: IMEIs added in 30, 20 and 7 days.</summary>
    SimWindow,

    /// <summary>Per IMEI: SIMs and numbers added in 30 and 7 days.</summary>
    ImeiWindow,

    /// <summary>Per IMEI, all time: SIMs and numbers ever, and SIMs not removed.</summary>
    ImeiLifetime,

    /// <summary>
    /// Per range of numbers: the data-quality categories of current state and of the binding history
    /// (analytics migration 026). Last, so the risk measures are not held up by the longest pass.
    /// </summary>
    Quality,
}

/// <summary>What a snapshot is built from, and whether it can be built at all.</summary>
/// <param name="AsOf">The data-through day: the latest day the binding history holds as written.</param>
/// <param name="Fingerprint">
/// A hash of everything a run reads or is shaped by that can change: the history's day ledger, each
/// day's feed-quality measurement, the active GSMA version, the measures' definition and the storage
/// floors. A run built from other inputs is stale.
/// </param>
/// <param name="TacVersionId">The active GSMA version.</param>
/// <param name="Ready">False while the binding history is incomplete; nothing is built then.</param>
/// <param name="NotReadyReason">Why not, when it is not.</param>
public sealed record RiskInputs(DateOnly AsOf, ulong Fingerprint, int TacVersionId, bool Ready, string? NotReadyReason);

/// <summary>A run of the snapshot, as its ledger row records it.</summary>
/// <param name="RunId">Unix milliseconds when it was planned.</param>
/// <param name="AsOf">The day its windows end on.</param>
/// <param name="Fingerprint">The inputs it was planned from.</param>
/// <param name="TacVersionId">The GSMA version it was planned under.</param>
/// <param name="State">running, published, abandoned or failed.</param>
/// <param name="Chunks">Chunks per table.</param>
/// <param name="Done">The chunks written so far, as "Table:chunk".</param>
/// <param name="Note">Why it was abandoned or failed, or what publication checked.</param>
/// <param name="UpdatedAt">When the ledger row was last written.</param>
public sealed record RiskRun(
    ulong RunId,
    DateOnly AsOf,
    ulong Fingerprint,
    int TacVersionId,
    string State,
    int Chunks,
    IReadOnlySet<string> Done,
    string Note,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Every chunk of every table, in the order they are built.</summary>
    public IEnumerable<(RiskTable Table, int Chunk)> Plan() =>
        Enum.GetValues<RiskTable>().SelectMany(t => Enumerable.Range(0, Chunks).Select(c => (t, c)));

    /// <summary>The ledger key of one chunk.</summary>
    public static string Key(RiskTable table, int chunk) => $"{table}:{chunk}";

    /// <summary>The next chunk to build, or null when all are written.</summary>
    public (RiskTable Table, int Chunk)? Next() =>
        Plan().Cast<(RiskTable, int)?>().FirstOrDefault(p => !Done.Contains(Key(p!.Value.Item1, p.Value.Item2)));
}

/// <summary>
/// Builds and publishes the risk snapshot (analytics migration 024): the measures the SIM and IMEI
/// risk lists read.
/// </summary>
/// <remarks>
/// Every statement runs under the import's own limits - one thread, 1.2 GB, spilling at 300 MB - plus
/// a server-side time limit, so the snapshot can share the node with everything else.
/// </remarks>
public interface IRiskSnapshotStore
{
    /// <summary>False when migration 024 has not been applied.</summary>
    Task<bool> DeployedAsync(CancellationToken ct);

    /// <summary>What a run would be built from now, under these options' floors.</summary>
    Task<RiskInputs> ReadInputsAsync(RiskOptions options, CancellationToken ct);

    /// <summary>The newest run in the given state, or null.</summary>
    Task<RiskRun?> LatestRunAsync(string state, CancellationToken ct);

    /// <summary>Plans a run: fixes its key-range cut points and records it as running.</summary>
    Task<RiskRun> PlanRunAsync(RiskInputs inputs, RiskOptions options, CancellationToken ct);

    /// <summary>Writes one chunk of one table, replacing any earlier attempt, and records it done.</summary>
    Task<RiskRun> BuildChunkAsync(RiskRun run, RiskTable table, int chunk, RiskOptions options, CancellationToken ct);

    /// <summary>
    /// Publishes a run whose every chunk is written, if no key appears twice and its inputs are
    /// unchanged. Returns null when published, otherwise why not.
    /// </summary>
    Task<string?> TryPublishAsync(RiskRun run, RiskOptions options, CancellationToken ct);

    /// <summary>Records a run as abandoned - overtaken, or refused at publication - with the reason.</summary>
    Task AbandonAsync(RiskRun run, string reason, CancellationToken ct);

    /// <summary>
    /// Records a run as failed: a chunk that would not build. No run is planned again from the same
    /// inputs until they change, or a person asks (<c>--refresh-risk --force</c>).
    /// </summary>
    Task FailAsync(RiskRun run, string reason, CancellationToken ct);

    /// <summary>Drops every run's rows except the newest published one, its predecessor and any running one.</summary>
    Task<int> DropOldRunsAsync(CancellationToken ct);
}
