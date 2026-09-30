using Sqm.Domain.Quality;

namespace Sqm.Application.Quality;

/// <summary>How each day's file is judged against the days known to be ordinary.</summary>
/// <remarks>
/// <para>
/// The defaults are measured, not chosen. The reference window is the stretch of the operator's
/// files before either known defect: SIMs with more than one number start climbing on
/// 2026-07-27, shifted IMEIs on 2026-09-15. When corrected files arrive for the days after, the
/// window can be widened here without anything being recomputed - the rates are derived from
/// counts whenever they are read.
/// </para>
/// <para>
/// A flag is a rate above <see cref="Multiplier"/> times the reference days' median rate, and
/// above <see cref="MinimumRate"/>. Checked against all 233 real days: at 5, shifted IMEIs flag
/// exactly 15-26 September, multi-number SIMs 28 July onwards, malformed IMEIs 25-26 August - and
/// three earlier episodes nobody had noticed (docs/architecture/15-feed-quality.md).
/// </para>
/// </remarks>
public sealed class FeedQualityOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "FeedQuality";

    /// <summary>First day of the reference window, inclusive.</summary>
    public DateOnly ReferenceFrom { get; set; } = new(2026, 1, 26);

    /// <summary>Last day of the reference window, inclusive.</summary>
    public DateOnly ReferenceTo { get; set; } = new(2026, 7, 26);

    /// <summary>How many times the reference days' median rate a day's rate must exceed to be flagged.</summary>
    public double Multiplier { get; set; } = 5;

    /// <summary>The lowest rate that can be flagged at all, 0 to 1. 0.0001 is one row in ten thousand.</summary>
    public double MinimumRate { get; set; } = 0.0001;
}

/// <summary>Read access to the per-day feed-quality counts.</summary>
public interface IFeedQualityReader
{
    /// <summary>The days in a range, inclusive, oldest first. Either bound may be open.</summary>
    Task<IReadOnlyList<FeedQualityDay>> GetDaysAsync(DateOnly? fromDate, DateOnly? toDate, CancellationToken ct);
}

/// <summary>One day and what was found on it.</summary>
/// <param name="Day">The day's counts.</param>
/// <param name="Findings">Each check, flagged or not.</param>
public sealed record AssessedFeedDay(FeedQualityDay Day, IReadOnlyList<FeedQualityFinding> Findings)
{
    /// <summary>True when any check is out of line.</summary>
    public bool Flagged => Findings.Any(f => f.Flagged);
}

/// <summary>Days judged against a reference, and the reference they were judged against.</summary>
/// <param name="Reference">What is ordinary.</param>
/// <param name="Days">The days, oldest first.</param>
public sealed record FeedQualityReport(FeedQualityReference Reference, IReadOnlyList<AssessedFeedDay> Days)
{
    /// <summary>Judges every day in a range against the configured reference window.</summary>
    /// <remarks>
    /// One implementation for the API and the import, so the Import Center and the dashboard cannot
    /// judge the same day differently.
    /// </remarks>
    public static async Task<FeedQualityReport> BuildAsync(
        IFeedQualityReader reader, FeedQualityOptions options, DateOnly? fromDate, DateOnly? toDate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(options);

        var reference = FeedQualityRules.Reference(
            await reader.GetDaysAsync(options.ReferenceFrom, options.ReferenceTo, ct).ConfigureAwait(false));

        var days = await reader.GetDaysAsync(fromDate, toDate, ct).ConfigureAwait(false);

        return new FeedQualityReport(reference, [.. days.Select(d => new AssessedFeedDay(
            d, FeedQualityRules.Assess(d, reference, options.Multiplier, options.MinimumRate)))]);
    }
}
