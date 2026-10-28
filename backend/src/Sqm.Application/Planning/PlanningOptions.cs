namespace Sqm.Application.Planning;

/// <summary>The configuration section <c>Planning</c>: how the planning snapshot is computed.</summary>
public sealed class PlanningOptions
{
    /// <summary>The configuration section's name.</summary>
    public const string SectionName = "Planning";

    /// <summary>
    /// Bumped whenever what a stored measure means changes, so every run built under the old meaning
    /// becomes stale. 2: handset lifecycle (N4), analytics migration 031.
    /// </summary>
    public const int DefinitionVersion = 2;

    /// <summary>
    /// A new handset replaces the old one on a number when the old one leaves at most this many days
    /// after the new one arrives; a longer overlap is two devices used side by side. The product owner's
    /// decision of 2026-10-27, from the measured overlaps (analytics migration 031).
    /// </summary>
    public const int ChangeOverlapDays = 7;

    /// <summary>... or when the old one left at most this many days before the new one arrived.</summary>
    public const int ChangeGapDays = 30;

    /// <summary>A number with more handsets than this in its history is not assessed for changes.</summary>
    public const int MaxHandsetsPerNumber = 20;

    /// <summary>... nor one with more IMEIs than this before dual-SIM twins are set aside.</summary>
    public const int MaxImeisPerNumber = 40;

    /// <summary>
    /// A handset counts as a number's only if it stayed this many days (or is held now and has been for
    /// so long): 59.3% of spells were one-day sightings. The product owner's decision of 2026-10-27.
    /// </summary>
    public const int MinStayDays = 7;

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
