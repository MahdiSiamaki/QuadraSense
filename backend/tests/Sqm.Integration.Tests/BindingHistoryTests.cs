using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Application.DataImport;
using Sqm.Application.Sql;
using Sqm.Application.Timeline;
using Sqm.Infrastructure.ClickHouse;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Integration.Tests;

/// <summary>
/// The binding history holds exactly the event log's events - by number, by SIM and by IMEI -
/// whatever order days arrive in, and after a day is corrected or a write is cut short.
/// </summary>
/// <remarks>
/// <para>
/// Against the real ClickHouse, through the real <see cref="ClickHouseIngestionStore"/>, in a
/// scratch database that migration 022 is applied to as it ships. The truth is the event log
/// itself: for every binding, its events in the history must be its events in the log, no more and
/// no fewer - the property a timeline depends on.
/// </para>
/// <para>
/// Scale is not proven here: a range size of two forces the month rebuild through several ranges
/// on a handful of bindings. The real-data measurements are in the migration's header.
/// </para>
/// </remarks>
[Collection("clickhouse-scratch")]
public sealed class BindingHistoryTests : IAsyncLifetime
{
    private const string Endpoint = "http://localhost:18123/";
    private const string User = "sqm_ingest";
    private const string Password = "sqm_dev";

    private static readonly DateOnly May1 = new(2026, 5, 1);
    private static readonly DateOnly May2 = new(2026, 5, 2);
    private static readonly DateOnly May3 = new(2026, 5, 3);
    private static readonly DateOnly May4 = new(2026, 5, 4);
    private static readonly DateOnly Jun1 = new(2026, 6, 1);
    private static readonly DateOnly Jun2 = new(2026, 6, 2);
    private static readonly DateOnly Jun3 = new(2026, 6, 3);

    private static readonly string[] Tables = ["binding_history", "binding_history_by_imsi", "binding_history_by_imei"];

    private readonly string _database = "itest_history_" + Guid.NewGuid().ToString("N")[..12];
    // Idle connections dropped before ClickHouse's 10 s keep-alive closes them: one full-suite run
    // failed here with "connection forcibly closed" and no failed query on the server.
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
            foreach (var table in new[] { "binding_event", "binding_current", "binding_snapshot", "binding_by_imsi", "binding_by_imei" })
            {
                await QueryAsync($"CREATE TABLE {_database}.{table} AS sqm.{table}");
            }

            // sqm.tac is a view over the active GSMA version; the timeline reads four of its columns.
            await QueryAsync($"CREATE TABLE {_database}.tac (tac String, brandName String, manufacturer String, marketingName String) ENGINE = Memory");

            // Current state's copies follow it as they do in production, for the timeline's "active".
            await QueryAsync($"CREATE MATERIALIZED VIEW {_database}.mv_by_imsi TO {_database}.binding_by_imsi AS SELECT imsi, msisdn, imei, active, last_change_seq, last_change_date FROM {_database}.binding_current");
            await QueryAsync($"CREATE MATERIALIZED VIEW {_database}.mv_by_imei TO {_database}.binding_by_imei AS SELECT imei, msisdn, imsi, active, last_change_seq, last_change_date FROM {_database}.binding_current");

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
        Path.Combine(AppContext.BaseDirectory, "Migrations", "analytics", "022_binding_history.sql"));

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
            ConnectionString =
                $"Host=localhost;Port=18123;Database={database ?? _database};Username={User};Password={Password}",
            FoldKeysPerRange = 2,
            HistoryEventsPerRange = 2,
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

    private static string K(int n) => $"({9_120_000_000 + n}, {432_110_000_000_000 + n}, '3500000000{n:0000}')";

    private static string Row(int n, string label) =>
        string.Create(CultureInfo.InvariantCulture, $"{9_120_000_000 + n},{432_110_000_000_000 + n},3500000000{n:0000},{label}\n");

    /// <summary>What SqmDailyProcessor does with one file, minus validation and marts.</summary>
    private static async Task<HistoryRefresh> ImportAsync(ClickHouseIngestionStore store, DateOnly day, params string[] rows)
    {
        var ct = TestContext.Current.CancellationToken;

        if (await store.CountEventsForDateAsync(day, ct) > 0)
        {
            await store.RemoveDayAsync(day, ct);
        }

        var sequence = await store.ResolveSequenceForDateAsync(day, ct);
        await using (var stream = new MemoryStream(Encoding.UTF8.GetBytes("msisdn,imsi,imei,label\n" + string.Concat(rows))))
        {
            await store.LoadDailyEventsAsync(day, sequence, stream, null, ct);
        }

        await store.FoldDayAsync(day, ct);
        return await store.RefreshHistoryForDayAsync(day, ct);
    }

    /// <summary>Bindings whose events in <paramref name="table"/> differ from the log's - zero when exact.</summary>
    private Task<string> MismatchesAsync(string table) => QueryAsync($"""
        SELECT count() FROM (
            SELECT msisdn, imsi, imei, arraySort(groupArrayArray(events)) AS e, sum(adds) AS a, sum(removes) AS r
            FROM {_database}.{table} WHERE month > 0 GROUP BY msisdn, imsi, imei
        ) AS h
        FULL OUTER JOIN (
            SELECT msisdn, imsi, imei, arraySort(groupArray((data_date, seq, toUInt8(label)))) AS e,
                   countIf(label = 'add') AS a, countIf(label = 'remove') AS r
            FROM {_database}.binding_event GROUP BY msisdn, imsi, imei
        ) AS l USING (msisdn, imsi, imei)
        WHERE h.e != l.e OR h.a != l.a OR h.r != l.r
        """);

    [Fact]
    public async Task The_history_holds_exactly_the_logs_events_through_late_corrected_and_interrupted_days()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var store = Store();

        // In order: four new days, each added, two of them in June.
        Assert.Equal(HistoryRefreshKind.Added, (await ImportAsync(store, May1, Row(1, "remove"), Row(2, "add"))).Kind);
        Assert.Equal(HistoryRefreshKind.Added, (await ImportAsync(store, May2, Row(3, "add"))).Kind);
        Assert.Equal(HistoryRefreshKind.Added, (await ImportAsync(store, May4, Row(1, "add"), Row(2, "remove"))).Kind);
        Assert.Equal(HistoryRefreshKind.Added, (await ImportAsync(store, Jun1, Row(3, "remove"), Row(4, "add"))).Kind);

        // A missing day, imported late: never seen, so it is simply added.
        Assert.Equal(HistoryRefreshKind.Added, (await ImportAsync(store, May3, Row(6, "add"))).Kind);

        // A corrected file for a day already in: its month is rebuilt, so K3's withdrawn add goes.
        var corrected = await ImportAsync(store, May2, Row(7, "add"));
        Assert.Equal(HistoryRefreshKind.RebuiltMonth, corrected.Kind);
        Assert.Contains("already in the history", corrected.Reason, StringComparison.Ordinal);

        // A write cut short: June 2 left pending with its events in twice, as an insert that landed
        // and then died before the ledger said so, and was retried, would leave it. The next June
        // day must rebuild June, not add to the remains.
        Assert.Equal(HistoryRefreshKind.Added, (await ImportAsync(store, Jun2, Row(1, "remove"))).Kind);
        await QueryAsync($"INSERT INTO {_database}.binding_history_day VALUES ('2026-06-02', 'pending', 1, now64(3))");
        await QueryAsync($"""
            INSERT INTO {_database}.binding_history
            SELECT 202606, msisdn, imsi, imei, 0, data_date, data_date, 0, 1, [(data_date, seq, 2)]
            FROM {_database}.binding_event WHERE data_date = '2026-06-02'
            """);
        var afterCrash = await ImportAsync(store, Jun3, Row(5, "remove"));
        Assert.Equal(HistoryRefreshKind.RebuiltMonth, afterCrash.Kind);
        Assert.Contains("did not finish", afterCrash.Reason, StringComparison.Ordinal);

        foreach (var table in Tables)
        {
            Assert.Equal("0", await MismatchesAsync(table));
        }

        // Every day the log holds is recorded as written.
        Assert.Equal("0", await QueryAsync(
            $"SELECT count() FROM (SELECT DISTINCT data_date FROM {_database}.binding_event) WHERE data_date NOT IN (SELECT data_date FROM {_database}.binding_history_day FINAL WHERE state = 'done')"));

        // Rows sit in the month of their events, so a rebuild of one month cannot touch another.
        Assert.Equal("202605\n202606", await QueryAsync($"SELECT DISTINCT month FROM {_database}.binding_history ORDER BY month FORMAT TSV"));
    }

    [Fact]
    public async Task The_initial_dump_is_loaded_with_its_window_and_no_events()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        await QueryAsync($"INSERT INTO {_database}.binding_snapshot (msisdn, imsi, imei) VALUES {K(1)}, {K(5)}");

        Assert.Equal(2, await Store().RebuildHistoryDumpAsync(TestContext.Current.CancellationToken));

        foreach (var table in Tables)
        {
            Assert.Equal("2\t2\t2025-12-27\t1970-01-01\t0", await QueryAsync(
                $"SELECT count(), sum(in_dump), min(first_date), max(last_date), sum(length(events)) FROM {_database}.{table} WHERE month = 0 FORMAT TSV"));
        }

        // Loading it again replaces it rather than doubling it.
        await Store().RebuildHistoryDumpAsync(TestContext.Current.CancellationToken);
        Assert.Equal("2", await QueryAsync($"SELECT count() FROM {_database}.binding_history WHERE month = 0"));
    }

    private ClickHouseTimelineStore Reader() => new(
        Options.Create(new ClickHouseOptions
        {
            ConnectionString = $"Host=localhost;Port=18123;Database={_database};Username={User};Password={Password}",
        }),
        new Factory(),
        NullLogger<ClickHouseTimelineStore>.Instance,
        TimeProvider.System);

    /// <summary>
    /// A handset from the dump, removed on 1 May and added again on 4 May: read back by IMEI, SIM and
    /// number, with its two periods, and "active" from current state.
    /// </summary>
    [Fact]
    public async Task A_timeline_is_read_by_number_SIM_and_handset_once_the_history_is_complete()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var store = Store();

        await QueryAsync($"INSERT INTO {_database}.binding_snapshot (msisdn, imsi, imei) VALUES {K(1)}");
        await QueryAsync($"INSERT INTO {_database}.binding_current (msisdn, imsi, imei, active, last_change_seq, last_change_date) SELECT msisdn, imsi, imei, 1, 0, NULL FROM {_database}.binding_snapshot");
        await QueryAsync($"INSERT INTO {_database}.tac (tac, brandName, manufacturer, marketingName) VALUES ('35000000', 'Samsung', 'Samsung Korea', 'Galaxy A32')");

        await ImportAsync(store, May1, Row(1, "remove"), Row(2, "add"));
        await ImportAsync(store, May4, Row(1, "add"));

        // Every day is written but the dump is not: not complete.
        Assert.Equal(HistoryReadiness.Building, await Reader().GetReadinessAsync(ct));
        await store.RebuildHistoryDumpAsync(ct);
        Assert.Equal(HistoryReadiness.Ready, await Reader().GetReadinessAsync(ct));

        var reader = Reader();
        var byImei = await reader.GetAsync(TimelineCentre.Imei, "35000000000001", 100, ct);
        var binding = Assert.Single(byImei.Bindings);
        Assert.Equal((9_120_000_001UL, 432_110_000_000_001UL, true, true), (binding.Msisdn, binding.Imsi, binding.InDump, binding.Active));
        Assert.Equal(("35000000", "Samsung", "Galaxy A32"), (binding.Tac, binding.Brand, binding.Model));

        var timeline = TimelineBuilder.Build(
            TimelineCentre.Imei, "35000000000001", byImei, new TimelineVisibility(true, true, true, (_, v) => v, false), 100, May4);
        Assert.Equal(
            [new("2025-12-27", true, "2026-05-01"), new("2026-05-04", false, null)],
            timeline.Bindings[0].Periods);
        Assert.Equal(0, timeline.Summary.StateDisagreements);

        Assert.Equal(1, (await reader.GetAsync(TimelineCentre.Imsi, "432110000000001", 100, ct)).Total);
        Assert.Equal("35000000000002", Assert.Single((await reader.GetAsync(TimelineCentre.Msisdn, "9120000002", 100, ct)).Bindings).Imei);

        Assert.Equal(new EntitySeen(new DateOnly(2025, 12, 27), true, May4), await reader.GetSeenAsync(TimelineCentre.Imsi, "432110000000001", ct));
        Assert.Equal(new EntitySeen(May1, false, May1), await reader.GetSeenAsync(TimelineCentre.Msisdn, "9120000002", ct));
        Assert.Null(await reader.GetSeenAsync(TimelineCentre.Imei, "35999999999999", ct));
        Assert.Equal(May4, await reader.DataThroughAsync(ct));
    }

    [Fact]
    public async Task Without_the_migration_the_import_is_told_and_nothing_fails()
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
            await QueryAsync($"CREATE TABLE {bare}.binding_event AS sqm.binding_event");
            var outcome = await Store(bare).RefreshHistoryForDayAsync(May1, TestContext.Current.CancellationToken);
            Assert.Equal(HistoryRefreshKind.NotDeployed, outcome.Kind);
        }
        finally
        {
            await QueryAsync($"DROP DATABASE IF EXISTS {bare}");
        }
    }
}
