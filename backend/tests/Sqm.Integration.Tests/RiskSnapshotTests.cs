using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Application.Risk;
using Sqm.Application.Sql;
using Sqm.Domain.Risk;
using Sqm.Infrastructure.ClickHouse;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Integration.Tests;

/// <summary>
/// The risk snapshot's measures are exact on a history built by hand, each key is written once
/// however the run is chunked, and a run is published only when nothing it read has changed.
/// </summary>
/// <remarks>
/// Against the real ClickHouse, through the real <see cref="ClickHouseIngestionStore"/>, in a scratch
/// database that migrations 020, 022 and 024 are applied to as they ship. Days are imported the way the
/// import does it - load, fold, history - so the snapshot reads what production would. The windows end
/// on 31 May: 30 days from 2 May, 20 from 12 May, 7 from 25 May.
/// </remarks>
[Collection("clickhouse-scratch")]
public sealed class RiskSnapshotTests : IAsyncLifetime
{
    private const string Endpoint = "http://localhost:18123/";
    private const string User = "sqm_ingest";
    private const string Password = "sqm_dev";

    // Handsets. P1 is shared; the others carry one SIM each. All in GSMA TAC 35000001.
    private const string P1 = "35000001000001";
    private const string P11 = "35000001000011";
    private const string P12 = "35000001000012";
    private const string P13 = "35000001000013";
    private const string P15 = "35000001000015";
    private const string P21 = "35000001000021";
    private const string P22 = "35000001000022";
    private const string P31 = "35000001000031";
    private const string P41 = "35000001000041";

    // The 15 September shape: ends in 0, unknown TAC, and '3' + its first seven digits is 35000001.
    private const string Shifted = "50000010000000";

    private readonly string _database = "itest_risksnap_" + Guid.NewGuid().ToString("N")[..12];
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
            foreach (var table in new[] { "binding_event", "binding_current", "binding_snapshot", "binding_by_imsi", "binding_by_imei", "tac_active" })
            {
                await QueryAsync($"CREATE TABLE {_database}.{table} AS sqm.{table}");
            }

            // sqm.tac is a view over the active GSMA version; the countable-IMEI test reads its tac column.
            await QueryAsync($"CREATE TABLE {_database}.tac (tac String, brandName String, manufacturer String, marketingName String) ENGINE = Memory");
            await QueryAsync($"INSERT INTO {_database}.tac VALUES ('35000001', 'Samsung', 'Samsung Korea', 'Galaxy A32')");
            await QueryAsync($"INSERT INTO {_database}.tac_active (singleton, version_id, activated_at) VALUES (1, 1, now64(3))");

            await QueryAsync($"CREATE MATERIALIZED VIEW {_database}.mv_by_imsi TO {_database}.binding_by_imsi AS SELECT imsi, msisdn, imei, active, last_change_seq, last_change_date FROM {_database}.binding_current");
            await QueryAsync($"CREATE MATERIALIZED VIEW {_database}.mv_by_imei TO {_database}.binding_by_imei AS SELECT imei, msisdn, imsi, active, last_change_seq, last_change_date FROM {_database}.binding_current");

            foreach (var migration in new[] { "020_feed_quality.sql", "022_binding_history.sql", "023_risk_sim_change_day.sql", "024_risk_snapshot.sql" })
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

    private ClickHouseIngestionStore Store() => new(
        Options.Create(new ClickHouseOptions
        {
            ConnectionString = $"Host=localhost;Port=18123;Database={_database};Username={User};Password={Password}",
        }),
        new Factory(),
        NullLogger<ClickHouseIngestionStore>.Instance);

    /// <summary>Floors of two, so a handful of bindings crosses them; three chunks, so the cut points are exercised.</summary>
    private static RiskOptions Settings(int chunks = 3) => new()
    {
        Floors = new RiskFloorOptions { SimImeis30 = 2, ImeiSims30 = 2, ImeiSimsEver = 2, ImeiSimsNotRemoved = 2 },
        Compute = new RiskComputeOptions { Chunks = chunks },
    };

    private async Task<string> QueryAsync(string sql)
    {
        using var response = await _http.PostAsync(
            Endpoint, new StringContent(sql, Encoding.UTF8, "text/plain"),
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        return response.IsSuccessStatusCode ? body.Trim() : throw new InvalidOperationException($"ClickHouse refused: {body}");
    }

    private static string M(int n) => (9_120_000_000L + n).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string S(int n) => (432_110_000_000_000L + n).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Add(int number, int sim, string imei) => $"{M(number)},{S(sim)},{imei},add\n";

    private static string Remove(int number, int sim, string imei) => $"{M(number)},{S(sim)},{imei},remove\n";

    /// <summary>What SqmDailyProcessor does with one file, minus validation, marts and feed quality.</summary>
    private static async Task ImportAsync(ClickHouseIngestionStore store, DateOnly day, params string[] rows)
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
        await store.RefreshHistoryForDayAsync(day, ct);
    }

    private static DateOnly May(int day) => new(2026, 5, day);

    /// <summary>
    /// SIM 1 is added to six handsets: one before the window, three countable, one shifted, one on a
    /// day the feed listed it under several numbers. Handset P1 takes SIMs 1, 2 and 3 in the window, SIM
    /// 4 before it (and loses it), SIM 5 from the initial dump, and SIM 2 again under a second number on
    /// a multi-number day. SIM 6 reaches the floor exactly; handset P41 reaches it on numbers alone.
    /// </summary>
    private async Task SeedAsync(ClickHouseIngestionStore store)
    {
        await QueryAsync($"""
            INSERT INTO {_database}.binding_current (msisdn, imsi, imei, active, last_change_seq, last_change_date)
            VALUES ({M(5)}, {S(5)}, '{P1}', 1, 0, NULL)
            """);
        await QueryAsync($"""
            INSERT INTO {_database}.dq_multi_number_sim_day (data_date, imsi, numbers, rows)
            VALUES ('2026-05-27', {S(2)}, 2, 2), ('2026-05-28', {S(1)}, 2, 2)
            """);

        await ImportAsync(store, May(1), Add(1, 1, P11), Add(4, 4, P1));
        await ImportAsync(store, May(3), Remove(4, 4, P1));
        await ImportAsync(store, May(5), Add(1, 1, P13));
        await ImportAsync(store, May(10), Add(3, 3, P1));
        await ImportAsync(store, May(15), Add(1, 1, P12));
        await ImportAsync(store, May(20), Add(7, 6, P21), Add(7, 6, P22), Add(9, 9, P41));
        await ImportAsync(store, May(21), Add(10, 9, P41));
        await ImportAsync(store, May(26), Add(1, 1, P1), Add(2, 2, P1));
        await ImportAsync(store, May(27), Add(1, 1, Shifted), Add(6, 2, P1));
        await ImportAsync(store, May(28), Add(1, 1, P15));
        await ImportAsync(store, May(31), Add(8, 8, P31));
    }

    private static async Task<RiskRun> BuildAsync(ClickHouseIngestionStore store, RiskOptions settings)
    {
        var ct = TestContext.Current.CancellationToken;
        var inputs = await store.ReadInputsAsync(settings, ct);
        Assert.True(inputs.Ready, inputs.NotReadyReason);

        var run = await store.PlanRunAsync(inputs, settings, ct);
        while (run.Next() is { } next)
        {
            run = await store.BuildChunkAsync(run, next.Table, next.Chunk, settings, ct);
        }

        return run;
    }

    [Fact]
    public async Task The_measures_are_exact_and_each_key_is_written_once()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var store = Store();
        Assert.True(await store.DeployedAsync(ct));
        await SeedAsync(store);

        var settings = Settings();
        var run = await BuildAsync(store, settings);
        Assert.Equal(May(31), run.AsOf);
        Assert.Null(await store.TryPublishAsync(run, settings, ct));
        Assert.Equal(run.RunId, (await store.LatestRunAsync("published", ct))?.RunId);

        // imsi, imeis 30 clean/raw, imeis 20, tacs 20, imeis 7 clean/raw, adds, set aside, add days,
        // most IMEIs in one day and that day, top TACs. SIM 1: P13, P12, P1 clean; the shifted IMEI and
        // the multi-number day's P15 raw only; P11 before the window.
        Assert.Equal(
            $"{S(1)}\t3\t5\t2\t1\t1\t3\t5\t2\t3\t1\t2026-05-05\t['35000001']\n" +
            $"{S(6)}\t2\t2\t2\t1\t0\t0\t2\t0\t1\t2\t2026-05-20\t['35000001']",
            await QueryAsync($"""
                SELECT imsi, imeis_30, imeis_30_raw, imeis_20, tacs_20, imeis_7, imeis_7_raw, adds_30,
                       adds_set_aside_30, add_days_30, max_imeis_one_day_30, max_day_30, top_tacs_20
                FROM {_database}.risk_sim_window WHERE run_id = {run.RunId} ORDER BY imsi FORMAT TSV
                """));

        // P1: SIMs 1, 2, 3; numbers 1, 2, 3 - SIM 2's second number came on its multi-number day, so
        // that add is set aside. P41: one SIM, but two numbers, which alone reach the floor.
        Assert.Equal(
            $"{P1}\t3\t3\t2\t3\t4\t1\t2\t2\t2026-05-26\n" +
            $"{P41}\t1\t1\t0\t2\t2\t0\t2\t1\t2026-05-20",
            await QueryAsync($"""
                SELECT imei, sims_30, sims_30_raw, sims_7, numbers_30, adds_30, adds_set_aside_30,
                       add_days_30, max_sims_one_day_30, max_day_30
                FROM {_database}.risk_imei_window WHERE run_id = {run.RunId} ORDER BY imei FORMAT TSV
                """));

        // P1 all time: SIMs 1-5, six numbers, six bindings; SIMs 1-3 not removed with a date, SIM 5 only
        // from the dump, SIM 4 removed.
        Assert.Equal(
            $"{P1}\t5\t6\t6\t3\t1",
            await QueryAsync($"""
                SELECT imei, sims_ever, numbers_ever, bindings, sims_not_removed_dated, sims_not_removed_dump
                FROM {_database}.risk_imei_lifetime WHERE run_id = {run.RunId} ORDER BY imei FORMAT TSV
                """));

        // One chunk or three, the same rows.
        var single = await BuildAsync(store, Settings(chunks: 1));
        foreach (var (table, key) in new[] { ("risk_sim_window", "imsi"), ("risk_imei_window", "imei"), ("risk_imei_lifetime", "imei") })
        {
            string Rows(ulong runId) =>
                $"SELECT * EXCEPT (run_id, chunk) FROM {_database}.{table} WHERE run_id = {runId} ORDER BY {key} FORMAT TSV";

            Assert.Equal(await QueryAsync(Rows(single.RunId)), await QueryAsync(Rows(run.RunId)));
        }
    }

    private ClickHouseRiskReader Reader() => new(
        Options.Create(new ClickHouseOptions
        {
            ConnectionString = $"Host=localhost;Port=18123;Database={_database};Username={User};Password={Password}",
        }),
        new Factory(),
        NullLogger<ClickHouseRiskReader>.Instance);

    private static RiskListQuery Query(RiskRule rule, RiskView view, long threshold, double share = 1.0) =>
        new(rule, view, new Sqm.Domain.Risk.RiskThreshold(threshold), share, 1, 50);

    private static IEnumerable<string> Keys(RiskListPage page) => page.Rows.Select(r => r.Key);

    [Fact]
    public async Task The_reader_lists_what_the_worker_published_and_knows_when_it_is_stale()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var store = Store();
        await SeedAsync(store);

        // Number 1 changed SIM on three clean days and one multi-number day in the 7 days to 31 May;
        // number 2 once, and on 23 May, outside them.
        await QueryAsync($"""
            INSERT INTO {_database}.risk_sim_change_day (data_date, msisdn, new_count, old_count, set_aside, computed_at) VALUES
                ('2026-05-26', {M(1)}, 1, 1, 'none', now64(3)), ('2026-05-27', {M(1)}, 1, 1, 'none', now64(3)),
                ('2026-05-28', {M(1)}, 1, 1, 'none', now64(3)), ('2026-05-29', {M(1)}, 1, 1, 'multi_number', now64(3)),
                ('2026-05-30', {M(2)}, 1, 1, 'none', now64(3)), ('2026-05-23', {M(2)}, 1, 1, 'none', now64(3))
            """);

        var settings = Settings();
        var reader = Reader();
        Assert.Null((await reader.GetStateAsync(settings.Floors, ct)).Run);

        var run = await BuildAsync(store, settings);
        Assert.Null(await store.TryPublishAsync(run, settings, ct));

        var state = await reader.GetStateAsync(settings.Floors, ct);
        Assert.Equal((run.RunId, May(31), (string?)null), (state.Run?.RunId, state.Run?.AsOf, state.Stale));
        Assert.Equal(11, state.DaysWithData.Count);
        var published = state.Run!;

        // SIM 1: 3 clean IMEIs, 5 raw, 2 of 5 adds set aside.
        Assert.Equal([S(1)], Keys(await reader.ListAsync(published, Query(RiskRule.HighDeviceCount30, RiskView.Risk, 2), ct)));
        Assert.Empty(Keys(await reader.ListAsync(published, Query(RiskRule.HighDeviceCount30, RiskView.DataQuality, 2), ct)));
        // Over 3 only on the raw count: data quality, not risk.
        Assert.Empty(Keys(await reader.ListAsync(published, Query(RiskRule.HighDeviceCount30, RiskView.Risk, 3), ct)));
        Assert.Equal([S(1)], Keys(await reader.ListAsync(published, Query(RiskRule.HighDeviceCount30, RiskView.DataQuality, 3), ct)));
        // Two fifths set aside is more than a quarter allows: not assessable, so data quality.
        Assert.Empty(Keys(await reader.ListAsync(published, Query(RiskRule.HighDeviceCount30, RiskView.Risk, 2, 0.25), ct)));
        Assert.Equal([S(1)], Keys(await reader.ListAsync(published, Query(RiskRule.HighDeviceCount30, RiskView.DataQuality, 2, 0.25), ct)));

        // Handsets by numbers, largest first, each completed with its other table and its model.
        var numbers = await reader.ListAsync(published, Query(RiskRule.SharedImeiNumbers30, RiskView.Risk, 1), ct);
        Assert.Equal([P1, P41], Keys(numbers));
        Assert.Equal(2, numbers.Total);
        var p1 = numbers.Rows[0];
        Assert.Equal((5L, "35000001", "Samsung", "Galaxy A32"), (p1.ImeiLifetime?.SimsEver, p1.Tac, p1.Brand, p1.Model));
        Assert.Null(numbers.Rows[1].ImeiLifetime);

        // Numbers: three clean change days in the 7 to 31 May, one more before the screen.
        var changes = await reader.ListAsync(published, Query(RiskRule.RepeatedSimChange7, RiskView.Risk, 2), ct);
        Assert.Equal([M(1)], Keys(changes));
        Assert.Equal(new RiskNumberMeasures(3, 4, May(29)), changes.Rows[0].Number);

        var counts = await reader.CountAsync(published, new Dictionary<RiskRule, Sqm.Domain.Risk.RiskThreshold>
        {
            [RiskRule.HighDeviceCount30] = new(3),
            [RiskRule.SharedImeiSimsEver] = new(1),
            [RiskRule.RepeatedSimChange7] = new(0),
        }, 1.0, ct);
        Assert.Equal(
            [new RiskRuleCount(RiskRule.SharedImeiSimsEver, 1, 0), new RiskRuleCount(RiskRule.HighDeviceCount30, 0, 1), new RiskRuleCount(RiskRule.RepeatedSimChange7, 2, 0)],
            counts);

        var entity = await reader.GetEntityAsync(published, Sqm.Domain.Risk.RiskFamily.Imei, P1, ct);
        Assert.Equal((3L, 5L), (entity?.ImeiWindow?.Numbers30, entity?.ImeiLifetime?.SimsEver));
        Assert.Null(await reader.GetEntityAsync(published, Sqm.Domain.Risk.RiskFamily.Sim, S(3), ct));

        // A corrected day: the worker's fingerprint moves, and the reader computes the same one.
        await ImportAsync(store, May(31), Add(8, 8, P31[..^1] + "2"));
        Assert.NotNull((await reader.GetStateAsync(settings.Floors, ct)).Stale);
    }

    [Fact]
    public async Task A_run_is_not_published_when_a_day_arrived_or_a_key_is_doubled()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var store = Store();
        await SeedAsync(store);
        var settings = Settings();

        // Doubled: a chunk's rows written a second time under another chunk, as an orphaned INSERT would.
        var doubled = await BuildAsync(store, settings);
        await QueryAsync($"""
            INSERT INTO {_database}.risk_sim_window
            SELECT * REPLACE (toUInt16(99) AS chunk) FROM {_database}.risk_sim_window WHERE run_id = {doubled.RunId}
            """);
        Assert.Equal("risk_sim_window holds 2 duplicate key(s)", await store.TryPublishAsync(doubled, settings, ct));

        // Stale: the last day is corrected after the run was planned - same date, same number of
        // events, so only the history ledger's write time tells the two apart.
        var stale = await BuildAsync(store, settings);
        await ImportAsync(store, May(31), Add(8, 8, P31[..^1] + "2"));
        Assert.Equal("its inputs changed while it was being built", await store.TryPublishAsync(stale, settings, ct));

        // And a new day.
        var overtaken = await BuildAsync(store, settings);
        await ImportAsync(store, new DateOnly(2026, 6, 1), Add(8, 8, P31[..^1] + "3"));
        Assert.Equal("its inputs changed while it was being built", await store.TryPublishAsync(overtaken, settings, ct));
        Assert.Null(await store.LatestRunAsync("published", ct));

        // Unfinished: a run with a chunk missing.
        var inputs = await store.ReadInputsAsync(settings, ct);
        var planned = await store.PlanRunAsync(inputs, settings, ct);
        Assert.Equal("chunk 0 of SimWindow is not written yet", await store.TryPublishAsync(planned, settings, ct));
    }

    [Fact]
    public async Task Inputs_are_not_ready_while_a_day_is_being_written_and_floors_change_the_fingerprint()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var store = Store();
        await ImportAsync(store, May(1), Add(1, 1, P11));

        var ready = await store.ReadInputsAsync(Settings(), ct);
        Assert.True(ready.Ready, ready.NotReadyReason);

        var higher = Settings();
        higher.Floors.SimImeis30 = 3;
        Assert.NotEqual(ready.Fingerprint, (await store.ReadInputsAsync(higher, ct)).Fingerprint);

        await QueryAsync($"INSERT INTO {_database}.binding_history_day VALUES ('2026-05-02', 'pending', 1, now64(3))");
        var pending = await store.ReadInputsAsync(Settings(), ct);
        Assert.False(pending.Ready);
        Assert.Equal("1 day(s) of the binding history are still being written", pending.NotReadyReason);
    }

    [Fact]
    public async Task Old_runs_are_dropped_but_the_two_newest_published_and_a_running_one_are_kept()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var store = Store();

        // Ledger rows and one data row per run, written directly: what is kept depends on the ledger only.
        foreach (var (runId, state) in new[] { (1, "published"), (2, "abandoned"), (3, "published"), (4, "published"), (5, "running"), (6, "failed") })
        {
            await QueryAsync($"""
                INSERT INTO {_database}.risk_run (run_id, as_of, fingerprint, tac_version_id, state, chunks, sim_cuts, imei_cuts, done, note, updated_at)
                VALUES ({runId}, '2026-05-31', 0, 1, '{state}', 1, [], [], [], '', now64(3))
                """);
            await QueryAsync($"INSERT INTO {_database}.risk_sim_window (run_id, chunk, imsi) VALUES ({runId}, 0, {runId})");
        }

        Assert.Equal(3, await store.DropOldRunsAsync(ct));
        Assert.Equal("3\n4\n5", await QueryAsync($"SELECT run_id FROM {_database}.risk_sim_window ORDER BY run_id FORMAT TSV"));
    }
}
