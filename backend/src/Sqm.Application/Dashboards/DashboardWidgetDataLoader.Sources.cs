using Sqm.Application.DataImport;
using Sqm.Application.Devices;
using Sqm.Application.Identity;
using Sqm.Application.Quality;
using Sqm.Application.Risk;
using Sqm.Contracts.Dashboards;
using Sqm.Contracts.Quality;
using Sqm.Contracts.Risk;
using Sqm.Domain.Risk;

namespace Sqm.Application.Dashboards;

/// <summary>The risk, data-quality, operations and tool widgets.</summary>
public sealed partial class DashboardWidgetDataLoader
{
    private const string Observed = "Observed, not proven: a level states how strong the evidence is, never that anyone did anything.";
    private const string NamesNobody = "Counts only: names nobody.";

    // ------------------------------------------------------------------ risk

    private Task<RiskContext> RiskContextAsync(CancellationToken ct) =>
        MemoAsync("risk-context", () => RiskQueries.ContextAsync(_risk, _feed, _feedOptions, _riskOptions(), ct));

    private async Task<RiskStatusResponse> RiskStatusResponseAsync(CancellationToken ct)
    {
        var context = await RiskContextAsync(ct).ConfigureAwait(false);
        return await MemoAsync("risk-status", () => RiskQueries.StatusAsync(_risk, context, ct)).ConfigureAwait(false);
    }

    private async Task<RiskAnswer<RiskOverviewResponse>> RiskOverviewAnswerAsync(CancellationToken ct)
    {
        var context = await RiskContextAsync(ct).ConfigureAwait(false);
        return await MemoAsync("risk-overview", () => RiskQueries.OverviewAsync(_risk, context, ct)).ConfigureAwait(false);
    }

    private async Task<WidgetDataResult> RiskStatusAsync(Config c, CancellationToken ct)
    {
        var status = await RiskStatusResponseAsync(ct).ConfigureAwait(false);
        var rows = c.Bool("showRules")
            ? status.Rules.Select(r => Row(
                RuleTitle(r.Rule), r.Unit, r.WindowDays, r.WindowTo, r.DaysWithData, r.Threshold, r.TacsThreshold, r.Floor, r.Ceiling,
                r.FlaggedDays.Count)).ToList()
            : [];

        return Done(
            Data(("status", status)), status.Run?.AsOf,
            [Observed, .. status.NotReady is { } why ? new[] { why } : [], .. status.Run?.Stale is { } stale ? new[] { stale } : []],
            Table(
                ["Rule", "Unit", "Window (days)", "Window to", "Days with data", "Threshold (more than)", "TAC threshold", "Stored from", "At most", "Flagged days in window"],
                rows, Visible, $"Rules {status.RuleSetVersion}."));
    }

    private async Task<WidgetDataResult> RiskOverviewAsync(Config c, CancellationToken ct)
    {
        var status = await RiskStatusResponseAsync(ct).ConfigureAwait(false);
        var answer = await RiskOverviewAnswerAsync(ct).ConfigureAwait(false);
        if (answer.Value is not { } overview)
        {
            return NotReady(answer.Reason ?? status.NotReady ?? "Risk signals are not available.", Data(("status", status), ("overview", null)));
        }

        var wanted = c.List("rules");
        return Done(
            Data(("status", status), ("overview", overview)), overview.AsOf, [NamesNobody, Observed],
            Table(["Rule", "Listed", "Level", "Held by feed defects", "Data quality (not risk)"],
                [.. overview.Rules.Where(r => wanted.Contains(r.Rule, StringComparer.Ordinal))
                    .Select(r => Row(RuleTitle(r.Rule), r.Listed, r.Level, r.Capped, r.DataQuality))],
                Visible, $"At each rule's configured threshold, rules {overview.RuleSetVersion}."));
    }

    private async Task<WidgetDataResult> RiskDistributionAsync(Config c, CancellationToken ct)
    {
        var status = await RiskStatusResponseAsync(ct).ConfigureAwait(false);
        var context = await RiskContextAsync(ct).ConfigureAwait(false);
        var rule = Enum.Parse<RiskRule>(c.Str("rule"));
        var answer = await RiskQueries.DistributionAsync(_risk, context, rule, ct).ConfigureAwait(false);
        if (answer.Value is not { } distribution)
        {
            return NotReady(answer.Reason ?? status.NotReady ?? "Risk signals are not available.", Data(("status", status), ("distribution", null)));
        }

        var clean = c.Str("values") != "raw";
        var raw = c.Str("values") != "clean";
        var headers = new List<string> { "From", "To (exclusive)" };
        if (clean)
        {
            headers.Add("Clean");
        }

        if (raw)
        {
            headers.Add("Before the screens");
        }

        IReadOnlyList<object?> Bucket(RiskBucketInfo b)
        {
            var row = new List<object?> { b.From, b.To };
            if (clean)
            {
                row.Add(b.Clean);
            }

            if (raw)
            {
                row.Add(b.Raw);
            }

            return row;
        }

        return Done(
            Data(("status", status), ("distribution", distribution)), distribution.AsOf,
            [NamesNobody, $"Values under {distribution.Floor} are not stored."],
            Table(
                headers,
                [.. distribution.Buckets.Select(Bucket)],
                Full, $"{RuleTitle(distribution.Rule)}: every stored entity by {distribution.Unit}; listed above {distribution.Threshold?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "no threshold"}."));
    }

    private async Task<WidgetDataResult> RiskDeviceTypesAsync(Config c, CancellationToken ct)
    {
        var status = await RiskStatusResponseAsync(ct).ConfigureAwait(false);
        var context = await RiskContextAsync(ct).ConfigureAwait(false);
        var rule = Enum.Parse<RiskRule>(c.Str("rule"));
        if (!RiskQueries.HasDeviceTypes(rule))
        {
            return NotReady("A per-number rule has no device type.", Data(("status", status), ("deviceTypes", null)));
        }

        var answer = await RiskQueries.DeviceTypesAsync(_risk, context, rule, ct).ConfigureAwait(false);
        if (answer.Value is not { } types)
        {
            return NotReady(answer.Reason ?? status.NotReady ?? "Risk signals are not available.", Data(("status", status), ("deviceTypes", null)));
        }

        var limit = c.Int("limit", 10);
        return Done(
            Data(("status", status), ("deviceTypes", types)), status.Run?.AsOf, [NamesNobody],
            Table(["Device type", "Listed"], [.. types.Rows.Take(limit).Select(r => Row(r.DeviceType, r.Entities))],
                types.Rows.Count > limit ? TopN : Full, $"Listed at the configured threshold, by device type of the {types.Basis}."));
    }

    private async Task<WidgetDataResult> SimChangeEvidenceAsync(Config c, DashboardLens lens, WidgetLoadContext context, CancellationToken ct)
    {
        var status = await RiskStatusResponseAsync(ct).ConfigureAwait(false);
        var answer = await RiskQueries.DailyAsync(_risk, _feed, _feedOptions, _riskOptions().Floors, ct).ConfigureAwait(false);
        if (answer.Value is not { } daily)
        {
            return NotReady(answer.Reason ?? "Risk signals are not available.", Data(("status", status), ("daily", null)));
        }

        var series = c.List("series");
        var both = series.Contains("changes", StringComparer.Ordinal) && series.Contains("setAside", StringComparer.Ordinal);
        var days = InWindow(daily.Days, lens, context, d => DateOnly.ParseExact(d.Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

        return Done(
            Data(("status", status), ("daily", daily)), daily.Days.Count == 0 ? null : daily.Days[^1].Date, [NamesNobody],
            Table(["Day", "Numbers whose SIM changed", "Counted", "Set aside as feed defects", "Flagged checks"],
                [.. days.Select(d => Row(d.Date, d.Changes, d.Changes - d.SetAside, d.SetAside, string.Join(' ', d.Flagged)))],
                both ? Visible : Full, "Every day in the dashboard's range; counted and set aside sum to the day's total."));
    }

    private async Task<WidgetDataResult> EvidenceQualityAsync(Config c, CancellationToken ct)
    {
        var status = await RiskStatusResponseAsync(ct).ConfigureAwait(false);
        var answer = await RiskOverviewAnswerAsync(ct).ConfigureAwait(false);
        if (answer.Value is not { } overview)
        {
            return NotReady(answer.Reason ?? status.NotReady ?? "Risk signals are not available.", Data(("status", status), ("overview", null)));
        }

        var wanted = c.List("rules");
        var rules = status.Rules.Where(r => wanted.Contains(r.Rule, StringComparer.Ordinal)).ToList();
        var counts = overview.Rules.ToDictionary(r => r.Rule, StringComparer.Ordinal);

        return Done(
            Data(("status", status), ("overview", overview)), overview.AsOf, [NamesNobody, Observed],
            Table(["Rule", "Clean evidence", "Feed defects only", "Share set aside", "Flagged days in window"],
                [.. rules.Select(r =>
                {
                    var n = counts.GetValueOrDefault(r.Rule);
                    var over = n is null ? 0 : n.Listed + n.DataQuality;
                    return Row(
                        RuleTitle(r.Rule), n?.Listed, r.HasDataQualityView ? n?.DataQuality : null,
                        r.HasDataQualityView && over > 0 ? 100.0 * n!.DataQuality / over : null, r.FlaggedDays.Count);
                })],
                Visible, "Feed defects only: over the threshold only through rows matching known feed defects; listed as data quality, not risk."));
    }

    private static string RuleTitle(string rule) => rule switch
    {
        "SharedImeiSims30" => "SIMs added to one IMEI, 30 days",
        "SharedImeiNumbers30" => "Numbers added to one IMEI, 30 days",
        "SharedImeiSimsEver" => "SIMs ever on one IMEI",
        "SharedImeiSimsNotRemoved" => "SIMs not yet removed from one IMEI",
        "HighDeviceCount30" => "IMEIs one SIM was added to, 30 days",
        "RapidDeviceChange7" => "IMEIs one SIM was added to, 7 days",
        "Randomisation20" => "IMEIs and TACs one SIM was added to, 20 days",
        "RepeatedSimChange7" => "Days with a SIM change on one number, 7 days",
        _ => rule,
    };

    // ------------------------------------------------------------------ data quality

    private async Task<WidgetDataResult> EnrichmentAsync(DashboardLens lens, CancellationToken ct)
    {
        var kpi = await KpiSummaryAsync(Filter(lens), ct).ConfigureAwait(false);
        double Share(double n) => kpi.ActiveBindings == 0 ? 0 : 100.0 * n / kpi.ActiveBindings;
        var unregistered = UnregisteredTac(kpi);

        return Done(
            Data(("kpi", kpi)), Iso(kpi.DeliveryDate), ["The four categories sum exactly to the active bindings; only a malformed IMEI is a defect."],
            Table(["Category", "Bindings", "Percent of active bindings"],
            [
                Row("Enriched from GSMA", kpi.TacMatchedBindings, Share(kpi.TacMatchedBindings)),
                Row("Unknown device (000000)", kpi.UnknownDeviceBindings, Share(kpi.UnknownDeviceBindings)),
                Row("Malformed IMEI", kpi.MalformedImeiBindings, Share(kpi.MalformedImeiBindings)),
                Row("Unregistered TAC", unregistered, Share(unregistered)),
                Row("Active bindings", kpi.ActiveBindings, 100.0),
            ], Full, "Every active binding."));
    }

    private async Task<WidgetDataResult> FeedTrendAsync(Config c, DashboardLens lens, WidgetLoadContext context, CancellationToken ct)
    {
        var feed = await FeedQualityAsync(ct).ConfigureAwait(false);
        var checks = c.List("checks");
        var rate = c.Str("measure") != "count";
        var days = InWindow(feed.Days, lens, context, d => d.Date);

        object? Value(FeedQualityDayInfo d, string check) => rate
            ? d.Findings.FirstOrDefault(f => f.Check == check)?.Rate * 100
            : check switch
            {
                "ShiftedImei" => d.ShiftedImeiRows,
                "MultiNumberSim" => d.MultiNumberSimRows,
                "MalformedImei" => d.MalformedImeiRows,
                _ => d.UnknownDeviceRows,
            };

        return Done(
            Data(("feedQuality", feed)), Iso(feed.Days.Count == 0 ? null : feed.Days[^1].Date), [],
            Table(["Day", "Rows in the file", .. checks.Select(x => rate ? $"{x}, percent" : $"{x}, rows"), "Flagged"],
                [.. days.Select(d => (IReadOnlyList<object?>)[Iso(d.Date), d.Rows, .. checks.Select(x => Value(d, x)), d.Flagged])],
                Visible, "Every measured day in the dashboard's range."));
    }

    private async Task<WidgetDataResult> FlaggedDaysAsync(Config c, DashboardLens lens, WidgetLoadContext context, CancellationToken ct)
    {
        var feed = await FeedQualityAsync(ct).ConfigureAwait(false);
        var freshness = await FreshnessListAsync(ct).ConfigureAwait(false);
        var include = c.List("include");
        var through = context.DataThrough ?? (feed.Days.Count == 0 ? (DateOnly?)null : feed.Days.Max(d => d.Date));
        var (from, to) = DashboardLensResolver.ResolveRange(lens.Range, through);
        bool In(DateOnly d) => (from is null || d >= from) && (to is null || d <= to);

        var entries = new List<(DateOnly Date, string Kind, string What, long? Rows)>();
        if (include.Contains("flagged", StringComparer.Ordinal))
        {
            entries.AddRange(feed.Days.Where(d => d.Flagged && In(d.Date)).Select(d => (d.Date, "Out of line",
                string.Join("; ", d.Findings.Where(f => f.Flagged).Select(f => f.Explanation)), (long?)d.Rows)));
        }

        if (include.Contains("missing", StringComparer.Ordinal))
        {
            var missing = freshness.FirstOrDefault(f => f.SourceCode == "SQM")?.MissingBusinessDates ?? [];
            entries.AddRange(missing.Distinct().Where(In).Select(d => (d, "Missing", "Never delivered by the source.", (long?)null)));
        }

        var limit = c.Int("limit", 20);
        var sorted = entries.OrderByDescending(e => e.Date).ToList();
        return Done(
            Data(("feedQuality", feed), ("freshness", freshness)), null, [],
            Table(["Day", "Kind", "What", "Rows"], [.. sorted.Take(limit).Select(e => Row(Iso(e.Date), e.Kind, e.What, e.Rows))],
                sorted.Count > limit ? TopN : Full, sorted.Count > limit ? $"The newest {limit} of {sorted.Count}." : "Every such day in the range."));
    }

    private async Task<WidgetDataResult> SignalsAsync(Config c, CancellationToken ct)
    {
        var signals = await QualityQueries.SignalsAsync(_quality, ct).ConfigureAwait(false);
        if (!signals.Available)
        {
            return NotReady(signals.Reason ?? QualityQueries.SignalsNotPublished, Data(("signals", signals)));
        }

        var groups = c.List("groups");
        var unit = c.Str("unit");
        long Value(QualitySignalInfo s) => unit switch { "numbers" => s.Numbers, "sims" => s.Sims, "imeis" => s.Imeis, _ => s.Bindings };

        return Done(
            Data(("signals", signals)), Iso(signals.AsOf),
            ["A binding can be in more than one category.", "SIMs and IMEIs are estimated (about 0.5%); bindings and numbers are exact."],
            Table(["Group", "Category", unit, "Share of the base, percent"],
                [.. signals.Categories.Where(s => groups.Contains(s.Group, StringComparer.Ordinal))
                    .Select(s => Row(s.Group, s.Label, Value(s), s.Share * 100))],
                Visible, "The categories the card shows."));
    }

    // ------------------------------------------------------------------ operations

    private async Task<WidgetDataResult> FreshnessAsync(WidgetLoadContext context, CancellationToken ct)
    {
        var freshness = await FreshnessListAsync(ct).ConfigureAwait(false);
        var health = await _imports.GetWorkerHealthAsync(ct).ConfigureAwait(false);
        var tac = await TacVersionsAsync(ct).ConfigureAwait(false);
        // Deliberately the unfiltered KPI: which delivery the figures come from does not depend on a filter.
        var kpi = context.User.Can(Permissions.DashboardView)
            ? await KpiSummaryAsync(new Contracts.Dashboard.DashboardFilter(), ct).ConfigureAwait(false)
            : null;

        return Done(
            Data(("freshness", freshness), ("workerHealth", health), ("tacVersions", tac), ("kpi", kpi)), null, [],
            Table(["Source", "Data through", "Days behind", "Days not delivered", "Failed in 7 days", "Figures pending since"],
                [.. freshness.Select(f => Row(f.SourceCode, Iso(f.LatestBusinessDate), f.DaysBehind, f.MissingBusinessDates.Distinct().Count(),
                    f.FailedLast7Days, f.DashboardPendingSince))],
                Full, "Every source."));
    }

    private async Task<WidgetDataResult> RecentImportsAsync(Config c, CancellationToken ct)
    {
        var source = c.Str("source");
        var statuses = c.List("statuses").Select(s => Enum.Parse<ImportJobStatus>(s)).ToList();
        var filter = new ImportHistoryFilter(source == "any" ? null : source, statuses.Count == 0 ? null : statuses);
        var limit = c.Int("limit", 10);
        var items = await _imports.ListJobsAsync(filter, limit, 0, ct).ConfigureAwait(false);
        var total = await _imports.CountJobsAsync(filter, ct).ConfigureAwait(false);
        var page = new ImportPage(items, total, 1, limit);

        return Done(
            Data(("page", page)), null, [],
            Table(["Job", "File", "Source", "Day", "Status", "Rows in", "Imported", "Quarantined", "Finished"],
                [.. items.Select(j => Row(j.JobId, j.OriginalFileName, j.SourceCode, Iso(j.BusinessDate), j.Status.ToString(),
                    j.RowsInput, j.RowsInserted, j.RowsInvalid, j.FinishedAt))],
                total > items.Count ? TopN : Full, $"The newest {items.Count} of {total}."));
    }

    private async Task<WidgetDataResult> WorkerHealthAsync(WidgetLoadContext context, CancellationToken ct)
    {
        _ = context;
        var health = await _imports.GetWorkerHealthAsync(ct).ConfigureAwait(false);
        var freshness = await FreshnessListAsync(ct).ConfigureAwait(false);

        return Done(
            Data(("workerHealth", health), ("freshness", freshness)), null, [],
            Table(["Measure", "Value"],
            [
                Row("Workers alive", health.ActiveWorkers), Row("Running", health.Running), Row("Queued", health.Queued),
                Row("Retrying", health.Retrying), Row("Failed in 24 hours", health.FailedLast24Hours),
                Row("Oldest queued at", health.OldestQueuedAt), Row("Stale leases", health.StaleLeases), Row("Stalled", health.Stalled),
            ], Full, "The pipeline now."));
    }

    private async Task<WidgetDataResult> BlockagesAsync(Config c, WidgetLoadContext context, CancellationToken ct)
    {
        _ = context;
        var blockages = await _imports.GetBlockagesAsync(ct).ConfigureAwait(false);
        var freshness = await FreshnessListAsync(ct).ConfigureAwait(false);
        var limit = c.Int("limit", 10);

        return Done(
            Data(("blockages", blockages), ("freshness", freshness)), null, [],
            Table(["Source", "Day", "Job", "Status", "Later days waiting"],
                [.. blockages.Take(limit).Select(b => Row(b.SourceCode, Iso(b.BusinessDate), b.JobId, b.Status, b.Waiting))],
                blockages.Count > limit ? TopN : Full, blockages.Count > limit ? $"The first {limit} of {blockages.Count}." : "Every blocked day."));
    }

    private async Task<WidgetDataResult> TacVersionAsync(CancellationToken ct)
    {
        var versions = await TacVersionsAsync(ct).ConfigureAwait(false);
        return Done(
            Data(("tacVersions", versions)), null, ["Activating a version changes the manufacturer and model behind every figure."],
            Table(["Version", "Status", "Dataset date", "TACs", "Activated at"],
                [.. versions.Select(v => Row(v.VersionLabel, v.Status.ToString(), Iso(v.DatasetDate), v.RowCount, v.ActivatedAt))],
                Full, "Every version."));
    }

    // ------------------------------------------------------------------ tools

    private async Task<WidgetDataResult> ModelNetworkAgeAsync(Config c, CancellationToken ct)
    {
        var tac = c.Str("tac");
        var state = await _arrivals.GetStateAsync(ct).ConfigureAwait(false);
        if (ModelArrivalQueries.NotReady(state) is { } reason)
        {
            return NotReady(reason, Data(("age", null), ("device", null)));
        }

        if (await _arrivals.FirstSeenAsync(tac, ct).ConfigureAwait(false) is not { } seen)
        {
            return NotReady("This TAC has never been seen in this data.", Data(("age", null), ("device", null)));
        }

        var age = ModelArrivalQueries.NetworkAgeOf(tac, seen, state.DataThrough);
        var device = c.Bool("showDetail") ? await DeviceDetails.LoadAsync(_analytics, _images, tac, ct).ConfigureAwait(false) : null;

        return Done(
            Data(("age", age), ("device", device)), age.DataThrough,
            ["Days since the feed first named one of the model's handsets - not the age of any handset."],
            Table(["TAC", "Model", "First seen", "Network age (days)", "At least (in the initial dump)", "Days seen"],
                [Row(tac, device?.MarketingName ?? device?.Model, age.FirstSeen, age.NetworkAgeDays, age.AtLeast, age.DaysSeen)],
                Full, "One model."));
    }
}
