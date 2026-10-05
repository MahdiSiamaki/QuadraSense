namespace Sqm.Contracts.Quality;

/// <summary>The data-quality categories of the newest published measures run, or why there are none.</summary>
/// <param name="Available">False until a run with quality categories has been published.</param>
/// <param name="Reason">Why nothing is available, for a person; null when available.</param>
/// <param name="RunId">The run the counts come from.</param>
/// <param name="AsOf">Its data-through day.</param>
/// <param name="PublishedAt">When it was published.</param>
/// <param name="Bases">The populations the categories are shares of: all, active, history_all.</param>
/// <param name="Categories">Every category in the catalogue, in display order, zero when none.</param>
public sealed record QualitySignalsResponse(
    bool Available,
    string? Reason,
    ulong? RunId,
    DateOnly? AsOf,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<QualityBaseInfo> Bases,
    IReadOnlyList<QualitySignalInfo> Categories);

/// <summary>A population a category is measured against.</summary>
/// <param name="Code">all, active or history_all.</param>
/// <param name="Label">A short name.</param>
/// <param name="Bindings">Bindings in it.</param>
/// <param name="Numbers">Distinct numbers, exact.</param>
/// <param name="Sims">Distinct SIMs, estimated.</param>
/// <param name="Imeis">Distinct IMEIs, estimated.</param>
public sealed record QualityBaseInfo(string Code, string Label, long Bindings, long Numbers, long Sims, long Imeis);

/// <summary>One data-quality category's counts.</summary>
/// <param name="Code">Its code.</param>
/// <param name="Group">identifiers, sequences or lifetimes.</param>
/// <param name="Label">A short name.</param>
/// <param name="Meaning">What a binding in it is.</param>
/// <param name="Base">The base's code.</param>
/// <param name="Bindings">Bindings in it.</param>
/// <param name="Numbers">Distinct numbers, exact.</param>
/// <param name="Sims">Distinct SIMs, estimated.</param>
/// <param name="Imeis">Distinct IMEIs, estimated.</param>
/// <param name="Periods">For a history category, how often it happened (periods of a length, redundant adds,
/// orphan removes); for current state, 0.</param>
/// <param name="Share">Bindings over the base's bindings, 0 to 1; null when the base is empty.</param>
public sealed record QualitySignalInfo(
    string Code,
    string Group,
    string Label,
    string Meaning,
    string Base,
    long Bindings,
    long Numbers,
    long Sims,
    long Imeis,
    long Periods,
    double? Share);
