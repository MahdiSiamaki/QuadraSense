using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Application.Sql;
using Sqm.Domain.Quality;
using Sqm.Infrastructure.ClickHouse;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Integration.Tests;

/// <summary>
/// A day's file is measured exactly: every defect counted once, and nothing counted that is not one.
/// </summary>
/// <remarks>
/// Against the real ClickHouse, in a scratch database made for the run. The two feed-quality
/// tables are created by running migration 020 itself there, so the DDL under test is the DDL
/// that ships; the event log is copied from the live schema.
/// </remarks>
[Collection("clickhouse-scratch")]
public sealed class FeedQualityTests : IAsyncLifetime
{
    private const string Endpoint = "http://localhost:18123/";

    private static readonly DateOnly Day = new(2026, 9, 20);

    private readonly string _database = "itest_dq_" + Guid.NewGuid().ToString("N")[..12];
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private string? _unavailable;

    public async ValueTask InitializeAsync()
    {
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("sqm_ingest:sqm_dev")));

        try
        {
            if (await QueryAsync("EXISTS TABLE sqm.binding_event") != "1")
            {
                _unavailable = "ClickHouse is up but has no sqm schema to copy";
                return;
            }

            await QueryAsync($"CREATE DATABASE {_database}");
            await QueryAsync($"CREATE TABLE {_database}.binding_event AS sqm.binding_event");
            await QueryAsync($"CREATE TABLE {_database}.tac_active AS sqm.tac_active");
            await QueryAsync($"INSERT INTO {_database}.tac_active (singleton, version_id, activated_at) VALUES (1, 7, now64(3))");

            // Three models GSMA knows. Only the tac column is read.
            await QueryAsync($"CREATE TABLE {_database}.tac (tac String) ENGINE = Memory");
            await QueryAsync($"INSERT INTO {_database}.tac VALUES ('35004012'), ('86453906'), ('01518100')");

            foreach (var statement in SqlScript.Split(MigrationText().Replace("sqm.", _database + ".", StringComparison.Ordinal)))
            {
                await QueryAsync(statement);
            }
        }
        catch (HttpRequestException ex)
        {
            _unavailable = $"no ClickHouse at {Endpoint}: {ex.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_unavailable is null)
        {
            await QueryAsync($"DROP DATABASE IF EXISTS {_database}");
        }

        _http.Dispose();
    }

    /// <summary>The migration as it ships, copied into the test output by the project file.</summary>
    private static string MigrationText() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Migrations", "analytics", "020_feed_quality.sql"));

    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        });
    }

    private ClickHouseIngestionStore Writer() => new(
        Options.Create(new ClickHouseOptions
        {
            ConnectionString = $"Host=localhost;Port=18123;Database={_database};Username=sqm_ingest;Password=sqm_dev",
        }),
        new Factory(),
        NullLogger<ClickHouseIngestionStore>.Instance);

    private ClickHouseFeedQualityStore Reader() => new(
        Options.Create(new ClickHouseOptions
        {
            ConnectionString = $"Host=localhost;Port=18123;Database={_database};Username=sqm_app;Password=sqm_dev",
        }),
        new Factory());

    private async Task<string> QueryAsync(string sql)
    {
        using var response = await _http.PostAsync(
            Endpoint, new StringContent(sql, Encoding.UTF8, "text/plain"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        return response.IsSuccessStatusCode
            ? body.Trim()
            : throw new InvalidOperationException($"ClickHouse refused: {body}");
    }

    /// <summary>One day with one of everything, each on its own number.</summary>
    /// <remarks>
    /// <c>35004012123456</c> is a real Galaxy TAC. Shifted the way the 15 September files shift it -
    /// first digit dropped, a 0 added - it becomes <c>50040121234560</c>, whose "TAC" 50040121 GSMA
    /// does not know and which a leading 3 restores.
    /// </remarks>
    private static readonly string[] Rows =
    [
        "9120000001,432110000000001,35004012000011,add",     // ordinary
        "9120000002,432110000000002,86453906000022,remove",  // ordinary
        "9120000003,432110000000003,50040121234560,add",     // shifted
        "9120000004,432110000000004,50040121234560,remove",  // the same shifted IMEI, another number: two rows
        "9120000005,432110000000005,99999999000010,add",     // unknown TAC ending in 0, but no digit restores it
        "9120000006,432110000000006,77777777000011,add",     // unknown TAC, not ending in 0
        "9120000007,432110000000007,000000,add",              // unknown device
        "9120000008,432110000000008,12345678,add",           // malformed: eight digits
        "9120000009,432110000000009,86453906000099,add",     // one SIM, two numbers
        "9120000010,432110000000009,86453906000099,remove",
        "9120000011,432110000000011,35004012000111,remove",  // one SIM, one number, two handsets: not multi-number
        "9120000011,432110000000011,35004012000112,add",
    ];

    private static async Task LoadAsync(ClickHouseIngestionStore store)
    {
        var csv = "msisdn,imsi,imei,label\n" + string.Join('\n', Rows) + "\n";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        await store.LoadDailyEventsAsync(Day, 1, stream, null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Every_defect_is_counted_once_and_nothing_else_is_counted()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var store = Writer();

        await LoadAsync(store);
        await store.RefreshFeedQualityForDayAsync(Day, ct);

        var day = Assert.Single(await Reader().GetDaysAsync(Day, Day, ct));

        Assert.Equal(new FeedQualityDay(
            Date: Day,
            Rows: 12,
            Sims: 10,
            UnknownDeviceRows: 1,
            MalformedImeiRows: 1,
            UnknownTacRows: 4,        // the two shifted rows, 99999999…, 77777777…
            ShiftedImeiRows: 2,       // only the IMEI a leading digit restores
            MultiNumberSims: 1,
            MultiNumberSimRows: 2,
            TacVersionId: 7), day);

        Assert.Equal("432110000000009\t2\t2", await QueryAsync(
            $"SELECT imsi, numbers, rows FROM {_database}.dq_multi_number_sim_day WHERE data_date = '2026-09-20'"));
    }

    [Fact]
    public async Task Measuring_a_day_again_replaces_it_rather_than_adding_to_it()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var store = Writer();

        await LoadAsync(store);
        await store.RefreshFeedQualityForDayAsync(Day, ct);
        await store.RefreshFeedQualityForDayAsync(Day, ct);

        Assert.Equal(12, Assert.Single(await Reader().GetDaysAsync(null, null, ct)).Rows);
        Assert.Equal("1", await QueryAsync($"SELECT count() FROM {_database}.dq_multi_number_sim_day"));
    }

    [Fact]
    public async Task A_range_returns_only_its_days_oldest_first()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var store = Writer();

        foreach (var offset in new[] { 2, 0, 1 })
        {
            var date = Day.AddDays(offset);
            var csv = "msisdn,imsi,imei,label\n9120000001,432110000000001,35004012000011,add\n";
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
            await store.LoadDailyEventsAsync(date, offset + 1, stream, null, ct);
            await store.RefreshFeedQualityForDayAsync(date, ct);
        }

        var days = await Reader().GetDaysAsync(Day.AddDays(1), Day.AddDays(2), ct);

        Assert.Equal(
            ["2026-09-21", "2026-09-22"],
            days.Select(d => d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
    }
}
