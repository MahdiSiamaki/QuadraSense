using Sqm.Contracts.Dashboard;
using Sqm.Infrastructure.ClickHouse;

namespace Sqm.Integration.Tests;

/// <summary>
/// Which filters one finished row of agg_filter_population answers. A wrong yes serves one
/// population's figures for another question, and nothing downstream could tell; a wrong no only
/// costs the seconds a merge takes.
/// </summary>
public sealed class MartRoutingSingleFilterTests
{
    [Theory]
    [InlineData("deviceType", "Smartphone")]
    [InlineData("operatingSystem", "Android")]
    [InlineData("manufacturer", "Samsung Korea")]
    [InlineData("vendor", "Samsung")]
    public void One_catalogue_filter_is_one_row(string dimension, string value)
    {
        var filter = dimension switch
        {
            "deviceType" => new DashboardFilter(DeviceType: value),
            "operatingSystem" => new DashboardFilter(OperatingSystem: value),
            "manufacturer" => new DashboardFilter(Manufacturer: value),
            _ => new DashboardFilter(VendorCanonical: value),
        };

        Assert.Equal((dimension, value), MartRouting.SingleFilter(filter));
    }

    [Fact]
    public void The_value_is_trimmed_as_the_filter_trims_it()
    {
        Assert.Equal(("deviceType", "Smartphone"), MartRouting.SingleFilter(new DashboardFilter(DeviceType: "  Smartphone ")));
    }

    [Fact]
    public void The_unknown_device_switch_does_not_change_a_catalogue_filter()
    {
        // A model the catalogue describes is never the '' model the switch removes.
        Assert.Equal(("vendor", "Apple"),
            MartRouting.SingleFilter(new DashboardFilter(VendorCanonical: "Apple", IncludeUnknownDevice: false)));
    }

    [Fact]
    public void Excluding_unknown_devices_alone_is_its_own_row()
    {
        Assert.Equal(("knownDevice", ""), MartRouting.SingleFilter(new DashboardFilter(IncludeUnknownDevice: false)));
    }

    [Fact]
    public void No_filter_is_not_a_row_of_this_table()
    {
        // Unfiltered figures come from the KPI mart, which this table does not replace.
        Assert.Null(MartRouting.SingleFilter(new DashboardFilter()));
    }

    [Fact]
    public void Anything_else_is_not_one_row()
    {
        // Two filters: distinct counts do not combine across rows.
        Assert.Null(MartRouting.SingleFilter(new DashboardFilter(VendorCanonical: "Samsung", DeviceType: "Smartphone")));
        Assert.Null(MartRouting.SingleFilter(new DashboardFilter(Manufacturer: "Samsung Korea", OperatingSystem: "Android")));

        // A TAC is one per-model row already.
        Assert.Null(MartRouting.SingleFilter(new DashboardFilter(Tac: "35446411")));
        Assert.Null(MartRouting.SingleFilter(new DashboardFilter(DeviceType: "Smartphone", Tac: "35446411")));

        // A number prefix is a property of the subscriber, not of the model.
        Assert.Null(MartRouting.SingleFilter(new DashboardFilter(DeviceType: "Smartphone", MsisdnPrefix: "912")));
        Assert.Null(MartRouting.SingleFilter(new DashboardFilter(IncludeUnknownDevice: false, MsisdnPrefix: "912")));

        // Blank is no filter, not a filter on ''.
        Assert.Null(MartRouting.SingleFilter(new DashboardFilter(DeviceType: "   ")));
    }
}
