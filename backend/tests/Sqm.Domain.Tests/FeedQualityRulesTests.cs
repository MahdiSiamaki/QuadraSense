using Sqm.Domain.Quality;

namespace Sqm.Domain.Tests;

/// <summary>A day's file is judged against the ordinary days, and says by how much it differs.</summary>
public sealed class FeedQualityRulesTests
{
    private static FeedQualityDay Day(
        DateOnly date, long rows = 6_503_281, long sims = 5_849_230, long shifted = 6_497,
        long multiNumber = 926, long malformed = 3_502, long unknownDevice = 3_282) =>
        new(date, rows, sims, unknownDevice, malformed, UnknownTacRows: shifted + 12_000, shifted,
            multiNumber, multiNumber * 3, TacVersionId: 1);

    private static DateOnly D(int i) => new DateOnly(2026, 1, 26).AddDays(i);

    /// <summary>Thirty days in the shape measured on 20 July, with a little day-to-day spread.</summary>
    private static FeedQualityReference Ordinary() => FeedQualityRules.Reference(
        Enumerable.Range(0, 30).Select(i => Day(D(i), shifted: 6_000 + (i * 30), multiNumber: 600 + (i * 20))));

    [Fact]
    public void An_ordinary_day_is_not_flagged_on_anything()
    {
        var findings = FeedQualityRules.Assess(Day(D(40)), Ordinary(), multiplier: 5, minimumRate: 0.0001);

        Assert.Equal(FeedQualityRules.Checks, findings.Select(f => f.Check));
        Assert.All(findings, f => Assert.False(f.Flagged, f.Explanation));
        Assert.All(findings, f => Assert.EndsWith("within the ordinary range.", f.Explanation, StringComparison.Ordinal));
    }

    /// <summary>20 September as it was: a quarter of rows shifted, 4.2% of SIMs with two numbers.</summary>
    [Fact]
    public void The_twentieth_of_september_is_flagged_on_both_defects_and_nothing_else()
    {
        var sep20 = new FeedQualityDay(new DateOnly(2026, 9, 20), 11_953_857, 8_550_042,
            UnknownDeviceRows: 404, MalformedImeiRows: 5_437, UnknownTacRows: 2_957_257,
            ShiftedImeiRows: 2_940_845, MultiNumberSims: 360_910, MultiNumberSimRows: 1_472_199, TacVersionId: 1);

        var findings = FeedQualityRules.Assess(sep20, Ordinary(), multiplier: 5, minimumRate: 0.0001)
            .ToDictionary(f => f.Check);

        Assert.True(findings[FeedQualityCheck.ShiftedImei].Flagged);
        Assert.True(findings[FeedQualityCheck.MultiNumberSim].Flagged);
        Assert.False(findings[FeedQualityCheck.MalformedImei].Flagged);
        Assert.False(findings[FeedQualityCheck.UnknownDevice].Flagged);

        // The sentence carries the evidence: the rate, the count, the reference, the margin.
        var shifted = findings[FeedQualityCheck.ShiftedImei].Explanation;
        Assert.StartsWith("24.60% of rows (2,940,845) carry an IMEI that looks shifted by one digit", shifted, StringComparison.Ordinal);
        Assert.Contains("(median of 30 reference days)", shifted, StringComparison.Ordinal);
        Assert.EndsWith("times that.", shifted, StringComparison.Ordinal);
    }

    [Fact]
    public void The_threshold_is_the_median_times_the_multiplier_exactly()
    {
        // Ten reference days, all at exactly 0.1% shifted: the median is 0.1%.
        var reference = FeedQualityRules.Reference(Enumerable.Range(0, 10).Select(i => Day(D(i), rows: 1_000_000, shifted: 1_000)));
        var atIt = FeedQualityRules.Assess(Day(D(20), rows: 1_000_000, shifted: 5_000), reference, 5, 0.0001)[0];
        var past = FeedQualityRules.Assess(Day(D(21), rows: 1_000_000, shifted: 5_001), reference, 5, 0.0001)[0];

        Assert.Equal(0.001, reference.Median[FeedQualityCheck.ShiftedImei], 12);
        Assert.Equal(0.005, atIt.Threshold!.Value, 12);
        Assert.False(atIt.Flagged);
        Assert.True(past.Flagged);
    }

    /// <summary>
    /// Why the median: the real reference window holds an episode of its own, and a high percentile
    /// lands on it.
    /// </summary>
    /// <remarks>
    /// As measured: multi-number SIMs ordinarily 0.0111% of SIMs, but 0.2-0.39% on 25 days from
    /// 14 April to 7 May. A 99th percentile of that window is 0.378%, and 28 July at 0.068% - the
    /// start of the defect - passed under it. The median does not notice the episode.
    /// </remarks>
    [Fact]
    public void An_episode_inside_the_reference_window_does_not_raise_what_counts_as_ordinary()
    {
        var days = Enumerable.Range(0, 172).Select(i => Day(D(i), sims: 6_000_000,
            multiNumber: i is >= 78 and < 103 ? 18_000 : 666));   // 0.3% for 25 days, 0.0111% otherwise
        var reference = FeedQualityRules.Reference(days);

        var jul28 = FeedQualityRules.Assess(Day(new DateOnly(2026, 7, 28), sims: 6_000_000, multiNumber: 4_080), reference, 5, 0.0001)
            .Single(f => f.Check == FeedQualityCheck.MultiNumberSim);

        Assert.Equal(0.000111, reference.Median[FeedQualityCheck.MultiNumberSim], 9);
        Assert.True(jul28.Flagged, jul28.Explanation);
    }

    [Fact]
    public void A_check_that_is_zero_on_ordinary_days_needs_the_floor_to_flag()
    {
        var reference = FeedQualityRules.Reference(Enumerable.Range(0, 10).Select(i => Day(D(i), malformed: 0)));

        var stray = FeedQualityRules.Assess(Day(D(20), rows: 1_000_000, malformed: 5), reference, 5, 0.0001)
            .Single(f => f.Check == FeedQualityCheck.MalformedImei);
        var real = FeedQualityRules.Assess(Day(D(21), rows: 1_000_000, malformed: 100_000), reference, 5, 0.0001)
            .Single(f => f.Check == FeedQualityCheck.MalformedImei);

        Assert.False(stray.Flagged);
        Assert.True(real.Flagged);
        Assert.Contains("Ordinary days show none", real.Explanation, StringComparison.Ordinal);
        Assert.EndsWith("above the 0.010% floor.", real.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_reference_days_nothing_is_flagged_and_it_says_why()
    {
        var findings = FeedQualityRules.Assess(Day(D(1), shifted: 3_000_000), FeedQualityRules.Reference([]), 5, 0.0001);

        Assert.All(findings, f =>
        {
            Assert.False(f.Flagged);
            Assert.Null(f.Threshold);
            Assert.EndsWith("no reference days to judge it against.", f.Explanation, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void An_empty_day_has_rates_of_zero_rather_than_a_division_by_zero()
    {
        var empty = new FeedQualityDay(new DateOnly(2026, 5, 8), 0, 0, 0, 0, 0, 0, 0, 0, 1);

        Assert.All(FeedQualityRules.Checks, c => Assert.Equal(0, FeedQualityRules.RateOf(empty, c)));
    }
}
