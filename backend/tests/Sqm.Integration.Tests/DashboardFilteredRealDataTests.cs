using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Application.Abstractions;
using Sqm.Contracts.Dashboard;
using Sqm.Infrastructure.ClickHouse;

namespace Sqm.Integration.Tests;

/// <summary>
/// A filtered dashboard view answers from the population marts: one filter from its finished row in
/// well under a second, a combination by merging the per-model states, with the figures the
/// per-model rows add up to.
/// </summary>
/// <remarks>
/// <para>
/// Against the real analytics store, through the store the API uses, as the API's user. On
/// 2026-10-19 the same KPI read binding_current FINAL - 817 million row versions for 120 million
/// bindings - and took 100 s alone, 183 s on the dashboard, failing at the API's client timeout.
/// </para>
/// <para>
/// Skips, saying so, without the database or before the marts hold the served delivery (a refresh
/// that predates migrations 027 and 028). In the ClickHouse collection, so it never runs beside the
/// other real-data tests.
/// </para>
/// </remarks>
[Collection("clickhouse-scratch")]
public sealed class DashboardFilteredRealDataTests : IAsyncLifetime
{
    private const string Endpoint = "http://localhost:18123/";

    private const string Served = "(SELECT max(seq) FROM sqm.mart_ready)";

    private readonly HttpClient _reader = new() { Timeout = TimeSpan.FromMinutes(2) };
    private string? _unavailable;

    public async ValueTask InitializeAsync()
    {
        _reader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("sqm_app:sqm_dev")));

        try
        {
            var models = await ScalarAsync($"SELECT count() FROM sqm.agg_tac_population WHERE seq = {Served}");
            var finished = await ScalarAsync($"SELECT count() FROM sqm.agg_filter_population WHERE seq = {Served}");
            if (models == 0 || finished == 0)
            {
                _unavailable = "the population marts do not hold the served delivery yet (run the mart refresh)";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _unavailable = $"no analytics store at {Endpoint}: {ex.Message}";
        }
    }

    public ValueTask DisposeAsync()
    {
        _reader.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        });
    }

    private static ClickHouseAnalyticsStore Store() => new(
        Options.Create(new ClickHouseOptions
        {
            ConnectionString = "Host=localhost;Port=18123;Database=sqm;Username=sqm_app;Password=sqm_dev",
        }),
        new Factory(),
        NullLogger<ClickHouseAnalyticsStore>.Instance);

    private async Task<long> ScalarAsync(string sql)
    {
        using var response = await _reader.PostAsync(
            Endpoint, new StringContent(sql, Encoding.UTF8, "text/plain"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return response.IsSuccessStatusCode
            ? long.Parse(body.Trim(), CultureInfo.InvariantCulture)
            : throw new InvalidOperationException($"ClickHouse refused: {body}");
    }

    /// <summary>Bindings of the served delivery's models that a device type names, from agg_device_model.</summary>
    private Task<long> ModelBindingsAsync(string deviceType) => ScalarAsync($"""
        SELECT sum(m.bindings)
        FROM sqm.agg_device_model AS m
        JOIN sqm.tac AS t ON t.tac = m.tac
        WHERE m.seq = {Served} AND t.deviceType = '{deviceType}'
        """);

    /// <summary>One aggregate over the served delivery's per-model rows that match a condition.</summary>
    private Task<long> MergedAsync(string aggregate, string condition) => ScalarAsync($"""
        SELECT {aggregate}
        FROM sqm.agg_tac_population AS b
        LEFT JOIN sqm.tac AS t ON t.tac = b.tac
        LEFT JOIN (SELECT * FROM sqm.tac_vendor_map FINAL) AS v ON v.raw_manufacturer = t.manufacturer
        WHERE b.seq = {Served} AND {condition}
        """);

    [Fact]
    public async Task One_filter_answers_from_its_finished_row_not_a_merge()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
        }

        var store = Store();
        var filter = new DashboardFilter(DeviceType: "Smartphone", IncludeUnknownDevice: true);

        // Timed warm: the first call also opens the connection and checks the marts are ready.
        await store.GetKpiSummaryAsync(filter, TestContext.Current.CancellationToken);
        var clock = Stopwatch.StartNew();
        var kpi = await store.GetKpiSummaryAsync(filter, TestContext.Current.CancellationToken);
        clock.Stop();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"one filter: {clock.Elapsed.TotalMilliseconds:N0} ms, {kpi.ActiveBindings:N0} bindings, "
            + $"{kpi.DistinctSubscribers:N0} subscribers, {kpi.DistinctSims:N0} SIMs, {kpi.DistinctDevices:N0} handsets");

        // A finished row is 65-185 ms (2026-10-19); merging this filter's 35,069 models' states, 3.4 s.
        // The bound tells the two apart with room for a busy machine - the first full run of this
        // suite, beside a frontend build, went past a 500 ms bound once. The budget is in the output.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.Elapsed.TotalMilliseconds:N0} ms");
        Assert.Equal(await ModelBindingsAsync("Smartphone"), kpi.ActiveBindings);
        Assert.Equal(kpi.ActiveBindings, kpi.TacMatchedBindings);
        Assert.Equal(0, kpi.UnknownDeviceBindings);

        // The finished row is the models' states merged: the figures a merge gives now.
        const string Smartphones = "t.deviceType = 'Smartphone'";
        Assert.Equal(await MergedAsync("uniqMerge(b.subscribers)", Smartphones), kpi.DistinctSubscribers);
        Assert.Equal(await MergedAsync("uniqMerge(b.sims)", Smartphones), kpi.DistinctSims);
        Assert.Equal(await MergedAsync("uniqMerge(b.handsets)", Smartphones), kpi.DistinctDevices);
    }

    [Fact]
    public async Task Two_filters_together_merge_the_matching_models_in_seconds()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
        }

        var filter = new DashboardFilter(VendorCanonical: "Samsung", DeviceType: "Smartphone", IncludeUnknownDevice: true);
        var clock = Stopwatch.StartNew();
        var kpi = await Store().GetKpiSummaryAsync(filter, TestContext.Current.CancellationToken);
        clock.Stop();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"two filters: {clock.Elapsed.TotalSeconds:N1} s, {kpi.ActiveBindings:N0} bindings, "
            + $"{kpi.DistinctSubscribers:N0} subscribers");

        const string Both =
            "t.deviceType = 'Smartphone' AND coalesce(nullIf(v.vendor_canonical, ''), t.manufacturer) = 'Samsung'";
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed.TotalSeconds:N1} s");
        Assert.Equal(await MergedAsync("sum(b.bindings)", Both), kpi.ActiveBindings);
        Assert.Equal(await MergedAsync("uniqMerge(b.subscribers)", Both), kpi.DistinctSubscribers);
        // A distinct count merged across models cannot exceed the rows it counts.
        Assert.InRange(kpi.DistinctSubscribers, 1, kpi.ActiveBindings);
    }

    [Fact]
    public async Task A_filtered_breakdown_answers_in_seconds_and_its_shares_are_of_the_filtered_population()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
        }

        var filter = new DashboardFilter(DeviceType: "Smartphone", IncludeUnknownDevice: true);
        var clock = Stopwatch.StartNew();
        var vendors = await Store().GetTopDimensionAsync(
            AnalyticsDimension.VendorCanonical, filter, 500, CountBy.Bindings, TestContext.Current.CancellationToken);
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed.TotalSeconds:N1} s");
        Assert.NotEmpty(vendors);

        // The 500 largest of the filtered models' vendors - not all of them: smartphones came from 752
        // vendors on 2026-10-19, and the 252 smallest held 306 of 93,397,209 bindings.
        var top500 = await ScalarAsync($"""
            SELECT sum(n) FROM (
                SELECT coalesce(nullIf(v.vendor_canonical, ''), t.manufacturer) AS k, sum(m.bindings) AS n
                FROM sqm.agg_device_model AS m
                LEFT JOIN sqm.tac AS t ON t.tac = m.tac
                LEFT JOIN (SELECT * FROM sqm.tac_vendor_map FINAL) AS v ON v.raw_manufacturer = t.manufacturer
                WHERE m.seq = {Served} AND t.deviceType = 'Smartphone'
                GROUP BY k ORDER BY n DESC LIMIT 500)
            """);
        Assert.Equal(top500, vendors.Sum(v => v.Count));

        // A share is of the whole filtered population, the vendors not listed included.
        var population = await ModelBindingsAsync("Smartphone");
        Assert.Equal(Math.Round(100.0 * vendors[0].Count / population, 3), vendors[0].Percent, 3);
    }

    [Fact]
    public async Task A_filtered_breakdown_by_subscribers_merges_each_vendors_models()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
        }

        // The breakdown that reads states: each vendor's subscribers, a distinct count per group.
        var filter = new DashboardFilter(DeviceType: "Smartphone", IncludeUnknownDevice: true);
        var clock = Stopwatch.StartNew();
        var vendors = await Store().GetTopDimensionAsync(
            AnalyticsDimension.VendorCanonical, filter, 10, CountBy.Subscribers, TestContext.Current.CancellationToken);
        clock.Stop();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"breakdown by subscribers: {clock.Elapsed.TotalSeconds:N1} s, top {vendors[0].Key} {vendors[0].Count:N0}");

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed.TotalSeconds:N1} s");
        var key = vendors[0].Key.Replace("'", "''", StringComparison.Ordinal);
        Assert.Equal(
            await MergedAsync("uniqMerge(b.subscribers)",
                $"t.deviceType = 'Smartphone' AND coalesce(nullIf(v.vendor_canonical, ''), t.manufacturer) = '{key}'"),
            vendors[0].Count);
    }

    [Fact]
    public async Task The_device_model_mart_is_the_per_model_states_finalised()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
        }

        // Section 1b now finalises agg_device_model from these states; for the served delivery the two
        // must agree model for model, or the device pages would move.
        var differing = await ScalarAsync($"""
            SELECT count()
            FROM sqm.agg_tac_population AS p
            JOIN sqm.agg_device_model AS m ON m.seq = p.seq AND m.tac = p.tac
            WHERE p.seq = {Served}
              AND (m.bindings != p.bindings
                   OR m.handsets != finalizeAggregation(p.handsets)
                   OR m.sims != finalizeAggregation(p.sims)
                   OR m.subscribers != finalizeAggregation(p.subscribers))
            """);

        Assert.Equal(0, differing);
    }

    [Fact]
    public async Task Every_finished_device_type_row_is_its_models_merged()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
        }

        // Every value of one dimension, not one sample: a key that differed from the filter's own
        // comparison would show here as a missing or a doubled row.
        var differing = await ScalarAsync($"""
            SELECT count()
            FROM (
                SELECT value, bindings, subscribers, sims, handsets
                FROM sqm.agg_filter_population WHERE seq = {Served} AND dimension = 'deviceType') AS f
            FULL JOIN (
                SELECT t.deviceType AS value, sum(b.bindings) AS bindings, uniqMerge(b.subscribers) AS subscribers,
                       uniqMerge(b.sims) AS sims, uniqMerge(b.handsets) AS handsets
                FROM sqm.agg_tac_population AS b LEFT JOIN sqm.tac AS t ON t.tac = b.tac
                WHERE b.seq = {Served} AND t.deviceType != ''
                GROUP BY value) AS r ON r.value = f.value
            WHERE f.bindings != r.bindings OR f.subscribers != r.subscribers
               OR f.sims != r.sims OR f.handsets != r.handsets
            """);

        Assert.Equal(0, differing);
    }
}
