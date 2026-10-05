namespace Sqm.Application.Quality;

/// <summary>One data-quality category, summed over a run's chunks.</summary>
/// <param name="Category">The category's code.</param>
/// <param name="Bindings">Bindings in it.</param>
/// <param name="Numbers">Distinct numbers among them, exact.</param>
/// <param name="Sims">Distinct SIMs, estimated (uniq).</param>
/// <param name="Imeis">Distinct IMEIs, estimated (uniq).</param>
/// <param name="Periods">For a history category, how often it happened - the add-to-remove periods of a length, the
/// redundant adds, the orphan removes - against the bindings it happened to; for current state, 0.</param>
public sealed record QualityCategoryCount(string Category, long Bindings, long Numbers, long Sims, long Imeis, long Periods);

/// <summary>The data-quality categories of one published run of the measures snapshot.</summary>
/// <param name="RunId">The run.</param>
/// <param name="AsOf">Its data-through day.</param>
/// <param name="PublishedAt">When it was published.</param>
/// <param name="Categories">Every category with at least one binding.</param>
public sealed record QualitySnapshot(ulong RunId, DateOnly AsOf, DateTimeOffset PublishedAt, IReadOnlyList<QualityCategoryCount> Categories);

/// <summary>Reads the data-quality categories (analytics migration 026). Read-only.</summary>
public interface IQualityReader
{
    /// <summary>The newest published run that carries quality categories, or null.</summary>
    Task<QualitySnapshot?> GetLatestAsync(CancellationToken ct);
}

/// <summary>What a category means, where it comes from, and what it is measured against.</summary>
/// <param name="Code">Its code.</param>
/// <param name="Group">identifiers, sequences or lifetimes.</param>
/// <param name="Label">A short name.</param>
/// <param name="Meaning">What a binding in it is, in a sentence.</param>
/// <param name="Base">The category whose bindings it is a share of.</param>
public sealed record QualityCategory(string Code, string Group, string Label, string Meaning, string Base);

/// <summary>
/// The data-quality categories: feed and identifier facts, never evidence about a subscriber.
/// </summary>
/// <remarks>
/// <para>
/// The spec's list, each defined so that it says something on this feed. "Extremely short lifetime" is
/// shown as a distribution, not a flag: a period of a day or less is how 71% of bindings with a period
/// behave here (measured 2026-10-05), so flagging it would flag the ordinary.
/// </para>
/// </remarks>
public static class QualityCategories
{
    /// <summary>Every category, in the order shown.</summary>
    public static IReadOnlyList<QualityCategory> All { get; } =
    [
        new("missing_imei", "identifiers", "Missing IMEI",
            "The handset's identity is the feed's '000000': the network did not know the device.", "all"),
        new("invalid_imei", "identifiers", "Invalid IMEI",
            "The IMEI is neither 14 digits nor the '000000' sentinel - cut short, padded or not numeric.", "all"),
        new("zero_imei", "identifiers", "IMEI of fourteen zeros",
            "Fourteen zeros: a placeholder, not a handset.", "all"),
        new("shifted_imei", "identifiers", "Shifted IMEI",
            "The 15 September shape: the first digit dropped and a 0 added, so the TAC is not a real one.", "all"),
        new("unknown_tac", "identifiers", "Unknown TAC",
            "A well-formed IMEI whose TAC the active GSMA version does not know. Shifted IMEIs are not counted here.", "all"),
        new("invalid_imsi", "identifiers", "Invalid IMSI",
            "The SIM's IMSI is not 15 digits.", "all"),
        new("invalid_msisdn", "identifiers", "Invalid number",
            "The phone number (MSISDN) is not 10 digits; most are 4.", "all"),
        new("redundant_add", "sequences", "Added while already held",
            "The feed added the binding when it already held it: an add with no remove since the last add.", "history_all"),
        new("orphan_remove", "sequences", "Removed while not held",
            "The feed removed the binding when it did not hold it: a remove with no add before it, outside the initial dump.", "history_all"),
        new("untouched_since_dump", "lifetimes", "Held since the initial dump, never changed",
            "Not yet removed, and no daily file has mentioned it: held since before 2026-01-26.", "active"),
        new("period_same_day", "lifetimes", "Period: removed the day it was added",
            "An add and a remove on the same day.", "history_all"),
        new("period_1_day", "lifetimes", "Period: one day",
            "Removed the day after it was added.", "history_all"),
        new("period_2_7_days", "lifetimes", "Period: 2 to 7 days", "Removed 2 to 7 days after it was added.", "history_all"),
        new("period_8_30_days", "lifetimes", "Period: 8 to 30 days", "Removed 8 to 30 days after it was added.", "history_all"),
        new("period_31_90_days", "lifetimes", "Period: 31 to 90 days", "Removed 31 to 90 days after it was added.", "history_all"),
        new("period_over_90_days", "lifetimes", "Period: over 90 days", "Removed more than 90 days after it was added.", "history_all"),
        new("still_held", "lifetimes", "Still held",
            "The binding's last event is an add, or it came from the initial dump and was never removed.", "history_all"),
    ];
}
