using System.Globalization;
using System.Text.Json;
using Sqm.Application.Abstractions;
using Sqm.Application.DataImport;
using Sqm.Application.Devices;
using Sqm.Application.Identity;
using Sqm.Application.Quality;
using Sqm.Application.Risk;
using Sqm.Contracts.Dashboard;
using Sqm.Contracts.Dashboards;

namespace Sqm.Application.Dashboards;

/// <summary>
/// Each widget type's export data, read through the very compositions its own endpoint uses
/// (ADR-016 §11) - an allow-list, one adapter per type, nothing generic.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same payload as the card.</b> <c>Data</c> is an object keyed by source, each value exactly
/// what that source's endpoint returns (<c>frontend/src/features/dashboard/types/snapshot.ts</c>), so a
/// widget draws an export with the code it draws a live query with. Optional sources a card reads only
/// with import.view are null without it, as they are on screen.
/// </para>
/// <para>
/// <b>The table is what the card shows.</b> A series is cut to the widget's range, a top list to its
/// limit, and the scope says which: <c>visible</c>, <c>topN</c> or <c>full</c>. A capped file is never
/// called full.
/// </para>
/// <para>
/// <b>One request, one read per source.</b> Scoped per request, and several widgets reading the daily
/// series, the feed-quality days or the risk context share one read.
/// </para>
/// </remarks>
public sealed partial class DashboardWidgetDataLoader : IWidgetDataLoader
{
    private const string Ok = "ok";
    private const string Visible = "visible";
    private const string TopN = "topN";
    private const string Full = "full";
    private const string Estimated = "Subscribers and handsets are HyperLogLog estimates (about 0.5% error).";
    private const string NetworkWide = "Network-wide: this source has no vendor or device breakdown, so the dashboard's filters do not apply.";

    private readonly IDeviceAnalyticsStore _analytics;
    private readonly IImportJobRepository _imports;
    private readonly IRiskReader _risk;
    private readonly IFeedQualityReader _feed;
    private readonly IQualityReader _quality;
    private readonly IModelArrivalReader _arrivals;
    private readonly IDeviceImageStore _images;
    private readonly FeedQualityOptions _feedOptions;
    private readonly Func<RiskOptions> _riskOptions;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, object> _memo = new(StringComparer.Ordinal);

    /// <summary>Creates the loader for one request.</summary>
    /// <remarks>The rule set is a function: it is read per request, as the risk routes read it.</remarks>
    public DashboardWidgetDataLoader(
        IDeviceAnalyticsStore analytics, IImportJobRepository imports, IRiskReader risk, IFeedQualityReader feed,
        IQualityReader quality, IModelArrivalReader arrivals, IDeviceImageStore images, FeedQualityOptions feedOptions,
        Func<RiskOptions> riskOptions, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(analytics);
        ArgumentNullException.ThrowIfNull(imports);
        ArgumentNullException.ThrowIfNull(risk);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(quality);
        ArgumentNullException.ThrowIfNull(arrivals);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(feedOptions);
        ArgumentNullException.ThrowIfNull(riskOptions);
        ArgumentNullException.ThrowIfNull(clock);

        _analytics = analytics;
        _imports = imports;
        _risk = risk;
        _feed = feed;
        _quality = quality;
        _arrivals = arrivals;
        _images = images;
        _feedOptions = feedOptions;
        _riskOptions = riskOptions;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<DateOnly?> DataThroughAsync(CancellationToken ct)
    {
        var changes = await DailyChangesAsync(ct).ConfigureAwait(false);
        return changes.Count == 0 ? null : changes.Max(c => c.Date);
    }

    /// <inheritdoc />
    public async Task<WidgetDataResult> LoadAsync(
        ValidatedWidget widget, WidgetTypeSpec spec, DashboardLens effective, WidgetLoadContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(widget);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(effective);
        ArgumentNullException.ThrowIfNull(context);

        var c = new Config(widget.Config);
        return widget.Type switch
        {
            "metric.kpi" => await KpiAsync(c, effective, [c.Str("metric")], ct).ConfigureAwait(false),
            "metric.kpiSummary" => await KpiAsync(c, effective, c.List("metrics"), ct).ConfigureAwait(false),
            "population.trend" => await PopulationTrendAsync(c, effective, context, ct).ConfigureAwait(false),
            "device.top" => await TopAsync(c, effective, ct).ConfigureAwait(false),
            "device.distribution" => await DistributionAsync(c, effective, ct).ConfigureAwait(false),
            "device.classMix" => await ClassMixAsync(effective, ct).ConfigureAwait(false),
            "device.capabilities" => await CapabilitiesAsync(c, effective, context, ct).ConfigureAwait(false),
            "device.newModels" => await NewModelsAsync(c, effective, ct).ConfigureAwait(false),
            "device.modelArrivals" => await ModelArrivalsAsync(c, ct).ConfigureAwait(false),
            "change.daily" => await ChangeDailyAsync(c, effective, context, ct).ConfigureAwait(false),
            "change.simHandset" => await SimHandsetAsync(c, effective, context, ct).ConfigureAwait(false),
            "change.vendorMovement" => await VendorMovementAsync(c, effective, context, ct).ConfigureAwait(false),
            "change.periodComparison" => await PeriodComparisonAsync(c, effective, context, ct).ConfigureAwait(false),
            "risk.status" => await RiskStatusAsync(c, ct).ConfigureAwait(false),
            "risk.overview" => await RiskOverviewAsync(c, ct).ConfigureAwait(false),
            "risk.distribution" => await RiskDistributionAsync(c, ct).ConfigureAwait(false),
            "risk.deviceTypes" => await RiskDeviceTypesAsync(c, ct).ConfigureAwait(false),
            "risk.simChangeEvidence" => await SimChangeEvidenceAsync(c, effective, context, ct).ConfigureAwait(false),
            "risk.evidenceQuality" => await EvidenceQualityAsync(c, ct).ConfigureAwait(false),
            "quality.enrichment" => await EnrichmentAsync(effective, ct).ConfigureAwait(false),
            "quality.feedTrend" => await FeedTrendAsync(c, effective, context, ct).ConfigureAwait(false),
            "quality.flaggedDays" => await FlaggedDaysAsync(c, effective, context, ct).ConfigureAwait(false),
            "quality.signals" => await SignalsAsync(c, ct).ConfigureAwait(false),
            "ops.freshness" => await FreshnessAsync(context, ct).ConfigureAwait(false),
            "ops.recentImports" => await RecentImportsAsync(c, ct).ConfigureAwait(false),
            "ops.importStatus" or "ops.workerHealth" => await WorkerHealthAsync(context, ct).ConfigureAwait(false),
            "ops.blockages" => await BlockagesAsync(c, context, ct).ConfigureAwait(false),
            "ops.tacVersion" => await TacVersionAsync(ct).ConfigureAwait(false),
            "tools.modelNetworkAge" => await ModelNetworkAgeAsync(c, ct).ConfigureAwait(false),
            "risk.list" or "tools.explorerAggregate" => new WidgetDataResult(
                "interactive", "Runs only on request, through its own audited endpoint: run it on the dashboard and export it from its own menu.",
                null, null, [], null),
            "tools.quickLookup" => new WidgetDataResult("noData", "A lookup box: it has no data of its own.", null, null, [], null),
            _ => new WidgetDataResult("error", "This widget type cannot be exported.", null, null, [], null),
        };
    }

    // ------------------------------------------------------------------ overview

    private static readonly Dictionary<string, (string Label, Func<KpiSummary, double> Value)> KpiMetrics =
        new Dictionary<string, (string, Func<KpiSummary, double>)>(StringComparer.Ordinal)
        {
            ["activeBindings"] = ("Active bindings", k => k.ActiveBindings),
            ["distinctSubscribers"] = ("Subscribers (estimated)", k => k.DistinctSubscribers),
            ["distinctSims"] = ("SIM cards (estimated)", k => k.DistinctSims),
            ["distinctDevices"] = ("Handsets (estimated)", k => k.DistinctDevices),
            ["unknownDeviceBindings"] = ("Unknown device, bindings", k => k.UnknownDeviceBindings),
            ["malformedImeiBindings"] = ("Malformed IMEI, bindings", k => k.MalformedImeiBindings),
            ["tacMatchedBindings"] = ("Enriched from GSMA, bindings", k => k.TacMatchedBindings),
            ["unregisteredTacBindings"] = ("Unregistered TAC, bindings", UnregisteredTac),
            ["tacCoveragePercent"] = ("TAC coverage, % of bindings", k => k.TacCoveragePercent),
        };

    /// <summary>From the exact counts, never from the rounded coverage percentage.</summary>
    private static double UnregisteredTac(KpiSummary k) =>
        k.ActiveBindings - k.TacMatchedBindings - k.UnknownDeviceBindings - k.MalformedImeiBindings;

    private async Task<WidgetDataResult> KpiAsync(Config c, DashboardLens lens, IReadOnlyList<string> metrics, CancellationToken ct)
    {
        _ = c;
        var kpi = await KpiSummaryAsync(Filter(lens), ct).ConfigureAwait(false);
        var rows = metrics.Where(KpiMetrics.ContainsKey).Select(m => Row(KpiMetrics[m].Label, KpiMetrics[m].Value(kpi))).ToList();
        return Done(Data(("kpi", kpi)), Iso(kpi.DeliveryDate), [Estimated], Table(["Figure", "Value"], rows, Visible, "The figures the card shows."));
    }

    private async Task<WidgetDataResult> PopulationTrendAsync(Config c, DashboardLens lens, WidgetLoadContext context, CancellationToken ct)
    {
        var changes = await DailyChangesAsync(ct).ConfigureAwait(false);
        var (feed, freshness) = await FeedSourcesAsync(context, c.Bool("showFlagged"), c.Bool("showMissing"), ct).ConfigureAwait(false);
        var shown = InWindow(changes, lens, context, d => d.Date);

        var grain = c.Str("grain");
        var buckets = shown
            .GroupBy(d => grain switch
            {
                "week" => d.Date.AddDays(-(((int)d.Date.DayOfWeek + 6) % 7)),
                "month" => new DateOnly(d.Date.Year, d.Date.Month, 1),
                _ => d.Date,
            })
            .Select(g => g.MaxBy(d => d.Date)!)
            .Select(d => Row(Iso(d.Date), d.Cumulative))
            .ToList();

        var note = grain == "day" ? "Each delivered day's active bindings." : $"Each {grain}'s last delivered day.";
        return Done(
            Data(("changes", changes), ("feedQuality", feed), ("freshness", freshness)),
            Iso(changes.Count == 0 ? null : changes[^1].Date),
            [NetworkWide, "A missing day is a gap, never a zero."],
            Table(["Day", "Active bindings"], buckets, Visible, $"{note} In the dashboard's range."));
    }

    // ------------------------------------------------------------------ devices

    private async Task<WidgetDataResult> TopAsync(Config c, DashboardLens lens, CancellationToken ct)
    {
        var dimension = Enum.Parse<AnalyticsDimension>(c.Str("dimension"), ignoreCase: true);
        var filter = Filter(lens) with { IncludeUnknownDevice = !c.Bool("excludeUnknownBucket") && (lens.IncludeUnknownDevice ?? true) };
        var limit = c.Int("limit", 10);
        var rows = await _analytics.GetTopDimensionAsync(dimension, filter, limit, CountOf(lens), ct).ConfigureAwait(false);
        // Alphabetical as the card sorts it: case-insensitive, ties broken by the exact text.
        IEnumerable<DimensionCount> ordered = c.Str("sort") == "name"
            ? rows.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Key, StringComparer.Ordinal)
            : rows;

        return Done(
            Data(("rows", rows)), null, Notes(lens),
            Table([c.Str("dimension"), Unit(lens), "Percent"], [.. ordered.Select(r => Row(r.Key, r.Count, r.Percent))], TopN,
                $"The top {limit} by {Unit(lens)}; the rest are not in this file."));
    }

    private async Task<WidgetDataResult> DistributionAsync(Config c, DashboardLens lens, CancellationToken ct)
    {
        var dimension = Enum.Parse<AnalyticsDimension>(c.Str("dimension"), ignoreCase: true);
        var rows = await _analytics.GetDistributionAsync(dimension, Filter(lens), CountOf(lens), ct).ConfigureAwait(false);
        var limit = c.Int("limit", 10);
        var shown = rows.Take(limit).Select(r => Row(r.Key, r.Count, r.Percent)).ToList();
        var capped = rows.Count > limit;

        return Done(
            Data(("rows", rows)), null, Notes(lens),
            Table([c.Str("dimension"), Unit(lens), "Percent"], shown, capped ? TopN : Full,
                capped ? $"The top {limit} of {rows.Count}." : "Every value."));
    }

    private async Task<WidgetDataResult> ClassMixAsync(DashboardLens lens, CancellationToken ct)
    {
        var rows = await _analytics.GetDeviceClassMixAsync(CountOf(lens), ct).ConfigureAwait(false);
        return Done(
            Data(("rows", rows)), null, [.. Notes(lens), NetworkWide],
            Table(["Device class", Unit(lens), "Percent"], [.. rows.Select(r => Row(r.Key, r.Count, r.Percent))], Full, "Every class."));
    }

    private async Task<WidgetDataResult> CapabilitiesAsync(Config c, DashboardLens lens, WidgetLoadContext context, CancellationToken ct)
    {
        var rows = await _analytics.GetCapabilitySupportAsync(CountOf(lens), ct).ConfigureAwait(false);
        IReadOnlyList<TacVersion>? tac = c.Bool("showVolteNote") && context.User.Can(Permissions.ImportView)
            ? await TacVersionsAsync(ct).ConfigureAwait(false)
            : null;
        var wanted = c.List("capabilities");

        return Done(
            Data(("capabilities", rows), ("tacVersions", tac)), null,
            [.. Notes(lens), NetworkWide, "VoLTE is not in the GSMA dataset and is not shown."],
            Table(
                ["Capability", "Supported", "Unsupported", "Unknown", "Percent of assessable", "Percent of all", "Coverage percent"],
                [.. rows.Where(r => wanted.Contains(r.Capability, StringComparer.Ordinal))
                    .Select(r => Row(r.Capability, r.Supported, r.Unsupported, r.Unknown, r.PercentOfAssessable, r.PercentOfAll, r.CoveragePercent))],
                Visible, "The capabilities the card shows."));
    }

    private async Task<WidgetDataResult> NewModelsAsync(Config c, DashboardLens lens, CancellationToken ct)
    {
        var state = await _arrivals.GetStateAsync(ct).ConfigureAwait(false);
        var status = ModelArrivalQueries.Status(state);
        if (ModelArrivalQueries.NotReady(state) is { } reason || state.DataThrough is not { } through)
        {
            return NotReady(ModelArrivalQueries.NotReady(state) ?? "No day is written yet.", Data(("status", status), ("models", null)));
        }

        var (from, to) = DashboardLensResolver.ResolveRange(lens.Range, through);
        var start = from ?? NetworkAge.DataStart;
        var end = to ?? through;
        var limit = c.Int("limit", 10);
        var query = ModelArrivalQueries.Query(start, end, c.Str("brand"), c.Str("deviceType"), c.Bool("knownOnly"), 1, limit);
        var page = await _arrivals.ListAsync(query, ct).ConfigureAwait(false);
        var models = ModelArrivalQueries.NewModels(page, query, through);

        return Done(
            Data(("status", status), ("models", models)), models.DataThrough,
            ["First seen is the first daily file that named the model - not a launch date.", "Handsets now are estimated."],
            Table(
                ["TAC", "Brand", "Model", "Device type", "First seen", "Network age (days)", "Handsets now (estimated)"],
                [.. models.Rows.Select(m => Row(m.Tac, m.Brand, m.Model, m.DeviceType, m.FirstSeen, m.NetworkAgeDays, m.Handsets))],
                models.Total > models.Rows.Count ? TopN : Full,
                $"{models.Rows.Count} of {models.Total} models first seen from {models.From} to {models.To}."));
    }

    private async Task<WidgetDataResult> ModelArrivalsAsync(Config c, CancellationToken ct)
    {
        var state = await _arrivals.GetStateAsync(ct).ConfigureAwait(false);
        var status = ModelArrivalQueries.Status(state);
        if (ModelArrivalQueries.NotReady(state) is { } reason)
        {
            return NotReady(reason, Data(("status", status), ("arrivals", null)));
        }

        var grain = c.Str("grain") == "week" ? "week" : "month";
        var periods = await _arrivals.ArrivalsAsync(grain == "week", ct).ConfigureAwait(false);
        var arrivals = ModelArrivalQueries.Arrivals(grain, periods);

        return Done(
            Data(("status", status), ("arrivals", arrivals)), status.DataThrough,
            ["The first weeks after the data starts are mostly models the initial dump's month happened to miss."],
            Table([grain == "week" ? "Week starting" : "Month starting", "Models first seen", "Known to GSMA"],
                [.. arrivals.Periods.Select(p => Row(p.PeriodStart, p.Models, p.KnownModels))], Full, "Every period."));
    }

    // ------------------------------------------------------------------ change

    private async Task<WidgetDataResult> ChangeDailyAsync(Config c, DashboardLens lens, WidgetLoadContext context, CancellationToken ct)
    {
        var changes = await DailyChangesAsync(ct).ConfigureAwait(false);
        var (feed, freshness) = await FeedSourcesAsync(context, c.Bool("showFlagged"), c.Bool("showMissing"), ct).ConfigureAwait(false);
        var series = c.List("series");
        var columns = new (string Key, string Header, Func<DailyChange, long> Value)[]
        {
            ("added", "Added", d => d.Added), ("removed", "Removed", d => d.Removed),
            ("net", "Net", d => d.Net), ("cumulative", "Active bindings (running total)", d => d.Cumulative),
        }.Where(x => series.Contains(x.Key, StringComparer.Ordinal)).ToList();

        var rows = InWindow(changes, lens, context, d => d.Date)
            .Select(d => (IReadOnlyList<object?>)[Iso(d.Date), .. columns.Select(x => (object?)x.Value(d))]).ToList();

        return Done(
            Data(("changes", changes), ("feedQuality", feed), ("freshness", freshness)),
            Iso(changes.Count == 0 ? null : changes[^1].Date),
            [NetworkWide, "A missing day has no row: it is a gap, never a zero."],
            Table(["Day", .. columns.Select(x => x.Header)], rows, Visible, "Every delivered day in the dashboard's range."));
    }

    private async Task<WidgetDataResult> SimHandsetAsync(Config c, DashboardLens lens, WidgetLoadContext context, CancellationToken ct)
    {
        var churn = await DailyChurnAsync(ct).ConfigureAwait(false);
        var (feed, _) = await FeedSourcesAsync(context, c.Bool("showFlagged"), false, ct).ConfigureAwait(false);
        var series = c.List("series");
        var sims = series.Contains("simChanges", StringComparer.Ordinal);
        var handsets = series.Contains("deviceChanges", StringComparer.Ordinal);

        var headers = new List<string> { "Day" };
        if (sims)
        {
            headers.Add("SIM changes");
        }

        if (handsets)
        {
            headers.Add("Handset changes");
        }

        var rows = InWindow(churn, lens, context, d => d.Date)
            .Select(d =>
            {
                var row = new List<object?> { Iso(d.Date) };
                if (sims)
                {
                    row.Add(d.SimChanges);
                }

                if (handsets)
                {
                    row.Add(d.DeviceChanges);
                }

                return (IReadOnlyList<object?>)row;
            })
            .ToList();

        return Done(
            Data(("churn", churn), ("feedQuality", feed)),
            Iso(churn.Count == 0 ? null : churn[^1].Date),
            [NetworkWide, "Same-day definition: a change spanning midnight is not counted, so these are a floor."],
            Table(headers, rows, Visible,
                "Every delivered day in the dashboard's range."));
    }

    private async Task<WidgetDataResult> VendorMovementAsync(Config c, DashboardLens lens, WidgetLoadContext context, CancellationToken ct)
    {
        var rank = c.Str("rank");
        var ranking = rank switch { "share" => VendorRanking.Share, "growth" => VendorRanking.Growth, _ => VendorRanking.Movement };
        var (from, to) = ranking == VendorRanking.Movement
            ? DashboardLensResolver.ResolveRange(lens.Range, context.DataThrough)
            : (null, null);
        var limit = c.Int("limit", 5);
        var movement = await _analytics.GetVendorMovementAsync(from, to, ranking, limit, ct).ConfigureAwait(false);

        var table = ranking switch
        {
            VendorRanking.Share => Table(["Vendor", "Active bindings", "Share percent"],
                [.. movement.Rows.Select(r => Row(r.Vendor, r.Population, r.SharePercent))], TopN, $"The top {limit} vendors by share."),
            VendorRanking.Growth => Table(["Vendor", "Bindings at start", "Bindings now", "Change percent", "Against the network, points"],
                [.. movement.Rows.Select(r => Row(r.Vendor, r.PopulationAtStart, r.Population, r.PopulationChangePercent, r.VsNetworkPoints))], TopN,
                $"The top {limit} vendors by growth against the network."),
            _ => Table(["Vendor", "Add events", "Remove events", "Net events", "Net, percent of population"],
                [.. movement.Rows.Select(r => Row(r.Vendor, r.Added, r.Removed, r.Net, r.NetPercentOfPopulation))], TopN,
                $"The top {limit} vendors by movement."),
        };

        return Done(
            Data(("movement", movement)), Iso(movement.LatestAvailable),
            ranking == VendorRanking.Movement
                ? ["Movement counts events, not devices: a SIM moving between handsets thirty times is thirty adds and thirty removes."]
                : movement.StartIsInitialDump && ranking == VendorRanking.Growth
                    ? ["The starting point is the initial dump, which covers a 30-day window: part of the network's decline is that over-count resolving."]
                    : [],
            table);
    }

    private async Task<WidgetDataResult> PeriodComparisonAsync(Config c, DashboardLens lens, WidgetLoadContext context, CancellationToken ct)
    {
        var metric = c.Str("metric");
        var churnMetric = PeriodComparison.UsesChurn(metric);
        IReadOnlyList<DailyChange> changes = churnMetric ? [] : await DailyChangesAsync(ct).ConfigureAwait(false);
        IReadOnlyList<DailyChurn> churn = churnMetric ? await DailyChurnAsync(ct).ConfigureAwait(false) : [];
        var series = PeriodComparison.Series(metric, changes, churn);
        var data = Data(("changes", churnMetric ? null : changes), ("churn", churnMetric ? churn : null));

        if (series.Count == 0)
        {
            return NotReady("No daily files loaded yet.", data);
        }

        var dataStart = series.Min(r => r.Date);
        var through = context.DataThrough ?? series.Max(r => r.Date);
        var (from, to) = DashboardLensResolver.ResolveRange(lens.Range, through);
        var result = PeriodComparison.Compare(
            metric, c.Str("aggregate"), c.Str("baseline"), c.Date("baselineFrom"), c.Date("baselineTo"),
            from ?? dataStart, to ?? through, series, dataStart);

        var rows = new List<IReadOnlyList<object?>> { PeriodRow("Dashboard range", result.Current) };
        if (result.Baseline is { } b)
        {
            rows.Add(PeriodRow("Baseline", b));
        }

        // The change rows the card shows: its "change" setting picks one or both, and none when there
        // is nothing to compare with. One cell per heading - label, from, to, days, value.
        var change = c.Str("change");
        if (result.Unavailable is null && change != "percent")
        {
            rows.Add(Row("Change", null, null, null, result.Absolute));
        }

        if (result.Unavailable is null && change != "absolute")
        {
            rows.Add(Row("Change, percent", null, null, null, result.Percent));
        }

        var notes = new List<string> { NetworkWide };
        if (result.Unavailable is { } why)
        {
            notes.Add(why);
        }

        if (result.Uneven)
        {
            notes.Add("The two periods have different numbers of delivered days, so their totals are not like for like.");
        }

        return Done(data, Iso(through), notes,
            Table(["Period", "From", "To", "Delivered days", $"{metric} ({c.Str("aggregate")})"], rows, Visible, "The comparison the card shows."));
    }

    private static object?[] PeriodRow(string label, PeriodFigure f) =>
        Row(label, Iso(f.From), Iso(f.To), $"{f.DeliveredDays} of {f.CalendarDays}", f.Value);

    // ------------------------------------------------------------------ shared reads

    private Task<KpiSummary> KpiSummaryAsync(DashboardFilter filter, CancellationToken ct) =>
        MemoAsync($"kpi:{JsonSerializer.Serialize(filter)}", () => _analytics.GetKpiSummaryAsync(filter, ct));

    private Task<IReadOnlyList<DailyChange>> DailyChangesAsync(CancellationToken ct) =>
        MemoAsync("daily-changes", () => _analytics.GetDailyChangesAsync(ct));

    private Task<IReadOnlyList<DailyChurn>> DailyChurnAsync(CancellationToken ct) =>
        MemoAsync("daily-churn", () => _analytics.GetDailyChurnAsync(ct));

    private Task<IReadOnlyList<TacVersion>> TacVersionsAsync(CancellationToken ct) =>
        MemoAsync("tac-versions", () => _imports.ListTacVersionsAsync(ct));

    private Task<IReadOnlyList<SourceFreshness>> FreshnessListAsync(CancellationToken ct) =>
        MemoAsync("freshness", () => _imports.GetFreshnessAsync(DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime), ct));

    private Task<Contracts.Quality.FeedQualityResponse> FeedQualityAsync(CancellationToken ct) =>
        MemoAsync("feed-quality", () => QualityQueries.FeedQualityAsync(_feed, _feedOptions, null, null, ct));

    /// <summary>The flagged-day and missing-day sources a daily chart reads, each only with import.view.</summary>
    private async Task<(Contracts.Quality.FeedQualityResponse? Feed, IReadOnlyList<SourceFreshness>? Freshness)> FeedSourcesAsync(
        WidgetLoadContext context, bool flagged, bool missing, CancellationToken ct)
    {
        var allowed = context.User.Can(Permissions.ImportView);
        var feed = allowed && flagged ? await FeedQualityAsync(ct).ConfigureAwait(false) : null;
        var freshness = allowed && missing ? await FreshnessListAsync(ct).ConfigureAwait(false) : null;
        return (feed, freshness);
    }

    private async Task<T> MemoAsync<T>(string key, Func<Task<T>> load)
        where T : class
    {
        if (_memo.TryGetValue(key, out var hit))
        {
            return (T)hit;
        }

        var value = await load().ConfigureAwait(false);
        _memo[key] = value;
        return value;
    }

    // ------------------------------------------------------------------ helpers

    private static IReadOnlyList<T> InWindow<T>(IReadOnlyList<T> rows, DashboardLens lens, WidgetLoadContext context, Func<T, DateOnly> date)
    {
        var through = context.DataThrough ?? (rows.Count == 0 ? (DateOnly?)null : rows.Max(date));
        var (from, to) = DashboardLensResolver.ResolveRange(lens.Range, through);
        return [.. rows.Where(r => (from is null || date(r) >= from) && (to is null || date(r) <= to)).OrderBy(date)];
    }

    private static DashboardFilter Filter(DashboardLens lens) => new(
        VendorCanonical: lens.Vendor, DeviceType: lens.DeviceType, OperatingSystem: lens.OperatingSystem, Tac: lens.Tac,
        IncludeUnknownDevice: lens.IncludeUnknownDevice ?? true);

    private static CountBy CountOf(DashboardLens lens) => lens.CountBy switch
    {
        "subscribers" => CountBy.Subscribers,
        "handsets" => CountBy.Handsets,
        _ => CountBy.Bindings,
    };

    private static string Unit(DashboardLens lens) => lens.CountBy switch
    {
        "subscribers" => "Subscribers (estimated)",
        "handsets" => "Handsets (estimated)",
        _ => "Bindings",
    };

    private static IReadOnlyList<string> Notes(DashboardLens lens) =>
        lens.CountBy is "subscribers" or "handsets" ? [Estimated] : [];

    private static Dictionary<string, object?> Data(params (string Key, object? Value)[] sources) =>
        sources.ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);

    private static object?[] Row(params object?[] cells) => cells;

    private static WidgetTable Table(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<object?>> rows, string scope, string note) =>
        new(headers, rows, scope, note);

    private static WidgetDataResult Done(object data, string? asOf, IReadOnlyList<string> notes, WidgetTable table) =>
        new(Ok, null, data, asOf, notes, table);

    private static WidgetDataResult NotReady(string reason, object? data) => new("notReady", reason, data, null, [], null);

    private static string? Iso(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Reads a validated config: every field is present, with its default filled in.</summary>
    private readonly struct Config(JsonElement config)
    {
        public string Str(string name) =>
            config.ValueKind == JsonValueKind.Object && config.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()!
                : string.Empty;

        public int Int(string name, int fallback) =>
            config.ValueKind == JsonValueKind.Object && config.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var n)
                ? n
                : fallback;

        public bool Bool(string name) =>
            config.ValueKind == JsonValueKind.Object && config.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        public DateOnly? Date(string name) =>
            DateOnly.TryParseExact(Str(name), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

        public IReadOnlyList<string> List(string name) =>
            config.ValueKind == JsonValueKind.Object && config.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? [.. v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]
                : [];
    }
}
