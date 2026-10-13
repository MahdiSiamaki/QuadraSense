using System.Collections.Frozen;
using System.Text.Json;
using Sqm.Application.Abstractions;
using Sqm.Application.Dashboards;
using Sqm.Application.DataImport;
using Sqm.Application.Devices;
using Sqm.Application.Identity;
using Sqm.Application.Quality;
using Sqm.Application.Risk;
using Sqm.Contracts.Dashboard;
using Sqm.Contracts.Dashboards;
using Sqm.Domain.Quality;
using Sqm.Domain.Risk;

namespace Sqm.Integration.Tests;

/// <summary>
/// The export loader's tables against what each card shows, over stub stores: the parity defects the
/// review confirmed - sort order, the rows a setting hides, readiness, scope, uncalibrated rules.
/// </summary>
public sealed class DashboardWidgetDataLoaderTests
{
    private static readonly WidgetContract Contract = WidgetContract.Load();

    private static readonly AuthenticatedUser Everyone = new(1, "user1", "Person 1", false, Permissions.All);

    private int _simChangeReads;

    private DashboardWidgetDataLoader Loader(
        IReadOnlyList<DimensionCount>? top = null, IReadOnlyList<DailyChange>? changes = null,
        RiskReadState? risk = null, RiskOptions? riskOptions = null, IReadOnlyList<RiskRuleCount>? counts = null,
        IReadOnlyList<RiskChangeDay>? simDays = null)
    {
        var analytics = TestStubs.Create<IDeviceAnalyticsStore>((m, _) => m.Name switch
        {
            "GetTopDimensionAsync" => Task.FromResult(top ?? []),
            "GetDailyChangesAsync" => Task.FromResult(changes ?? []),
            "GetDailyChurnAsync" => Task.FromResult<IReadOnlyList<DailyChurn>>([]),
            _ => throw new NotSupportedException(m.Name),
        });
        var reader = TestStubs.Create<IRiskReader>((m, _) => m.Name switch
        {
            "GetStateAsync" => Task.FromResult(risk ?? new RiskReadState(true, null, null, null, new HashSet<DateOnly>())),
            "DeviceTypesAsync" => Task.FromResult<IReadOnlyList<string>>([]),
            "CountAsync" => Task.FromResult(counts ?? []),
            "SimChangeDaysAsync" => Task.FromResult(Read(simDays ?? [])),
            _ => throw new NotSupportedException(m.Name),
        });
        var feed = TestStubs.Create<IFeedQualityReader>((m, _) => m.Name == "GetDaysAsync"
            ? Task.FromResult<IReadOnlyList<FeedQualityDay>>([])
            : throw new NotSupportedException(m.Name));

        return new DashboardWidgetDataLoader(
            analytics,
            TestStubs.Create<IImportJobRepository>((m, _) => throw new NotSupportedException(m.Name)),
            reader,
            feed,
            TestStubs.Create<IQualityReader>((m, _) => throw new NotSupportedException(m.Name)),
            TestStubs.Create<IModelArrivalReader>((m, _) => throw new NotSupportedException(m.Name)),
            TestStubs.Create<IDeviceImageStore>((m, _) => throw new NotSupportedException(m.Name)),
            new FeedQualityOptions(),
            () => riskOptions ?? Calibrated(),
            TimeProvider.System);
    }

    private IReadOnlyList<RiskChangeDay> Read(IReadOnlyList<RiskChangeDay> days)
    {
        _simChangeReads++;
        return days;
    }

    private static RiskOptions Calibrated() => new()
    {
        Thresholds = new RiskThresholdOptions
        {
            SharedImeiSims30 = 9, SharedImeiNumbers30 = 9, SharedImeiSimsEver = 40, SharedImeiSimsNotRemoved = 20,
            HighDeviceCount30 = 9, RapidDeviceChange7 = 5, Randomisation20Imeis = 9, Randomisation20Tacs = 3, RepeatedSimChange7 = 3,
        },
    };

    private static RiskReadState Published() => new(
        true, new RiskPublishedRun(1, new DateOnly(2026, 9, 30), 7, DateTimeOffset.UtcNow), null, new DateOnly(2026, 9, 30),
        new HashSet<DateOnly>());

    private static Task<WidgetDataResult> LoadAsync(DashboardWidgetDataLoader loader, string type, string config, DashboardLens? lens = null) =>
        loader.LoadAsync(
            new ValidatedWidget("w_test0001", type, 1, null, null, JsonDocument.Parse(config).RootElement.Clone(), new DashboardLens(), new WidgetLayout(0, 0, 6, 6)),
            Contract.Types[type], lens ?? new DashboardLens(), new WidgetLoadContext(Everyone, new DateOnly(2026, 3, 8), new DateOnly(2026, 10, 12)),
            TestContext.Current.CancellationToken);

    private static void EveryRowFitsTheHeadings(WidgetTable table) =>
        Assert.All(table.Rows, row => Assert.Equal(table.Headers.Count, row.Count));

    [Fact]
    public async Task Top_values_sorted_by_name_come_out_in_the_cards_order()
    {
        var loader = Loader(top: [new("Redmi Note 8", 30, 30), new("iPhone 13", 20, 20), new("Galaxy A54", 10, 10)]);
        var result = await LoadAsync(loader, "device.top", """{"dimension":"marketingName","limit":10,"sort":"name"}""");

        Assert.Equal(["Galaxy A54", "iPhone 13", "Redmi Note 8"], result.Table!.Rows.Select(r => (string)r[0]!));
    }

    [Fact]
    public async Task Period_comparison_writes_only_the_change_the_card_shows_one_cell_per_heading()
    {
        DailyChange Day(int d, long added) => new(new DateOnly(2026, 3, d), added, 0, added, 1000 + added, 0);
        var loader = Loader(changes: [Day(1, 10), Day(2, 20), Day(3, 30), Day(4, 40), Day(5, 50), Day(7, 70), Day(8, 80)]);
        var config = """{"metric":"added","baseline":"previous","baselineFrom":null,"baselineTo":null,"aggregate":"total","change":"absolute"}""";

        var result = await LoadAsync(loader, "change.periodComparison", config, new DashboardLens(Range: "2026-03-05..2026-03-08"));

        var labels = result.Table!.Rows.Select(r => (string)r[0]!).ToList();
        Assert.Equal(["Dashboard range", "Baseline", "Change"], labels);
        Assert.Equal(100.0, result.Table.Rows[2][^1]);
        EveryRowFitsTheHeadings(result.Table);
    }

    [Fact]
    public async Task SIM_change_evidence_waits_for_a_ready_risk_status_as_the_card_does()
    {
        var loader = Loader(risk: new RiskReadState(true, null, null, null, new HashSet<DateOnly>()));
        var result = await LoadAsync(loader, "risk.simChangeEvidence", """{"series":["changes","setAside"],"showFlagged":true}""");

        Assert.Equal("notReady", result.Status);
        Assert.Equal(0, _simChangeReads);
    }

    [Fact]
    public async Task SIM_change_evidence_of_one_series_is_the_visible_rows_with_that_series_only()
    {
        // Inside the last 7 days of the context's data (to 2026-03-08), and one day outside it.
        var loader = Loader(risk: Published(), simDays:
        [
            new(new DateOnly(2026, 2, 20), 90, 9, 4), new(new DateOnly(2026, 3, 7), 100, 10, 5), new(new DateOnly(2026, 3, 8), 120, 12, 6),
        ]);
        var result = await LoadAsync(loader, "risk.simChangeEvidence", """{"series":["changes"],"showFlagged":false}""", new DashboardLens(Range: "last7"));

        Assert.Equal("ok", result.Status);
        Assert.Equal("visible", result.Table!.Scope);
        Assert.Equal(["Day", "Numbers whose SIM changed"], result.Table.Headers);
        Assert.Equal([["2026-03-07", 100L], ["2026-03-08", 120L]], result.Table.Rows.Select(r => r.ToArray()));
        EveryRowFitsTheHeadings(result.Table);
    }

    [Fact]
    public async Task Risk_overview_keeps_every_rule_the_card_lists_an_uncalibrated_one_too()
    {
        var partly = new RiskOptions { Thresholds = new RiskThresholdOptions { SharedImeiSims30 = 9 } };
        var loader = Loader(risk: Published(), riskOptions: partly, counts: [new(RiskRule.SharedImeiSims30, 5, 1)]);
        var result = await LoadAsync(loader, "risk.overview", """{"rules":["SharedImeiSims30","HighDeviceCount30"],"display":"table"}""");

        Assert.Equal("ok", result.Status);
        Assert.Equal(2, result.Table!.Rows.Count);
        Assert.Equal("Not calibrated: nothing is judged.", result.Table.Rows[1][^1]);
        EveryRowFitsTheHeadings(result.Table);
    }
}
