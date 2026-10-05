using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Application.Devices;
using Sqm.Application.Sql;
using Sqm.Infrastructure.ClickHouse;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Integration.Tests;

/// <summary>
/// A model's first appearance is the first daily file that named one of its countable handsets; the
/// dump's models are never new; and the list, the arrivals and network age read exactly that.
/// </summary>
/// <remarks>
/// Against the real ClickHouse, through the real store and reader, in a scratch database that
/// migration 025 is applied to as it ships. Days are loaded the way the import loads them.
/// </remarks>
[Collection("clickhouse-scratch")]
public sealed class ModelArrivalTests : IAsyncLifetime
{
    private const string Endpoint = "http://localhost:18123/";
    private const string User = "sqm_ingest";
    private const string Password = "sqm_dev";

    private static readonly DateOnly May1 = new(2026, 5, 1);
    private static readonly DateOnly May2 = new(2026, 5, 2);

    private readonly string _database = "itest_models_" + Guid.NewGuid().ToString("N")[..12];
    private readonly HttpClient _http = new(new SocketsHttpHandler { PooledConnectionIdleTimeout = TimeSpan.FromSeconds(5) })
    {
        Timeout = TimeSpan.FromMinutes(2),
    };
    private string? _unavailable;

    public async ValueTask InitializeAsync()
    {
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}")));

        try
        {
            if (await QueryAsync("EXISTS TABLE sqm.binding_event") != "1")
            {
                _unavailable = "ClickHouse is up but has no sqm schema to copy";
                return;
            }

            await QueryAsync($"CREATE DATABASE {_database}");
            foreach (var table in new[] { "binding_event", "binding_current", "binding_snapshot", "binding_by_imsi", "binding_by_imei", "agg_device_model", "mart_ready" })
            {
                await QueryAsync($"CREATE TABLE {_database}.{table} AS sqm.{table}");
            }

            // sqm.tac is a view over the active GSMA version.
            await QueryAsync($"CREATE TABLE {_database}.tac (tac String, brandName String, manufacturer String, marketingName String, deviceType String) ENGINE = Memory");
            await QueryAsync($"""
                INSERT INTO {_database}.tac VALUES
                    ('35000001', 'Samsung', 'Samsung Korea', 'Galaxy A32', 'Smartphone'),
                    ('35000002', 'Quectel', 'Quectel', 'EC200U-EU', 'Modem'),
                    ('35000003', 'Apple', 'Apple Inc', 'iPhone 17', 'Smartphone')
                """);

            foreach (var statement in SqlScript.Split(MigrationText("025_tac_day.sql").Replace("sqm.", _database + ".", StringComparison.Ordinal)))
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

    private static string MigrationText(string name) => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Migrations", "analytics", name));

    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
    }

    private Options<ClickHouseOptions> Connection() => new(new ClickHouseOptions
    {
        ConnectionString = $"Host=localhost;Port=18123;Database={_database};Username={User};Password={Password}",
    });

    private sealed class Options<T>(T value) : IOptions<T> where T : class
    {
        public T Value { get; } = value;
    }

    private ClickHouseIngestionStore Store() => new(Connection(), new Factory(), NullLogger<ClickHouseIngestionStore>.Instance);

    private ClickHouseModelArrivalReader Reader() => new(Connection(), new Factory());

    private async Task<string> QueryAsync(string sql)
    {
        using var response = await _http.PostAsync(
            Endpoint, new StringContent(sql, Encoding.UTF8, "text/plain"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        return response.IsSuccessStatusCode ? body.Trim() : throw new InvalidOperationException($"ClickHouse refused: {body}");
    }

    private static async Task LoadAsync(ClickHouseIngestionStore store, DateOnly day, string rows)
    {
        var ct = TestContext.Current.CancellationToken;
        var sequence = await store.ResolveSequenceForDateAsync(day, ct);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("msisdn,imsi,imei,label\n" + rows));
        await store.LoadDailyEventsAsync(day, sequence, stream, null, ct);
    }

    [Fact]
    public async Task First_appearances_come_from_the_daily_files_and_the_dump_is_never_new()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var store = Store();
        var reader = Reader();

        Assert.False((await reader.GetStateAsync(ct)).Complete);

        // The dump knew the Samsung model.
        await QueryAsync($"INSERT INTO {_database}.binding_snapshot (msisdn, imsi, imei) VALUES (9120000001, 432110000000001, '35000001000001')");

        // 1 May: two Quectel modules, a shifted IMEI ('3' + its first seven digits is the Quectel TAC),
        // and a handset of a TAC GSMA does not know. 2 May: an Apple handset, a Quectel removed, and a
        // second Samsung - a model the dump already knew, so seen in a file but never new.
        await LoadAsync(store, May1,
            "9120000002,432110000000002,35000002000002,add\n" +
            "9120000003,432110000000003,35000002000003,add\n" +
            "9120000005,432110000000005,50000020000000,add\n" +
            "9120000006,432110000000006,99999999000001,add\n");
        await LoadAsync(store, May2,
            "9120000004,432110000000004,35000003000004,add\n" +
            "9120000002,432110000000002,35000002000002,remove\n" +
            "9120000009,432110000000009,35000001000009,add\n");

        Assert.Equal(new Sqm.Application.DataImport.TacDayRefresh(1, 1), await store.RefreshTacDumpAsync(ct));
        Assert.Equal(new Sqm.Application.DataImport.TacDayRefresh(2, 3), await store.RefreshTacDayAsync(May1, ct));
        Assert.Equal(new Sqm.Application.DataImport.TacDayRefresh(3, 3), await store.RefreshTacDayAsync(May2, ct));
        // Idempotent: a day written twice holds the same rows.
        await store.RefreshTacDayAsync(May1, ct);

        Assert.Equal(
            "1970-01-01\t35000001\t1\t1\t0\n" +
            "2026-05-01\t35000002\t2\t2\t2\n" +
            "2026-05-01\t99999999\t1\t1\t1\n" +
            "2026-05-02\t35000001\t1\t1\t1\n" +
            "2026-05-02\t35000002\t1\t1\t0\n" +
            "2026-05-02\t35000003\t1\t1\t1",
            await QueryAsync($"SELECT data_date, tac, imeis, sims, adds FROM {_database}.tac_day ORDER BY data_date, tac FORMAT TSV"));

        var state = await reader.GetStateAsync(ct);
        Assert.Equal((true, May2), (state.Complete, state.DataThrough));

        // Handsets now, from the latest ready delivery's device mart.
        await QueryAsync($"INSERT INTO {_database}.agg_device_model (seq, data_date, tac, bindings, handsets, sims, subscribers) VALUES (2, '2026-05-02', '35000003', 1, 1, 1, 1)");
        await QueryAsync($"INSERT INTO {_database}.mart_ready (seq, completed_at, statements) VALUES (2, now64(3), 15)");

        NewModelQuery Query(bool knownOnly = true, string? brand = null, string? type = null) =>
            new(May1, May2, brand, type, knownOnly, 1, 50);

        var page = await reader.ListAsync(Query(), ct);
        Assert.Equal(["35000003", "35000002"], page.Rows.Select(r => r.Tac));
        Assert.Equal(new NewModelRow("35000003", "Apple", "iPhone 17", "Smartphone", May2, 1, 1, 1, 1), page.Rows[0]);
        Assert.Equal((May1, 2, 2L, (long?)null), (page.Rows[1].FirstSeen, page.Rows[1].DaysSeen, page.Rows[1].FirstDayImeis, page.Rows[1].Handsets));

        Assert.Equal(["35000003", "35000002", "99999999"], (await reader.ListAsync(Query(knownOnly: false), ct)).Rows.Select(r => r.Tac));
        Assert.Equal(["35000002"], (await reader.ListAsync(Query(type: "Modem"), ct)).Rows.Select(r => r.Tac));
        Assert.Equal(["35000003"], (await reader.ListAsync(Query(brand: "aPPle"), ct)).Rows.Select(r => r.Tac));
        Assert.Empty((await reader.ListAsync(Query() with { From = May2.AddDays(1), To = May2.AddDays(5) }, ct)).Rows);

        Assert.Equal([new ModelArrivalPeriod(May1, 3, 2)], await reader.ArrivalsAsync(weekly: false, ct));

        Assert.Equal(new ModelFirstSeen("35000001", true, May2, 1), await reader.FirstSeenAsync("35000001", ct));
        Assert.Equal(new ModelFirstSeen("35000002", false, May1, 2), await reader.FirstSeenAsync("35000002", ct));
        Assert.Null(await reader.FirstSeenAsync("12345678", ct));
    }

    [Fact]
    public void Network_age_is_days_since_first_seen_and_a_lower_bound_for_the_dump()
    {
        Assert.Equal(1, NetworkAge.Days(May1, false, May2));
        Assert.Equal(97, NetworkAge.Days(null, true, May2));   // 2026-01-26 .. 2026-05-02, at least
        Assert.Null(NetworkAge.Days(null, false, May2));
        Assert.Null(NetworkAge.Days(May1, false, null));
    }
}
