using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sqm.Application.Risk;

namespace Sqm.Integration.Tests;

/// <summary>
/// The rule set the API ships with is the one ADR-014 records: every rule calibrated, nothing below its
/// storage floor, and the defect share at a half.
/// </summary>
public sealed class RiskConfigurationTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public void The_shipped_rule_set_is_complete_and_valid()
    {
        var options = factory.Services.GetRequiredService<IOptions<RiskOptions>>().Value;

        Assert.Empty(options.Problems());
        Assert.Equal(8, options.ThresholdsByRule().Count);
        Assert.Equal(0.5, options.MaxDefectShare);
        Assert.Equal(188, options.Thresholds.HighDeviceCount30);
    }
}
