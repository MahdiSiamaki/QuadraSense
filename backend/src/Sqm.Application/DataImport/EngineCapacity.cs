namespace Sqm.Application.DataImport;

/// <summary>What the analytics engine has to give, as it reports it where it runs.</summary>
/// <remarks>
/// The three machines this runs on differ in what limits them: memory on the laptop, the disk on the
/// department's VM, and limits that are ceilings rather than promises on OpenShift. So nothing here is
/// a constant of one of them (<c>docs/research/2026-11-worker-capacity.md</c>).
/// </remarks>
/// <param name="MemoryBytes">The memory the server allows itself.</param>
/// <param name="Cores">The cores it would give one statement.</param>
/// <param name="StepPeakBytes">
/// The most one step of a background run has held lately, or the per-statement limit before there is
/// any such step to go by.
/// </param>
public sealed record EngineCapacity(long MemoryBytes, int Cores, long StepPeakBytes)
{
    /// <summary>The share of the server's memory that background runs may count on.</summary>
    /// <remarks>
    /// The rest is for merges, dashboards and searches. Two lanes at their measured peak took 2.2 GiB of
    /// the laptop's 5.2 and would take 3.2 GiB of the VM's 6.4 (ADR-021).
    /// </remarks>
    public const double BackgroundShare = 0.6;

    /// <summary>How many steps can run at once: one lane at the least.</summary>
    /// <remarks>
    /// Half the cores, because each step holds one and merges and readers need the others.
    /// </remarks>
    public int Lanes => StepPeakBytes <= 0
        ? 1
        : (int)Math.Max(1, Math.Min(Cores / 2, MemoryBytes * BackgroundShare / StepPeakBytes));
}

/// <summary>Reads what the analytics engine has to give.</summary>
public interface IEngineCapacityReader
{
    /// <summary>Reads the engine's capacity as it is now.</summary>
    Task<EngineCapacity> ReadCapacityAsync(CancellationToken ct);

    /// <summary>The rows the API has read lately: what people are asking of the disk.</summary>
    /// <param name="window">How far back to count.</param>
    /// <param name="ct">Cancellation.</param>
    Task<long> ReadPeoplesRowsAsync(TimeSpan window, CancellationToken ct);
}
