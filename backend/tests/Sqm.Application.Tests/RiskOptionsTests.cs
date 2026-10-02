using Sqm.Application.Risk;
using Sqm.Domain.Risk;

namespace Sqm.Application.Tests;

/// <summary>
/// The rule set refuses thresholds its stored rows cannot answer, and its version changes with every
/// value that changes a verdict or a stored row - and with nothing else.
/// </summary>
public sealed class RiskOptionsTests
{
    [Fact]
    public void Nothing_is_judged_until_a_threshold_is_set()
    {
        var options = new RiskOptions();

        Assert.Empty(options.Problems());
        Assert.Empty(options.ThresholdsByRule());
        Assert.Equal(1.0, options.Settings().MaxDefectShare);
    }

    [Theory]
    [InlineData(4, true)]    // lists 5 and up; 5 is below the floor of 6 and was never stored
    [InlineData(5, false)]   // lists 6 and up: every one stored
    [InlineData(6, false)]
    public void A_threshold_below_its_floor_is_refused(long threshold, bool refused)
    {
        var options = new RiskOptions();
        options.Thresholds.HighDeviceCount30 = threshold;

        Assert.Equal(refused, options.Problems().Any(p => p.Contains("HighDeviceCount30", StringComparison.Ordinal)));
    }

    [Fact]
    public void Each_rule_is_checked_against_its_own_floor()
    {
        var options = new RiskOptions();
        options.Thresholds.SharedImeiSimsEver = 10;        // floor 21
        options.Thresholds.SharedImeiSimsNotRemoved = 10;  // floor 6
        options.Thresholds.SharedImeiNumbers30 = 4;        // floor 6

        var problems = options.Problems();
        Assert.Contains(problems, p => p.Contains("SharedImeiSimsEver is 10, below the storage floor 21", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("SharedImeiNumbers30 is 4", StringComparison.Ordinal));
        Assert.DoesNotContain(problems, p => p.Contains("SharedImeiSimsNotRemoved", StringComparison.Ordinal));
    }

    [Fact]
    public void The_randomisation_pair_is_set_together()
    {
        var options = new RiskOptions();
        options.Thresholds.Randomisation20Imeis = 20;
        Assert.Contains(options.Problems(), p => p.Contains("set together", StringComparison.Ordinal));
        Assert.DoesNotContain(RiskRule.Randomisation20, options.ThresholdsByRule().Keys);

        options.Thresholds.Randomisation20Tacs = 10;
        Assert.Empty(options.Problems());
        Assert.Equal(new RiskThreshold(20, 10), options.ThresholdsByRule()[RiskRule.Randomisation20]);
    }

    [Fact]
    public void Shares_and_chunks_are_range_checked()
    {
        var options = new RiskOptions { MaxDefectShare = 1.5 };
        options.Compute.Chunks = 0;

        Assert.Equal(2, options.Problems().Count);
    }

    [Fact]
    public void The_version_follows_thresholds_shares_and_floors_but_not_compute()
    {
        var baseline = new RiskOptions().Version();
        Assert.Equal(8, baseline.Length);
        Assert.Equal(baseline, new RiskOptions().Version());

        var threshold = new RiskOptions();
        threshold.Thresholds.RepeatedSimChange7 = 3;
        var share = new RiskOptions { MaxDefectShare = 0.5 };
        var floor = new RiskOptions();
        floor.Floors.ImeiSimsEver = 30;
        var compute = new RiskOptions();
        compute.Compute.Chunks = 8;

        Assert.Equal(4, new[] { baseline, threshold.Version(), share.Version(), floor.Version() }.Distinct().Count());
        Assert.Equal(baseline, compute.Version());
        Assert.Equal(threshold.Version(), threshold.Settings().Version);
    }
}
