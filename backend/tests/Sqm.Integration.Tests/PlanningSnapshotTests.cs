using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Application.Planning;
using Sqm.Application.Sql;
using Sqm.Infrastructure.ClickHouse;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Integration.Tests;

/// <summary>
/// The planning snapshot classes each number by all of its active handsets at once, reports what it
/// cannot assess with the reason, settles a dual-SIM twin by the documented pair rule and no other, and
/// counts the SIMs an IoT device has carried.
/// </summary>
/// <remarks>
/// Against the real ClickHouse, through the real <see cref="ClickHouseIngestionStore"/> and radio
/// profile parse, in a scratch database that migrations 022, 029 and 030 are applied to as they ship.
/// Built in two chunks, so the cut points must tile the numbers exactly.
/// </remarks>
[Collection("clickhouse-scratch")]
public sealed class PlanningSnapshotTests : IAsyncLifetime
{
    private const string Endpoint = "http://localhost:18123/";
    private const string User = "sqm_ingest";
    private const string Password = "sqm_dev";

    private readonly string _database = "itest_planning_" + Guid.NewGuid().ToString("N")[..12];
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
            foreach (var table in new[] { "binding_event", "binding_current", "binding_by_imei", "tac_all", "tac_active" })
            {
                await QueryAsync($"CREATE TABLE {_database}.{table} AS sqm.{table}");
            }

            await QueryAsync($"CREATE MATERIALIZED VIEW {_database}.mv_by_imei TO {_database}.binding_by_imei AS SELECT imei, msisdn, imsi, active, last_change_seq, last_change_date FROM {_database}.binding_current");

            foreach (var migration in new[] { "022_binding_history.sql", "029_tac_radio.sql", "030_planning_snapshot.sql" })
            {
                foreach (var statement in SqlScript.Split(MigrationText(migration).Replace("sqm.", _database + ".", StringComparison.Ordinal)))
                {
                    await QueryAsync(statement);
                }
            }

            // The catalogue: an LTE phone with an eSIM, a GSM-only handheld whose eUICC fields read 00,
            // a 2G/3G feature phone (whose twin TAC, +100, the catalogue does not hold), an NB-IoT /
            // LTE-M modem, and a record that lists no band.
            await QueryAsync($"""
                INSERT INTO {_database}.tac_all (version_id, tac, deviceType, removableEUICC, nonremovableEUICC, ntnConnectivity, allocationDate, authenticatedIMSEmergencyCallSupport, bandDetails) VALUES
                    (1, '35000001', 'Smartphone', '0', '1', 'No', '01-Jan-2023', 'Y', '4G: LTE FDD BAND 3 | 2G/3G: GSM 900 | Radio Interface: NONE'),
                    (1, '35100001', 'Handheld', '00', '00', 'Not Known', '01-Jan-2005', 'Not Known', '2G/3G: GSM 900,GSM 1800 | Radio Interface: NONE'),
                    (1, '35200001', 'Mobile Phone/Feature phone', '0', '0', 'No', '01-Jan-2019', 'N', '2G/3G: GSM 900,WCDMA FDD Band 1 | Radio Interface: NONE'),
                    (1, '86000001', 'Modem', '0', '0', 'No', '01-Jan-2021', 'N', 'LPWAN: Cat-NB1,Cat-M1 | Radio Interface: NONE'),
                    (1, '01010900', 'Handheld', '00', '00', 'Not Known', '20-Apr-2000', 'Not Known', 'Radio Interface: NONE')
                """);
            await QueryAsync($"INSERT INTO {_database}.tac_active (singleton, version_id, activated_at) VALUES (1, 1, now64(3))");
            await QueryAsync($"CREATE VIEW {_database}.tac AS SELECT tac FROM {_database}.tac_all WHERE version_id = 1");

            // Each number is one case; the comment says what it must come out as.
            await QueryAsync($"""
                INSERT INTO {_database}.binding_current (msisdn, imsi, imei, active, last_change_seq, last_change_date) VALUES
                    (9120000001, 432110000000001, '35000001000001', 1, 1, '2026-05-01'),
                    (9120000002, 432110000000002, '35100001000001', 1, 1, '2026-05-01'),
                    (9120000003, 432110000000003, '35100001000002', 1, 1, '2026-05-01'),
                    (9120000003, 432110000000003, '35000001000003', 1, 1, '2026-05-01'),
                    (9120000004, 432110000000014, '86000001000001', 1, 1, '2026-05-01'),
                    (9120000005, 432110000000005, '35200001000005', 1, 1, '2026-05-01'),
                    (9120000005, 432110000000005, '000000', 1, 1, '2026-05-01'),
                    (9120000006, 432110000000006, '35200001000006', 1, 1, '2026-05-01'),
                    (9120000006, 432110000000006, '50000011234560', 1, 1, '2026-05-01'),
                    (9120000007, 432110000000007, '35200001000007', 1, 1, '2026-05-01'),
                    (9120000007, 432110000000007, '99999999000007', 1, 1, '2026-05-01'),
                    (9120000008, 432110000000008, '35200001000008', 1, 1, '2026-05-01'),
                    (9120000008, 432110000000008, '35200101000008', 1, 1, '2026-05-01'),
                    (9120000009, 432110000000009, '35200001000009', 1, 1, '2026-05-01'),
                    (9120000009, 432110000000009, '01010900000009', 1, 1, '2026-05-01'),
                    (9120000010, 432110000000010, '35000001000010', 0, 1, '2026-05-01'),
                    (9120000010, 432110000000010, '35100001000010', 1, 1, '2026-05-01'),
                    (9120000011, 432110000000011, '35200001000011', 1, 1, '2026-05-01'),
                    (9120000011, 432110000000011, '35200101000013', 1, 1, '2026-05-01')
                """);

            // The modem's life: three SIMs on two numbers, one still on it.
            await QueryAsync($"""
                INSERT INTO {_database}.binding_history (month, msisdn, imsi, imei, in_dump, first_date, last_date, adds, removes, events) VALUES
                    (202603, 9120000004, 432110000000012, '86000001000001', 0, '2026-03-01', '2026-03-20', 1, 1, [('2026-03-01', 1, 1), ('2026-03-20', 1, 2)]),
                    (202604, 9120000004, 432110000000013, '86000001000001', 0, '2026-04-01', '2026-04-20', 1, 1, [('2026-04-01', 1, 1), ('2026-04-20', 1, 2)]),
                    (202605, 9120000012, 432110000000014, '86000001000001', 0, '2026-05-01', '2026-05-01', 1, 0, [('2026-05-01', 1, 1)])
                """);
            await QueryAsync($"INSERT INTO {_database}.binding_history_day (data_date, state, events, updated_at) VALUES ('2026-05-01', 'done', 1, now64(3))");
            await QueryAsync($"INSERT INTO {_database}.binding_event (seq, data_date, msisdn, imsi, imei, label) VALUES (1, '2026-05-01', 9120000001, 432110000000001, '35000001000001', 'add')");
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

    [Fact]
    public async Task Every_number_is_classed_by_all_its_handsets_and_unknowns_say_why()
    {
        Assert.SkipWhen(_unavailable is not null, _unavailable ?? "");
        var ct = TestContext.Current.CancellationToken;

        var run = await BuildAsync(ct);
        Assert.Equal("published", (await Store().LatestPlanningRunAsync("published", ct))?.State);
        Assert.Equal(1, run.AsOfSequence);

        // msisdn | sunset class | reason | esim | iot | gsm only | paired
        var rows = await QueryAsync($"""
            SELECT concatWithSeparator('|', toString(msisdn), toString(sunset), toString(reason), toString(esim),
                   toString(iot), toString(gsm_only), toString(paired))
            FROM {_database}.planning_number WHERE run_id = {run.RunId} ORDER BY msisdn FORMAT TSVRaw
            """);

        Assert.Equal(
            [
                "9120000001|1|0|1|0|0|0",   // an LTE phone: ready
                "9120000002|3|0|2|0|1|0",   // GSM only, every handset known: exposed; eSIM not stated (00)
                "9120000003|1|0|1|0|0|0",   // one LTE handset beside a GSM one is enough
                "9120000004|2|0|0|1|0|0",   // NB-IoT / LTE-M only: ready, on its own row
                "9120000005|4|2|2|0|0|0",   // beside an unknown device (000000): not assessable
                "9120000006|4|1|2|0|0|0",   // beside a shifted IMEI: the feed defect is the reason
                "9120000007|4|3|2|0|0|0",   // beside a TAC the catalogue lacks
                "9120000008|3|0|0|0|0|1",   // the unregistered twin (+100, same serial) is this handset's other radio
                "9120000009|4|4|2|0|0|0",   // beside a model whose record lists no band
                "9120000010|3|0|2|0|1|0",   // the inactive LTE binding does not count
                "9120000011|4|3|2|0|0|0",   // serials two apart are not a pair
            ],
            rows.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task The_summary_counts_numbers_exactly_and_the_breakdown_names_the_models()
    {
        Assert.SkipWhen(_unavailable is not null, _unavailable ?? "");
        var ct = TestContext.Current.CancellationToken;

        var run = await BuildAsync(ct);

        var summary = await QueryAsync($"""
            SELECT concatWithSeparator('|', toString(class), toString(sum(numbers)), toString(sum(bindings)))
            FROM {_database}.planning_summary WHERE run_id = {run.RunId} AND measure = 'sunset'
            GROUP BY class ORDER BY class FORMAT TSVRaw
            """);
        Assert.Equal(
            ["1|2|3", "2|1|1", "3|3|4", "4|5|10"],
            summary.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

        // The exposed numbers' models: the GSM handheld on two numbers, the feature phone and its twin on one.
        var models = await QueryAsync($"""
            SELECT concatWithSeparator('|', tac, toString(uniqMerge(numbers)))
            FROM {_database}.planning_breakdown WHERE run_id = {run.RunId} AND sunset = 3
            GROUP BY tac ORDER BY tac FORMAT TSVRaw
            """);
        Assert.Equal(
            ["35100001|2", "35200001|1", "35200101|1"],
            models.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task An_iot_device_counts_the_sims_it_has_carried_and_those_on_it_now()
    {
        Assert.SkipWhen(_unavailable is not null, _unavailable ?? "");
        var ct = TestContext.Current.CancellationToken;

        var run = await BuildAsync(ct);

        var device = await QueryAsync($"""
            SELECT concatWithSeparator('|', imei, toString(sims_ever), toString(numbers_ever), toString(sims_active),
                   toString(numbers_active), toString(sims_90), toString(first_date))
            FROM {_database}.planning_iot_device WHERE run_id = {run.RunId} FORMAT TSVRaw
            """);

        Assert.Equal("86000001000001|3|2|1|1|3|2026-03-01", device);
    }

    private async Task<PlanningRun> BuildAsync(CancellationToken ct)
    {
        var store = Store();
        await TacStore().ParseRadioAsync(1, ct);

        var inputs = await store.ReadPlanningInputsAsync(ct);
        Assert.True(inputs.Ready, inputs.NotReadyReason);

        var options = new PlanningOptions { Chunks = 2 };
        var run = await store.PlanPlanningRunAsync(inputs, options, ct);
        while (run.Next() is { } next)
        {
            run = await store.BuildPlanningChunkAsync(run, next.Table, next.Chunk, options, ct);
        }

        Assert.Null(await store.TryPublishPlanningAsync(run, ct));
        return run;
    }

    private static string MigrationText(string name) => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Migrations", "analytics", name));

    private ClickHouseIngestionStore Store() => new(
        Options.Create(new ClickHouseOptions { ConnectionString = ConnectionString }),
        new Factory(),
        NullLogger<ClickHouseIngestionStore>.Instance);

    private ClickHouseTacVersionStore TacStore() => new(
        Options.Create(new ClickHouseOptions { ConnectionString = ConnectionString }),
        new Factory(),
        NullLogger<ClickHouseTacVersionStore>.Instance);

    private string ConnectionString =>
        $"Host=localhost;Port=18123;Database={_database};Username={User};Password={Password}";

    private async Task<string> QueryAsync(string sql)
    {
        using var response = await _http.PostAsync(
            Endpoint, new StringContent(sql, Encoding.UTF8, "text/plain"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        return response.IsSuccessStatusCode ? body.Trim() : throw new InvalidOperationException($"ClickHouse refused: {body}");
    }

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
}
