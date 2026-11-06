using System.Text.RegularExpressions;
using Sqm.Contracts.Dashboard;
using Sqm.Infrastructure.ClickHouse;

namespace Sqm.Integration.Tests;

/// <summary>
/// A filter names only what the table it is written for holds. The real-data tests prove this against
/// the store; these prove it with no database, because the pipeline has none and skips those.
/// </summary>
public sealed partial class FilterBuilderTests
{
    private static readonly DashboardFilter EveryModelFilter = new(
        Manufacturer: "m", VendorCanonical: "v", DeviceType: "d", OperatingSystem: "o", Tac: "35000000");

    [GeneratedRegex(@"\bb\.(\w+)")]
    private static partial Regex ColumnOfTheRow();

    private static string[] ColumnsAsked(FilterBuilder filter) =>
        [.. ColumnOfTheRow().Matches(filter.WhereClause).Select(m => m.Groups[1].Value).Distinct()];

    [Fact]
    public void The_model_mart_filter_asks_nothing_of_a_row_but_its_model()
    {
        // agg_tac_population has one row per model: no IMEI, no number. Asking for either fails the
        // statement, which is how every chart beside a filter went down on 2026-11-06.
        Assert.Equal(["tac"], ColumnsAsked(FilterBuilder.ForTacMart(EveryModelFilter)));
    }

    [Fact]
    public void The_model_mart_filter_leaves_out_the_empty_model_unless_asked_not_to()
    {
        Assert.Contains("b.tac != ''", FilterBuilder.ForTacMart(EveryModelFilter).WhereClause, StringComparison.Ordinal);

        Assert.DoesNotContain(
            "b.tac != ''",
            FilterBuilder.ForTacMart(EveryModelFilter with { IncludeUnknownDevice = true }).WhereClause,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_model_mart_filter_refuses_what_it_cannot_say()
    {
        // A number's prefix is not a property of a model. Dropped quietly, the answer would be a
        // plausible figure for a question nobody asked.
        Assert.Throws<ArgumentException>(() => FilterBuilder.ForTacMart(new DashboardFilter(MsisdnPrefix: "912")));
    }

    [Fact]
    public void The_row_filter_asks_the_row_for_its_imei()
    {
        Assert.Contains("imei", ColumnsAsked(FilterBuilder.ForCurrentState(EveryModelFilter)));
    }
}
