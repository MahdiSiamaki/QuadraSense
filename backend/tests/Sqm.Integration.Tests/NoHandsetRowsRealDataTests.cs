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
/// Rows with no handset stay stored and are left out of every count (ADR-020): each read path, on the
/// real data, through the store the API uses, against what the marts hold for both populations.
/// </summary>
/// <remarks>
/// The rule is written in each read path rather than in one place, so each has its own test here.
/// Skips, saying so, without the database or before the marts hold the served delivery. In the
/// ClickHouse collection, so it never runs beside the other real-data tests.
/// </remarks>
[Collection("clickhouse-scratch")]
public sealed class NoHandsetRowsRealDataTests : IAsyncLifetime
{
    private const string Endpoint = "http://localhost:18123/";
    private const string Served = "(SELECT max(seq) FROM sqm.mart_ready)";
    private const string NoHandset = "(unknown device)";

    private static readonly DashboardFilter Counted = new();
    private static readonly DashboardFilter Everything = new(IncludeUnknownDevice: true);

    private readonly HttpClient _reader = new() { Timeout = TimeSpan.FromMinutes(2) };
    private string? _unavailable;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _reader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("sqm_app:sqm_dev")));

        try
        {
            if (await ScalarAsync($"SELECT count() FROM sqm.agg_filter_population WHERE seq = {Served}") == 0)
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
        using var response = await _reader.PostAsync(Endpoint, new StringContent(sql, Encoding.UTF8, "text/plain"), Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        return response.IsSuccessStatusCode
            ? long.Parse(body.Trim(), CultureInfo.InvariantCulture)
            : throw new InvalidOperationException($"ClickHouse refused: {body}");
    }

    private void SkipWithoutTheStore()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
        }
    }

    private static DeviceListCriteria Catalogue(IReadOnlyList<string>? tacs, DeviceListSort sort) =>
        new(null, null, null, null, null, tacs, 0, sort, true, 0, 50);

    [Fact]
    public async Task The_headline_leaves_them_out_and_says_how_many()
    {
        SkipWithoutTheStore();
        var store = Store();

        var counted = await store.GetKpiSummaryAsync(Counted, Ct);
        var everything = await store.GetKpiSummaryAsync(Everything, Ct);

        Assert.True(everything.UnknownDeviceBindings > 0, "the store holds no 000000 rows to leave out");
        Assert.True(everything.InvalidImeiBindings > 0, "the store holds no IMEI no handset could carry");
        Assert.Equal(
            everything.ActiveBindings - everything.UnknownDeviceBindings - everything.MalformedImeiBindings - everything.InvalidImeiBindings,
            counted.ActiveBindings);

        // A card for them reads the true figure, counted or not.
        Assert.Equal(everything.UnknownDeviceBindings, counted.UnknownDeviceBindings);
        Assert.Equal(everything.MalformedImeiBindings, counted.MalformedImeiBindings);
        Assert.Equal(everything.InvalidImeiBindings, counted.InvalidImeiBindings);
        Assert.Equal(everything.ActiveBindings, counted.ActiveBindings + counted.SetAsideBindings);
        Assert.Equal(0, everything.SetAsideBindings);

        // They were never handsets, and their numbers and SIMs mostly have a real row.
        Assert.Equal(everything.DistinctDevices, counted.DistinctDevices);
        Assert.InRange(counted.DistinctSubscribers, everything.DistinctSubscribers * 0.98, everything.DistinctSubscribers);
        Assert.InRange(counted.DistinctSims, everything.DistinctSims * 0.98, everything.DistinctSims);

        // Coverage is of what is counted, and matched rows are the same rows either way.
        Assert.Equal(everything.TacMatchedBindings, counted.TacMatchedBindings);
        Assert.True(counted.TacCoveragePercent > everything.TacCoveragePercent);
    }

    [Fact]
    public async Task A_filter_on_a_model_has_nothing_to_set_aside()
    {
        SkipWithoutTheStore();

        var kpi = await Store().GetKpiSummaryAsync(new DashboardFilter(DeviceType: "Smartphone"), Ct);

        Assert.True(kpi.ActiveBindings > 0);
        Assert.Equal(0, kpi.SetAsideBindings);
        Assert.Equal(0, kpi.UnknownDeviceBindings);
    }

    [Theory]
    [InlineData(CountBy.Bindings)]
    [InlineData(CountBy.Subscribers)]
    public async Task A_top_list_leaves_them_out_and_keeps_a_tac_gsma_does_not_know(CountBy measure)
    {
        SkipWithoutTheStore();
        var store = Store();

        var counted = await store.GetTopDimensionAsync(AnalyticsDimension.VendorCanonical, Counted, 2000, measure, Ct);
        var everything = await store.GetTopDimensionAsync(AnalyticsDimension.VendorCanonical, Everything, 2000, measure, Ct);

        Assert.DoesNotContain(counted, r => r.Key == NoHandset);
        Assert.Contains(everything, r => r.Key == NoHandset);
        Assert.Contains(counted, r => r.Key == "(unknown TAC)");
    }

    [Fact]
    public async Task A_share_in_a_top_list_is_of_the_population_counted()
    {
        SkipWithoutTheStore();
        var store = Store();

        var kpi = await store.GetKpiSummaryAsync(Counted, Ct);
        var first = (await store.GetTopDimensionAsync(AnalyticsDimension.VendorCanonical, Counted, 1, CountBy.Bindings, Ct))[0];

        Assert.Equal(100.0 * first.Count / kpi.ActiveBindings, first.Percent, 2);
    }

    [Fact]
    public async Task The_class_mix_partitions_what_is_counted()
    {
        SkipWithoutTheStore();
        var store = Store();

        var mix = await store.GetDeviceClassMixAsync(CountBy.Bindings, Ct);
        var kpi = await store.GetKpiSummaryAsync(Counted, Ct);

        Assert.DoesNotContain(mix, s => s.Key == "Unknown device");
        Assert.Equal(kpi.ActiveBindings, mix.Sum(s => s.Count));
        Assert.Equal(100.0, mix.Sum(s => s.Percent), 1);
    }

    [Fact]
    public async Task The_daily_series_ends_on_the_card_above_it()
    {
        SkipWithoutTheStore();
        var store = Store();

        var kpi = await store.GetKpiSummaryAsync(Counted, Ct);
        var days = await store.GetDailyChangesAsync(Ct);

        Assert.Equal(kpi.ActiveBindings, days.Last(d => d.Date <= kpi.DeliveryDate).Cumulative);
    }

    [Fact]
    public async Task A_day_leaves_out_the_events_that_carried_no_handset()
    {
        SkipWithoutTheStore();

        // The day with the most: every day has some, and January had them in millions.
        var day = await ScalarAsync("""
            SELECT toInt32(toYYYYMMDD(data_date)) FROM sqm.agg_change_daily
            WHERE tac = '' GROUP BY data_date ORDER BY sum(n) DESC LIMIT 1
            """);
        var date = DateOnly.ParseExact(day.ToString(CultureInfo.InvariantCulture), "yyyyMMdd", CultureInfo.InvariantCulture);
        var iso = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var added = await ScalarAsync($"SELECT sum(n) FROM sqm.agg_change_daily WHERE data_date = '{iso}' AND label = 'add' AND tac != ''");
        var removed = await ScalarAsync($"SELECT sum(n) FROM sqm.agg_change_daily WHERE data_date = '{iso}' AND label = 'remove' AND tac != ''");

        var row = (await Store().GetDailyChangesAsync(Ct)).Single(d => d.Date == date);

        Assert.Equal(added, row.Added);
        Assert.Equal(removed, row.Removed);
        Assert.True(row.UnknownDeviceRows > 0, "the data-quality count of the day stays");
    }

    [Fact]
    public async Task The_catalogue_lists_them_only_for_someone_who_searched_for_them()
    {
        SkipWithoutTheStore();
        var store = Store();

        var listed = await store.SearchDevicesAsync(Catalogue(null, DeviceListSort.Bindings), Ct);
        var searched = await store.SearchDevicesAsync(Catalogue([string.Empty], DeviceListSort.Bindings), Ct);

        Assert.DoesNotContain(listed.Rows, d => d.Tac.Length == 0);
        Assert.Equal(string.Empty, Assert.Single(searched.Rows).Tac);
    }

    [Fact]
    public async Task An_imei_no_handset_could_carry_names_no_model()
    {
        SkipWithoutTheStore();

        var zeros = await Store().SearchDevicesAsync(Catalogue(["00000000"], DeviceListSort.Bindings), Ct);

        Assert.Empty(zeros.Rows);
    }

    [Fact]
    public async Task A_models_share_of_the_network_is_of_what_is_counted()
    {
        SkipWithoutTheStore();
        var store = Store();

        var kpi = await store.GetKpiSummaryAsync(Counted, Ct);
        var first = (await store.SearchDevicesAsync(Catalogue(null, DeviceListSort.Bindings), Ct)).Rows[0];
        var detail = await store.GetDeviceAsync(first.Tac, Ct);

        Assert.NotNull(detail);
        Assert.Equal(kpi.ActiveBindings, detail.NetworkNow);
    }

    [Fact]
    public async Task Vendor_movement_names_no_such_vendor()
    {
        SkipWithoutTheStore();

        var movement = await Store().GetVendorMovementAsync(null, null, VendorRanking.Share, 500, Ct);

        Assert.DoesNotContain(movement.Rows, r => r.Vendor == NoHandset);
    }

    [Fact]
    public async Task The_rows_themselves_are_still_in_current_state()
    {
        SkipWithoutTheStore();

        // One block of numbers: the table is ordered by number, and all of it is a billion versions.
        var held = await ScalarAsync("""
            SELECT count() FROM sqm.binding_current FINAL
            WHERE msisdn BETWEEN 9120000000 AND 9129999999 AND imei = '000000' AND active = 1
            """);

        Assert.True(held > 0);
    }
}
