using Sqm.Domain.Quality;
using Sqm.Domain.Risk;

namespace Sqm.Domain.Tests;

/// <summary>
/// A level is decided in one place, from measures, flagged feed days and the rule set - and the
/// system never names fraud.
/// </summary>
public sealed class RiskRulesTests
{
    private static readonly DateOnly Through = new(2026, 9, 26);

    private static RiskWindow Window(int days, int withData = -1) =>
        new(Through.AddDays(-(days - 1)), Through, withData < 0 ? days : withData);

    private static RiskSettings Settings(double maxDefectShare = 0.5) => new(
        new Dictionary<RiskRule, RiskThreshold>
        {
            [RiskRule.SharedImeiSims30] = new(50),
            [RiskRule.SharedImeiSimsEver] = new(100),
            [RiskRule.SharedImeiSimsNotRemoved] = new(20),
            [RiskRule.HighDeviceCount30] = new(50),
            [RiskRule.RapidDeviceChange7] = new(10),
            [RiskRule.Randomisation20] = new(200, Tacs: 20),
            [RiskRule.RepeatedSimChange7] = new(2),
        },
        maxDefectShare,
        "3f2a9c01");

    private static RiskEvidence Sim(params RiskMeasure[] measures) => new(RiskFamily.Sim, measures);

    private static RiskAssessment Assess(RiskEvidence evidence, params FlaggedDay[] flagged) =>
        RiskRules.Assess(evidence, Settings(), flagged);

    [Fact]
    public void A_count_at_or_below_its_threshold_is_an_observation()
    {
        var at = Assess(Sim(new RiskMeasure(RiskRule.HighDeviceCount30, 50, Window(30))));

        Assert.Equal(RiskLevel.Observation, at.Level);
        Assert.Equal("This SIM (IMSI) was added to 50 different IMEIs in the 30 days to 2026-09-26 "
            + "(30 of 30 days have data) (threshold: more than 50; rules 3f2a9c01).", at.Reasons[0].Text);
    }

    [Fact]
    public void A_clean_windowed_crossing_is_a_risk_signal_and_says_so_with_its_evidence()
    {
        var result = Assess(Sim(new RiskMeasure(RiskRule.HighDeviceCount30, 61, Window(30, withData: 29))));

        Assert.Equal(RiskLevel.RiskSignal, result.Level);
        Assert.True(result.Assessable);
        Assert.Equal("Potential risk signal: this SIM (IMSI) was added to 61 different IMEIs in the 30 days to "
            + "2026-09-26 (29 of 30 days have data) (threshold: more than 50; rules 3f2a9c01).", result.Reasons[0].Text);
        Assert.DoesNotContain("fraud", result.Reasons[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every window since 28 July overlaps days flagged for SIMs with several numbers.</summary>
    [Fact]
    public void A_crossing_in_a_window_that_overlaps_a_relevant_flagged_day_is_held_at_anomaly()
    {
        var result = Assess(
            Sim(new RiskMeasure(RiskRule.HighDeviceCount30, 61, Window(30))),
            new FlaggedDay(new DateOnly(2026, 9, 20), FeedQualityCheck.MultiNumberSim),
            new FlaggedDay(new DateOnly(2026, 9, 22), FeedQualityCheck.ShiftedImei));

        Assert.Equal(RiskLevel.Anomaly, result.Level);
        Assert.True(result.Reasons[0].Capped);
        Assert.Contains("2 of the window's days are flagged by the feed-quality monitor (shifted IMEIs, "
            + "SIMs with several numbers; 2026-09-20 to 2026-09-22)", result.Reasons[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flagged_day_outside_the_window_or_for_another_check_does_not_hold_it_back()
    {
        var result = Assess(
            Sim(new RiskMeasure(RiskRule.RapidDeviceChange7, 11, Window(7))),
            new FlaggedDay(new DateOnly(2026, 9, 1), FeedQualityCheck.MultiNumberSim),    // before the 7 days
            new FlaggedDay(new DateOnly(2026, 9, 25), FeedQualityCheck.UnknownDevice));  // not relevant to SIMs

        Assert.Equal(RiskLevel.RiskSignal, result.Level);
    }

    [Fact]
    public void A_number_is_held_back_only_by_the_check_that_can_inflate_sim_changes()
    {
        var shiftedOnly = RiskRules.Assess(
            new RiskEvidence(RiskFamily.Number, [new RiskMeasure(RiskRule.RepeatedSimChange7, 3, Window(7))]),
            Settings(), [new FlaggedDay(new DateOnly(2026, 9, 24), FeedQualityCheck.ShiftedImei)]);
        var multiNumber = RiskRules.Assess(
            new RiskEvidence(RiskFamily.Number, [new RiskMeasure(RiskRule.RepeatedSimChange7, 3, Window(7))]),
            Settings(), [new FlaggedDay(new DateOnly(2026, 9, 24), FeedQualityCheck.MultiNumberSim)]);

        Assert.Equal(RiskLevel.RiskSignal, shiftedOnly.Level);
        Assert.Equal(RiskLevel.Anomaly, multiNumber.Level);
    }

    [Theory]
    [InlineData(RiskRule.SharedImeiSimsEver, 33_101)]
    [InlineData(RiskRule.SharedImeiSimsNotRemoved, 655)]
    public void An_all_time_count_never_goes_above_anomaly_however_large(RiskRule rule, long value)
    {
        var result = RiskRules.Assess(
            new RiskEvidence(RiskFamily.Imei, [new RiskMeasure(rule, value, Window: null)]), Settings(), []);

        Assert.Equal(RiskLevel.Anomaly, result.Level);
        Assert.Contains("this is an anomaly, not a risk signal", result.Reasons[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Randomisation_needs_both_the_imeis_and_the_tacs()
    {
        var manyImeisFewTacs = Assess(Sim(new RiskMeasure(RiskRule.Randomisation20, 1_204, Window(20), Tacs: 3)));
        var both = Assess(Sim(new RiskMeasure(RiskRule.Randomisation20, 1_204, Window(20), Tacs: 318)));

        Assert.Equal(RiskLevel.Observation, manyImeisFewTacs.Level);
        Assert.Equal(RiskLevel.RiskSignal, both.Level);
        Assert.Contains("added to 1,204 different IMEIs across 318 TACs", both.Reasons[0].Text, StringComparison.Ordinal);
        Assert.Contains("thresholds: more than 200 IMEIs and more than 20 TACs", both.Reasons[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entity_whose_evidence_is_mostly_feed_defects_is_not_assessable()
    {
        var evidence = new RiskEvidence(RiskFamily.Sim,
            [new RiskMeasure(RiskRule.HighDeviceCount30, 61, Window(30))], AddsInWindow: 1_000, AddsSetAside: 501);

        var result = RiskRules.Assess(evidence, Settings(maxDefectShare: 0.5), []);

        Assert.False(result.Assessable);
        Assert.Equal(RiskLevel.Anomaly, result.Level);
    }

    [Fact]
    public void Exactly_at_the_defect_share_limit_is_still_assessable()
    {
        var evidence = new RiskEvidence(RiskFamily.Sim,
            [new RiskMeasure(RiskRule.HighDeviceCount30, 61, Window(30))], AddsInWindow: 1_000, AddsSetAside: 500);

        Assert.True(RiskRules.Assess(evidence, Settings(maxDefectShare: 0.5), []).Assessable);
    }

    [Fact]
    public void A_rule_with_no_threshold_is_not_judged()
    {
        var result = Assess(new RiskEvidence(RiskFamily.Imei,
            [new RiskMeasure(RiskRule.SharedImeiNumbers30, 9_999, Window(30))]));

        Assert.Equal(RiskLevel.Observation, result.Level);
        Assert.Null(result.Reasons[0].Threshold);
        Assert.Contains("Not judged: this rule has no calibrated threshold", result.Reasons[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_highest_reason_comes_first_and_sets_the_level()
    {
        var result = Assess(Sim(
            new RiskMeasure(RiskRule.HighDeviceCount30, 3, Window(30)),
            new RiskMeasure(RiskRule.RapidDeviceChange7, 12, Window(7))));

        Assert.Equal(RiskLevel.RiskSignal, result.Level);
        Assert.Equal(RiskRule.RapidDeviceChange7, result.Reasons[0].Rule);
        Assert.Equal(RiskRule.HighDeviceCount30, result.Reasons[1].Rule);
    }

    [Fact]
    public void A_measure_from_another_family_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Assess(Sim(new RiskMeasure(RiskRule.SharedImeiSims30, 1, Window(30)))));
    }

    [Fact]
    public void Two_families_signalling_on_linked_entities_make_a_suspicious_pattern_and_one_family_twice_does_not()
    {
        Assert.Equal(RiskLevel.SuspiciousPattern, RiskRules.Pattern(
            [(RiskFamily.Sim, RiskLevel.RiskSignal), (RiskFamily.Number, RiskLevel.RiskSignal)]));
        Assert.Equal(RiskLevel.RiskSignal, RiskRules.Pattern(
            [(RiskFamily.Sim, RiskLevel.RiskSignal), (RiskFamily.Sim, RiskLevel.RiskSignal), (RiskFamily.Number, RiskLevel.Anomaly)]));
        Assert.Equal(RiskLevel.Anomaly, RiskRules.Pattern(
            [(RiskFamily.Imei, RiskLevel.Anomaly), (RiskFamily.Number, RiskLevel.Anomaly)]));
    }

    /// <summary>
    /// Words a reason never uses: a finding of guilt, a mechanism the data cannot see, or a unit the
    /// data does not count ("users", "phones" - it counts numbers, SIMs and IMEIs).
    /// </summary>
    private static readonly string[] Forbidden = ["fraud", "cloned", "sim box", "simbox", "criminal", "user", "phone ", "device"];

    /// <summary>
    /// Every combination of rule, value, window coverage, defect share and flagged day: the system's
    /// highest word is Risk Signal for one entity, Suspicious Pattern for linked ones - never fraud.
    /// </summary>
    [Fact]
    public void No_input_ever_produces_confirmed_fraud()
    {
        var values = new long[] { 0, 1, 2, 10, 11, 50, 51, 200, 201, 5_000, long.MaxValue / 2 };
        var flags = new[]
        {
            Array.Empty<FlaggedDay>(),
            [new FlaggedDay(Through, FeedQualityCheck.MultiNumberSim)],
            [new FlaggedDay(Through, FeedQualityCheck.ShiftedImei)],
            [new FlaggedDay(Through, FeedQualityCheck.UnknownDevice)],
        };
        var seen = new HashSet<RiskLevel>();

        foreach (var spec in RiskRules.Catalogue)
        foreach (var value in values)
        foreach (var flag in flags)
        foreach (var setAside in new long[] { 0, 400, 900 })
        {
            var measure = new RiskMeasure(spec.Rule, value, spec.WindowDays is { } w ? Window(w) : null, Tacs: value);
            var result = RiskRules.Assess(new RiskEvidence(spec.Family, [measure], 1_000, setAside), Settings(), flag);

            Assert.True(result.Level <= RiskLevel.RiskSignal, $"{spec.Rule} {value}: {result.Level}");
            Assert.All(result.Reasons, r => Assert.All(Forbidden, word => Assert.DoesNotContain(word, r.Text, StringComparison.OrdinalIgnoreCase)));
            seen.Add(RiskRules.Pattern([(spec.Family, result.Level), (RiskFamily.Number, result.Level)]));
            seen.Add(result.Level);
        }

        Assert.DoesNotContain(RiskLevel.ConfirmedFraud, seen);
        Assert.Contains(RiskLevel.RiskSignal, seen);   // the grid does reach the top a system may say
    }
}
