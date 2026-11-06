namespace Sqm.Ingestion;

/// <summary>Work the import worker does when there is no job it can claim.</summary>
/// <remarks>
/// <para>
/// Run on the claim loop, before the worker next asks for a job - so a task here never runs
/// alongside a job this worker holds. It may run alongside a job another worker holds, and must
/// check for that itself.
/// </para>
/// <para>
/// One task's steps never overlap, but two tasks may run at once (<see cref="IdleLanes"/>, ADR-021):
/// a step must be safe beside another task's, unless <see cref="RunsAlone"/> says it is not.
/// </para>
/// </remarks>
public interface IIdleTask
{
    /// <summary>Whether nothing else may run beside this task. Such a task goes first.</summary>
    bool RunsAlone => false;

    /// <summary>Does the next piece of work, if there is any.</summary>
    /// <returns>
    /// Whether there may be more straight away. The worker then goes on instead of waiting its poll
    /// interval, which cost a run of 384 steps half an hour of sleep.
    /// </returns>
    Task<bool> RunAsync(CancellationToken ct);
}
