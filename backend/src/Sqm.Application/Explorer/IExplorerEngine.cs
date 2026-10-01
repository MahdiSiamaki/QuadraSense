using Sqm.Contracts.Explorer;

namespace Sqm.Application.Explorer;

/// <summary>A page of results and what it cost.</summary>
/// <param name="Plan">What it was estimated to cost before it ran.</param>
/// <param name="Rows">Values in column order.</param>
/// <param name="Total">Rows or groups matching, across all pages.</param>
/// <param name="ElapsedMs">Server time.</param>
/// <param name="RowsRead">Rows the server read.</param>
public sealed record ExplorerRows(
    ExplorerPlanInfo Plan, IReadOnlyList<IReadOnlyList<object?>> Rows, long Total, long ElapsedMs, long RowsRead);

/// <summary>Plans and runs checked Explorer queries.</summary>
public interface IExplorerEngine
{
    /// <summary>What the query would cost, from the server's own plan, without running it.</summary>
    Task<ExplorerPlanInfo> PlanAsync(CheckedExplorerQuery query, CancellationToken ct);

    /// <summary>Runs the query, if its plan is within the budget.</summary>
    /// <exception cref="ExplorerRefusedException">The plan is over the budget, or the server stopped it at a limit.</exception>
    /// <exception cref="ExplorerBusyException">As many queries as the server allows are already running.</exception>
    Task<ExplorerRows> RunAsync(CheckedExplorerQuery query, CancellationToken ct);

    /// <summary>The latest day in the event log: what every answer is as of. Null before any import.</summary>
    Task<DateOnly?> DataThroughAsync(CancellationToken ct);
}

/// <summary>A query not run, or stopped, because it would cost more than the budget allows.</summary>
public sealed class ExplorerRefusedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ExplorerRefusedException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public ExplorerRefusedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public ExplorerRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with the plan that decided it.</summary>
    public ExplorerRefusedException(ExplorerPlanInfo plan, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Plan = plan;
    }

    /// <summary>The plan, with the notes that say how to narrow the query.</summary>
    public ExplorerPlanInfo? Plan { get; }
}

/// <summary>Every Explorer slot is taken; try again in a moment.</summary>
public sealed class ExplorerBusyException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ExplorerBusyException()
        : base("Every Explorer slot is in use; try again in a moment.")
    {
    }

    /// <summary>Creates the exception.</summary>
    public ExplorerBusyException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public ExplorerBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>How an estimate is judged against the budget.</summary>
public static class ExplorerBudget
{
    /// <summary>Light, Moderate, Heavy, or Refused when over the budget.</summary>
    public static string Verdict(long estimatedRows, ExplorerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return estimatedRows > options.BudgetRows ? "Refused"
            : estimatedRows > options.ModerateRows ? "Heavy"
            : estimatedRows > options.LightRows ? "Moderate"
            : "Light";
    }
}
