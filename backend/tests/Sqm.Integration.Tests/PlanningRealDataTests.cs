using System.Diagnostics;
using Microsoft.Extensions.Options;
using Sqm.Application.Planning;
using Sqm.Infrastructure.ClickHouse;

namespace Sqm.Integration.Tests;

/// <summary>
/// The planning reader on the real published run: the classes add up to the denominator they are
/// shares of, the finished summary agrees with the per-number rows it was written from, no number is
/// in two classes, and every view and breakdown reads within the budget the server enforces.
/// </summary>
/// <remarks>Skips without the stack or without a published planning run (Sqm.Ingestion --refresh-planning).</remarks>
[Collection("clickhouse-scratch")]
public sealed class PlanningRealDataTests
{
    private const string Connection = "Host=localhost;Port=18123;Database=sqm;Username=sqm_app;Password=sqm_dev";

    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromMinutes(3),
        };
    }

    private static ClickHousePlanningReader Reader() =>
        new(Options.Create(new ClickHouseOptions { ConnectionString = Connection }), new Factory());

    private static void Write(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    private static async Task<string> QueryAsync(string sql)
    {
        using var http = new Factory().CreateClient("test");
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("sqm_app:sqm_dev")));
        using var response = await http.PostAsync(
            "http://localhost:18123/", new StringContent(sql, System.Text.Encoding.UTF8, "text/plain"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return response.IsSuccessStatusCode ? body.Trim() : throw new InvalidOperationException($"ClickHouse refused: {body}");
    }

    private static async Task<bool> PublishedAsync()
    {
        try
        {
            var status = await Reader().GetStatusAsync(TestContext.Current.CancellationToken);
            return status.Deployed && status.Runs.Count > 0 && status.Runs[0].HasNumbers;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    [Fact]
    public async Task The_sunset_classes_add_up_and_the_summary_agrees_with_the_rows()
    {
        if (!await PublishedAsync())
        {
            Assert.Skip("no ClickHouse with a published planning run (analytics migration 030, Sqm.Ingestion --refresh-planning)");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        var sunset = (await Reader().GetSunsetAsync(null, PlanningFilter.None, ct))!;
        Write($"sunset view: {watch.ElapsedMilliseconds} ms, {sunset.TotalNumbers:N0} numbers");

        Assert.Equal(sunset.TotalNumbers, sunset.Classes.Sum(c => c.Numbers));
        Assert.Equal(sunset.Classes.Single(c => c.Class == "not-assessable").Numbers, sunset.NotAssessable.Sum(r => r.Numbers));

        // An independent count of the same population: the dashboard's KPI for the same delivery,
        // a HyperLogLog estimate (~0.5%) from a different pass over current state.
        var kpi = long.Parse(await QueryAsync(
            $"SELECT distinct_subscribers FROM sqm.agg_kpi_daily WHERE seq = {sunset.Run.AsOfSequence}"), System.Globalization.CultureInfo.InvariantCulture);
        Write($"KPI subscribers for delivery {sunset.Run.AsOfSequence}: {kpi:N0}; planning numbers: {sunset.TotalNumbers:N0}");
        Assert.InRange(sunset.TotalNumbers, (long)(kpi * 0.99), (long)(kpi * 1.01));

        // No number in two classes, and the summary is the rows'.
        var rows = await QueryAsync($"""
            SELECT concat(toString(count()), '|', toString(uniqExact(msisdn)))
            FROM sqm.planning_number WHERE run_id = {sunset.Run.RunId}
            """);
        Assert.Equal($"{sunset.TotalNumbers}|{sunset.TotalNumbers}", rows);
        Assert.InRange(sunset.ExposedGsmOnly, 0, sunset.Classes.Single(c => c.Class == "exposed").Numbers);

        var esim = (await Reader().GetEsimAsync(null, PlanningFilter.None, ct))!;
        Assert.Equal(sunset.TotalNumbers, esim.TotalNumbers);

        foreach (var c in sunset.Classes)
        {
            Write($"  {c.Label}: {c.Numbers:N0} numbers ({c.SharePercent}%), {c.Bindings:N0} bindings, ~{c.Handsets:N0} handsets");
        }

        foreach (var r in sunset.NotAssessable)
        {
            Write($"    {r.Label}: {r.Numbers:N0}");
        }
    }

    [Fact]
    public async Task The_lifecycle_reads_within_budget_and_its_parts_agree()
    {
        if (!await PublishedAsync())
        {
            Assert.Skip("no ClickHouse with a published planning run");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        var view = (await Reader().GetLifecycleAsync(null, PlanningFilter.None, ct))!;
        Write($"lifecycle view: {watch.ElapsedMilliseconds} ms; {view.Changes:N0} changes {view.FirstMonth}..{view.LastMonth}; "
            + string.Join("; ", view.Kinds.Select(k => $"{k.Label} {k.Changes:N0}")) + $"; not assessed {view.NotAssessedNumbers:N0} numbers");
        foreach (var c in view.Cohorts)
        {
            Write($"  {c.Cohort}: {c.Spells:N0} spells, {c.Changed:N0} changed; by 30/90/180 days {c.ChangedBy30}/{c.ChangedBy90}/{c.ChangedBy180}%, "
                + $"upgraded by 90/180 {c.UpgradedBy90}/{c.UpgradedBy180}%, median {c.MedianDays}");
        }

        Assert.Equal(view.Changes, view.Kinds.Sum(k => k.Changes));
        Assert.All(view.Cohorts, c => Assert.Equal(c.Spells, c.Changed + c.Censored));
        Assert.All(view.Cohorts, c => Assert.True((c.ChangedBy30 ?? 0) <= (c.ChangedBy90 ?? 100) && (c.UpgradedBy90 ?? 0) <= (c.ChangedBy90 ?? 100)));

        // The same handsets two ways, and no more than the dashboard's active 14-digit IMEIs, which
        // also count the shifted ones the lifecycle sets aside.
        var held = view.DeviceAge.Sum(a => a.Handsets);
        Assert.Equal(held, view.ModelAge.Sum(a => a.Handsets));
        var devices = long.Parse(await QueryAsync(
            $"SELECT distinct_devices FROM sqm.agg_kpi_daily WHERE seq = {view.Run.AsOfSequence}"), System.Globalization.CultureInfo.InvariantCulture);
        Write($"handsets held now with a history: {held:N0}; the KPI's active 14-digit IMEIs (shifted included): {devices:N0}");
        Assert.InRange(held, 1, (long)(devices * 1.01));

        watch.Restart();
        var samsung = (await Reader().GetLifecycleAsync(null, new PlanningFilter("Samsung", null, null), ct))!;
        Write($"lifecycle narrowed to Samsung: {watch.ElapsedMilliseconds} ms, {samsung.Changes:N0} changes from or to it");
        Assert.InRange(samsung.Changes, 1, view.Changes);
        Assert.Empty(samsung.Cohorts);
    }

    [Fact]
    public async Task Every_breakdown_and_the_iot_view_read_within_budget()
    {
        if (!await PublishedAsync())
        {
            Assert.Skip("no ClickHouse with a published planning run");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        foreach (var (view, planningClass) in new[] { ("sunset", "exposed"), ("sunset", "not-assessable"), ("esim", "capable"), ("iot", "iot"), ("bands", "lte-3"), ("bands", "arch-nsa"), ("ntn", "ntn") })
        {
            foreach (var by in new[] { "vendor", "model", "deviceType", "tac" })
            {
                var watch = Stopwatch.StartNew();
                var breakdown = await Reader().GetBreakdownAsync(null, view, planningClass, by, PlanningFilter.None, 50, new OperatorBandsOptions(), ct);
                Write($"{view}/{planningClass} by {by}: {breakdown!.Rows.Count} of {breakdown.Total:N0} rows, {watch.ElapsedMilliseconds} ms");
                Assert.True(breakdown.Rows.SequenceEqual(breakdown.Rows.OrderByDescending(r => r.Numbers).ThenBy(r => r.Key, StringComparer.Ordinal)));
            }
        }

        var timer = Stopwatch.StartNew();
        var iot = (await Reader().GetIotAsync(null, PlanningFilter.None, ct))!;
        Write($"iot view: {timer.ElapsedMilliseconds} ms, ~{iot.Devices.Handsets:N0} devices, {iot.SimRotation.Count} rotation buckets");
        Assert.True(iot.Devices.Handsets > 0);

        // The SIM rotation counts the same fleet as the headline - the devices active now - exactly,
        // where the headline is an estimate that also holds the few with no history row.
        var rotated = iot.SimRotation.Sum(r => r.Numbers);
        Write($"iot devices: ~{iot.Devices.Handsets:N0} active (estimated); {rotated:N0} in the SIM rotation");
        Assert.InRange(rotated, (long)(iot.Devices.Handsets * 0.9), (long)(iot.Devices.Handsets * 1.02));

        // The per-model views: every binding of the delivery is in one NTN class or not classified, and
        // the band view reads within budget with no operator list configured.
        timer.Restart();
        var ntn = (await Reader().GetNtnAsync(null, PlanningFilter.None, ct))!;
        var bindings = long.Parse(await QueryAsync(
            $"SELECT sum(bindings) FROM sqm.agg_tac_population WHERE seq = {ntn.Run.AsOfSequence}"), System.Globalization.CultureInfo.InvariantCulture);
        Write($"ntn view: {timer.ElapsedMilliseconds} ms; " + string.Join("; ", ntn.Classes.Select(c => $"{c.Label} ~{c.Numbers:N0}")));
        Assert.Equal(bindings, ntn.Classes.Sum(c => c.Bindings) + ntn.NotClassified.Bindings);

        timer.Restart();
        var bands = (await Reader().GetBandsAsync(null, PlanningFilter.None, new OperatorBandsOptions(), ct))!;
        Write($"bands view: {timer.ElapsedMilliseconds} ms; {bands.LteBands.Count} LTE bands, {bands.NrBands.Count} NR bands; "
            + string.Join("; ", bands.Architecture.Select(c => $"{c.Label} ~{c.Numbers:N0}")) + $"; ambiguous ~{bands.Ambiguous.Numbers:N0}");
        Assert.Empty(bands.LteMatch);
        Assert.Contains(bands.NoData, n => n.Topic == "The operator's bands");

        var filtered = (await Reader().GetSunsetAsync(null, new PlanningFilter("Samsung", null, null), ct))!;
        Write($"sunset filtered to Samsung: {filtered.TotalNumbers:N0} numbers holding a Samsung");
        Assert.True(filtered.Filtered);
        Assert.InRange(filtered.TotalNumbers, 1, (await Reader().GetSunsetAsync(null, PlanningFilter.None, ct))!.TotalNumbers);
    }

    [Fact]
    public async Task A_page_of_numbers_reads_in_order_within_budget()
    {
        if (!await PublishedAsync())
        {
            Assert.Skip("no ClickHouse with a published planning run");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        var deep = await Reader().ListNumbersAsync(null, "sunset", "exposed", null, PlanningFilter.None, 199, 50, ct);
        Write($"exposed, page 199: {watch.ElapsedMilliseconds} ms, {deep!.Value.Total:N0} in the class");

        Assert.Equal(50, deep.Value.Rows.Count);
        Assert.True(deep.Value.Rows.Select(r => r.Msisdn).SequenceEqual(deep.Value.Rows.Select(r => r.Msisdn).Order()));
    }
}
