namespace Sqm.Application.Planning;

/// <summary>The configuration section <c>Planning</c>: how the planning snapshot is computed.</summary>
public sealed class PlanningOptions
{
    /// <summary>The configuration section's name.</summary>
    public const string SectionName = "Planning";

    /// <summary>
    /// Bumped whenever what a stored measure means changes, so every run built under the old meaning
    /// becomes stale.
    /// </summary>
    public const int DefinitionVersion = 1;

    /// <summary>
    /// Key-range chunks per table. One chunk is built per idle moment of the worker, so a file that
    /// arrives waits at most for the chunk in progress.
    /// </summary>
    public int Chunks { get; set; } = 32;

    /// <summary>The server-side time limit of one chunk's statement, below the ingestion client's 30 minutes.</summary>
    public int MaxStatementSeconds { get; set; } = 1500;

    /// <summary>What is wrong with these settings, if anything.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (Chunks is < 1 or > 256)
        {
            problems.Add("Planning:Chunks is between 1 and 256.");
        }

        if (MaxStatementSeconds is < 60 or > 1700)
        {
            problems.Add("Planning:MaxStatementSeconds is between 60 and 1700.");
        }

        return problems;
    }
}
