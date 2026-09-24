namespace Sqm.Ingestion;

/// <summary>Work the import worker does when there is no job it can claim.</summary>
/// <remarks>
/// Run on the claim loop, one at a time, and before the worker next asks for a job - so a task
/// here never runs alongside a job this worker holds. It may run alongside a job another worker
/// holds, and must check for that itself.
/// </remarks>
public interface IIdleTask
{
    /// <summary>Does the work, if there is any.</summary>
    Task RunAsync(CancellationToken ct);
}
