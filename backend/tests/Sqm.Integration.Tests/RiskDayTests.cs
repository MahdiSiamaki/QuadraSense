using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Application.Sql;
using Sqm.Infrastructure.ClickHouse;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Integration.Tests;

/// <summary>
/// The risk pages' daily SIM changes are the dashboard's SIM changes, row for row - and a change on a
/// SIM the feed listed under several numbers is kept but set aside.
/// </summary>
/// <remarks>
/// Against the real ClickHouse, through the real <see cref="ClickHouseIngestionStore"/>, in a scratch
/// database that migrations 020 and 023 are applied to as they ship. The day is loaded the way the
/// import loads it; the dashboard's own mart refresh is the truth the rows are reconciled with.
/// </remarks>
[Collection("clickhouse-scratch")]
public sealed class RiskDayTests : IAsyncLifetime
{
    private const string Endpoint = "http://localhost:18123/";
    private const string User = "sqm_ingest";
    private const string Password = "sqm_dev";

    private static readonly DateOnly Day = new(2026, 5, 10);
    private static readonly DateOnly Unscreened = new(2026, 5, 11);

    private readonly string _database = "itest_riskday_" + Guid.NewGuid().ToString("N")[..12];
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
            foreach (var table in new[]
            {
                "binding_event", "binding_current", "binding_by_imsi", "binding_by_imei",
                "agg_change_daily", "agg_change_summary_daily", "agg_sim_change_daily", "agg_device_change_daily",
            })
            {
                await QueryAsync($"CREATE TABLE {_database}.{table} AS sqm.{table}");
            }

            foreach (var migration in new[] { "020_feed_quality.sql", "023_risk_sim_change_day.sql" })
            {
                foreach (var statement in SqlScript.Split(MigrationText(migration).Replace("sqm.", _database + ".", StringComparison.Ordinal)))
                {
                    await QueryAsync(statement);
                }
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

    private ClickHouseIngestionStore Store(string? database = null) => new(
        Options.Create(new ClickHouseOptions
        {
            ConnectionString = $"Host=localhost;Port=18123;Database={database ?? _database};Username={User};Password={Password}",
        }),
        new Factory(),
        NullLogger<ClickHouseIngestionStore>.Instance);

    private async Task<string> QueryAsync(string sql)
    {
        using var response = await _http.PostAsync(
            Endpoint, new StringContent(sql, Encoding.UTF8, "text/plain"),
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        return response.IsSuccessStatusCode ? body.Trim() : throw new InvalidOperationException($"ClickHouse refused: {body}");
    }

    private static async Task<int> LoadAsync(ClickHouseIngestionStore store, DateOnly day, string rows)
    {
        var ct = TestContext.Current.CancellationToken;
        var sequence = await store.ResolveSequenceForDateAsync(day, ct);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("msisdn,imsi,imei,label\n" + rows));
        await store.LoadDailyEventsAsync(day, sequence, stream, null, ct);
        return sequence;
    }

    [Fact]
    public async Task A_days_rows_are_the_dashboards_sim_changes_with_feed_defects_set_aside()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var store = Store();

        var sequence = await LoadAsync(store, Day,
            // A: lost one SIM, gained another - a SIM change.
            "9121000001,432110000000101,35000000000101,remove\n" +
            "9121000001,432110000000102,35000000000102,add\n" +
            // B: only gained a SIM - not a change.
            "9121000002,432110000000201,35000000000201,add\n" +
            // C: the same SIM removed and added, on another handset - a handset change, not a SIM change.
            "9121000003,432110000000301,35000000000301,remove\n" +
            "9121000003,432110000000301,35000000000302,add\n" +
            // E: a SIM change whose new SIM the feed listed under several numbers that day.
            "9121000004,432110000000401,35000000000401,remove\n" +
            "9121000004,432110000000402,35000000000402,add\n" +
            // F: one SIM lost, two gained.
            "9121000005,432110000000501,35000000000501,remove\n" +
            "9121000005,432110000000502,35000000000502,add\n" +
            "9121000005,432110000000503,35000000000503,add\n");

        // The day's feed quality as the monitor would have written it, with E's new SIM listed.
        await QueryAsync($"INSERT INTO {_database}.dq_daily (data_date, rows_total, sims_total, computed_at) VALUES ('{Day:yyyy-MM-dd}', 10, 9, now64(3))");
        await QueryAsync($"INSERT INTO {_database}.dq_multi_number_sim_day (data_date, imsi, numbers, rows) VALUES ('{Day:yyyy-MM-dd}', 432110000000402, 2, 2)");

        var written = await store.RefreshRiskDayAsync(Day, ct);

        Assert.NotNull(written);
        Assert.Equal(3, written.Changes);
        Assert.Equal(1, written.SetAside);
        Assert.True(written.QualityMeasured);

        // Row for row the dashboard's count: its own refresh, on the same day, says the same.
        await store.RefreshChangeMartsForDayAsync(Day, sequence, ct);
        Assert.Equal("3", await QueryAsync($"SELECT msisdn_changed FROM {_database}.agg_sim_change_daily WHERE data_date = '{Day:yyyy-MM-dd}'"));

        // F gained two SIMs, so which of their IMEIs is shown as "new" is not defined: compared as "either".
        Assert.Equal(
            "9121000001\t[432110000000102]\t[432110000000101]\t1\t1\t35000000000102\t35000000000101\tnone\n" +
            "9121000004\t[432110000000402]\t[432110000000401]\t1\t1\t35000000000402\t35000000000401\tmulti_number\n" +
            "9121000005\t[432110000000502,432110000000503]\t[432110000000501]\t2\t1\teither\t35000000000501\tnone",
            await QueryAsync($"""
                SELECT msisdn, arraySort(new_imsis), old_imsis, new_count, old_count,
                       if(new_count = 1, new_imei, 'either'), old_imei, set_aside
                FROM {_database}.risk_sim_change_day ORDER BY msisdn FORMAT TSV
                """));

        // Idempotent: written again, the day holds the same rows, not twice as many.
        await store.RefreshRiskDayAsync(Day, ct);
        Assert.Equal("3", await QueryAsync($"SELECT count() FROM {_database}.risk_sim_change_day WHERE data_date = '{Day:yyyy-MM-dd}'"));
        Assert.Contains(Day, await store.GetRiskDaysAsync(ct));
    }

    [Fact]
    public async Task A_day_whose_feed_quality_was_not_measured_is_kept_but_unscreened()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var store = Store();

        await LoadAsync(store, Unscreened,
            "9121000006,432110000000601,35000000000601,remove\n" +
            "9121000006,432110000000602,35000000000602,add\n");

        var written = await store.RefreshRiskDayAsync(Unscreened, ct);

        Assert.NotNull(written);
        Assert.False(written.QualityMeasured);
        Assert.Equal(1, written.Changes);
        Assert.Equal("dq_unavailable", await QueryAsync(
            $"SELECT set_aside FROM {_database}.risk_sim_change_day WHERE data_date = '{Unscreened:yyyy-MM-dd}'"));
    }

    [Fact]
    public async Task Without_the_migration_nothing_is_written_and_it_says_so()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var bare = _database + "_bare";
        await QueryAsync($"CREATE DATABASE {bare}");
        try
        {
            Assert.Null(await Store(bare).RefreshRiskDayAsync(Day, TestContext.Current.CancellationToken));
            Assert.Empty(await Store(bare).GetRiskDaysAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            await QueryAsync($"DROP DATABASE IF EXISTS {bare}");
        }
    }
}
