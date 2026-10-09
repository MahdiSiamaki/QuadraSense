using System.Collections.Frozen;
using System.Text.Json;
using Sqm.Application.Dashboards;
using Sqm.Application.Identity;
using Sqm.Contracts.Dashboards;

namespace Sqm.Application.Tests.Dashboards;

/// <summary>
/// The lens a widget is computed with, who may do what with a dashboard, and which permissions a
/// widget needs. The lens cases are the same table frontend/tests/unit pins for composables/lens.ts:
/// the two sides must agree, case by case.
/// </summary>
public sealed class DashboardLensAndAccessTests
{
    private static readonly WidgetContract Contract = WidgetContract.Load();

    private static WidgetTypeSpec Spec(string type) => Contract.Types[type];

    private static AuthenticatedUser User(long id, params string[] permissions) =>
        new(id, $"user{id}", $"User {id}", false, permissions.ToFrozenSet(StringComparer.Ordinal));

    // ------------------------------------------------------------------- lens

    [Fact]
    public void L1_the_url_wins_over_the_defaults_for_a_field_the_type_supports()
    {
        var lens = DashboardLensResolver.Effective(
            Spec("device.top"), new DashboardLens(CountBy: "handsets", Vendor: "Samsung"), new DashboardLens(Vendor: "Apple"), new DashboardLens());

        Assert.Equal(new DashboardLens(CountBy: "handsets", IncludeUnknownDevice: true, Vendor: "Apple"), lens);
    }

    [Fact]
    public void L2_an_override_wins_over_both()
    {
        var lens = DashboardLensResolver.Effective(
            Spec("device.top"), new DashboardLens(CountBy: "handsets"), new DashboardLens(), new DashboardLens(CountBy: "subscribers"));

        Assert.Equal("subscribers", lens.CountBy);
    }

    [Fact]
    public void L3_a_network_wide_series_takes_the_range_and_nothing_else()
    {
        var lens = DashboardLensResolver.Effective(
            Spec("change.daily"), new DashboardLens(Range: "last30", Vendor: "Samsung"), new DashboardLens(), new DashboardLens());

        Assert.Equal(new DashboardLens(Range: "last30"), lens);
    }

    [Fact]
    public void L4_a_network_wide_breakdown_takes_the_count_unit_only()
    {
        var lens = DashboardLensResolver.Effective(
            Spec("device.classMix"), new DashboardLens(), new DashboardLens(CountBy: "subscribers", Vendor: "X"), new DashboardLens());

        Assert.Equal(new DashboardLens(CountBy: "subscribers"), lens);
    }

    [Fact]
    public void L5_a_kpi_has_its_own_unit_but_honours_unknown_devices()
    {
        var lens = DashboardLensResolver.Effective(
            Spec("metric.kpi"), new DashboardLens(), new DashboardLens(CountBy: "handsets", IncludeUnknownDevice: false), new DashboardLens());

        Assert.Null(lens.CountBy);
        Assert.False(lens.IncludeUnknownDevice);
    }

    [Fact]
    public void L6_a_unit_the_type_cannot_count_by_falls_back_to_its_first()
    {
        var bindingsOnly = Spec("device.top") with { Measures = ["bindings"] };
        var lens = DashboardLensResolver.Effective(bindingsOnly, new DashboardLens(), new DashboardLens(CountBy: "handsets"), new DashboardLens());

        Assert.Equal("bindings", lens.CountBy);
    }

    [Fact]
    public void R1_to_R3_ranges_count_back_from_the_data_not_from_today()
    {
        Assert.Equal((new DateOnly(2026, 9, 7), new DateOnly(2026, 10, 6)), DashboardLensResolver.ResolveRange("last30", new DateOnly(2026, 10, 6)));
        Assert.Equal(((DateOnly?)null, (DateOnly?)null), DashboardLensResolver.ResolveRange("last30", null));
        Assert.Equal((new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31)), DashboardLensResolver.ResolveRange("2026-08-01..2026-08-31", null));
        Assert.Equal(((DateOnly?)null, (DateOnly?)null), DashboardLensResolver.ResolveRange("all", new DateOnly(2026, 10, 6)));
    }

    [Theory]
    [InlineData("2026-02-30..2026-03-01", false)]
    [InlineData("2026-09-01..2026-08-01", false)]
    [InlineData("last7", true)]
    [InlineData("last8", false)]
    [InlineData("all", true)]
    [InlineData("2026-08-01..2026-08-31", true)]
    public void R4_only_ranges_the_contract_allows_are_valid(string range, bool valid) =>
        Assert.Equal(valid, DashboardLensResolver.IsValidRange(range, Contract.Lens));

    [Fact]
    public void S1_a_hand_edited_url_degrades_to_no_filter()
    {
        var lens = DashboardLensResolver.Sanitize(
            new DashboardLens(CountBy: "devices", Vendor: "  Samsung ", Tac: "1234567", DeviceType: new string('x', 121)), Contract.Lens);

        Assert.Null(lens.CountBy);
        Assert.Equal("Samsung", lens.Vendor);
        Assert.Null(lens.Tac);
        Assert.Null(lens.DeviceType);
    }

    // ----------------------------------------------------------------- access

    [Fact]
    public void An_owner_without_dashboard_edit_cannot_edit_but_can_delete()
    {
        var owner = User(1, Permissions.DashboardView);
        var level = DashboardAccess.Resolve(1, null, owner);

        Assert.Equal(DashboardAccessLevel.Owner, level);
        Assert.False(DashboardAccess.CanEdit(level, owner));
        Assert.True(DashboardAccess.CanDelete(level));
        Assert.False(DashboardAccess.CanShare(level, owner));
    }

    [Fact]
    public void An_editor_grant_edits_only_with_dashboard_edit_and_never_shares_or_deletes()
    {
        var withEdit = User(2, Permissions.DashboardView, Permissions.DashboardEdit, Permissions.DashboardShare);
        var level = DashboardAccess.Resolve(1, GrantLevels.Editor, withEdit);

        Assert.Equal(DashboardAccessLevel.Editor, level);
        Assert.True(DashboardAccess.CanEdit(level, withEdit));
        Assert.False(DashboardAccess.CanShare(level, withEdit));
        Assert.False(DashboardAccess.CanDelete(level));

        var withoutEdit = User(3, Permissions.DashboardView);
        Assert.False(DashboardAccess.CanEdit(DashboardAccess.Resolve(1, GrantLevels.Editor, withoutEdit), withoutEdit));
    }

    [Fact]
    public void A_viewer_grant_opens_and_nothing_more()
    {
        var viewer = User(4, Permissions.DashboardView, Permissions.DashboardEdit);
        var level = DashboardAccess.Resolve(1, GrantLevels.Viewer, viewer);

        Assert.Equal(DashboardAccessLevel.Viewer, level);
        Assert.False(DashboardAccess.CanEdit(level, viewer));
        Assert.False(DashboardAccess.CanShare(level, viewer));
        Assert.False(DashboardAccess.CanDelete(level));
    }

    [Fact]
    public void Dashboard_manage_reaches_every_dashboard_and_outranks_a_grant()
    {
        var admin = User(5, Permissions.DashboardView, Permissions.DashboardManage);
        var level = DashboardAccess.Resolve(1, GrantLevels.Viewer, admin);

        Assert.Equal(DashboardAccessLevel.Manager, level);
        Assert.True(DashboardAccess.CanEdit(level, admin));
        Assert.True(DashboardAccess.CanShare(level, admin));
        Assert.True(DashboardAccess.CanDelete(level));
        Assert.Equal(DashboardAccessLevel.Manager, DashboardAccess.Resolve(1, null, admin));
    }

    [Fact]
    public void Nobody_else_reaches_it()
    {
        var stranger = User(6, Permissions.DashboardView, Permissions.DashboardEdit, Permissions.DashboardShare, Permissions.DashboardCreate);
        var level = DashboardAccess.Resolve(1, null, stranger);

        Assert.Equal(DashboardAccessLevel.None, level);
        Assert.False(DashboardAccess.CanDuplicate(level, stranger));
        Assert.Equal("none", DashboardAccess.Describe(level, stranger).Level);
    }

    [Fact]
    public void Duplicating_needs_dashboard_create_and_exporting_needs_data_export()
    {
        var viewer = User(7, Permissions.DashboardView);
        var level = DashboardAccess.Resolve(1, GrantLevels.Viewer, viewer);
        Assert.False(DashboardAccess.CanDuplicate(level, viewer));
        Assert.False(DashboardAccess.CanExportData(level, viewer));

        var analyst = User(8, Permissions.DashboardView, Permissions.DashboardCreate, Permissions.DataExport);
        var analystLevel = DashboardAccess.Resolve(1, GrantLevels.Viewer, analyst);
        Assert.True(DashboardAccess.CanDuplicate(analystLevel, analyst));
        Assert.True(DashboardAccess.CanExportData(analystLevel, analyst));
    }

    [Fact]
    public void An_owner_shares_only_while_holding_dashboard_share()
    {
        var owner = User(9, Permissions.DashboardView, Permissions.DashboardShare);
        Assert.True(DashboardAccess.CanShare(DashboardAccess.Resolve(9, null, owner), owner));
    }

    // ------------------------------------------------------------ permissions

    [Fact]
    public void A_risk_list_needs_the_lookup_permission_of_its_rule_family()
    {
        var spec = Spec("risk.list");
        var config = JsonSerializer.SerializeToElement(new { rule = "RepeatedSimChange7" });

        Assert.Equal(["risk.view", "lookup.subscriber"], WidgetPermissions.Required(spec, config));

        var riskOnly = User(10, Permissions.RiskView);
        Assert.Equal(["lookup.subscriber"], WidgetPermissions.Missing(spec, config, riskOnly));
    }

    [Fact]
    public void Only_the_rules_whose_lookup_the_person_holds_are_offered()
    {
        var simsOnly = User(11, Permissions.RiskView, Permissions.LookupImsi);
        var options = WidgetPermissions.AvailableOptions(Spec("risk.list"), simsOnly)!;

        Assert.Equal(["HighDeviceCount30", "Randomisation20", "RapidDeviceChange7"], options.Order(StringComparer.Ordinal));
        Assert.Null(WidgetPermissions.AvailableOptions(Spec("device.top"), simsOnly));
    }
}
