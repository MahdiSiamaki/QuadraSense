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
            foreach (var table in new[] { "binding_event", "binding_current", "binding_by_imei", "tac_all", "tac_active", "tac_vendor_map" })
            {
                await QueryAsync($"CREATE TABLE {_database}.{table} AS sqm.{table}");
            }

            await QueryAsync($"CREATE MATERIALIZED VIEW {_database}.mv_by_imei TO {_database}.binding_by_imei AS SELECT imei, msisdn, imsi, active, last_change_seq, last_change_date FROM {_database}.binding_current");

            foreach (var migration in new[] { "022_binding_history.sql", "029_tac_radio.sql", "030_planning_snapshot.sql", "031_planning_lifecycle.sql" })
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
            await QueryAsync($"CREATE VIEW {_database}.tac AS SELECT * FROM {_database}.tac_all WHERE version_id = 1");

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
            // Handset lifecycle: numbers 30-37 are one case each, every binding ended so the classes
            // above do not move; number 1's phone is still held. Allocation: 35000001 2023, 35100001
            // 2005, 35200001 2019.
            await QueryAsync($"""
                INSERT INTO {_database}.binding_current (msisdn, imsi, imei, active, last_change_seq, last_change_date) VALUES
                    (9120000030, 432110000000030, '35100001000030', 0, 1, '2026-03-02'),
                    (9120000030, 432110000000030, '35000001000031', 0, 1, '2026-04-10'),
                    (9120000031, 432110000000031, '35100001000040', 0, 1, '2026-04-01'),
                    (9120000031, 432110000000031, '35000001000041', 0, 1, '2026-04-20'),
                    (9120000032, 432110000000032, '35200001000050', 0, 1, '2026-02-10'),
                    (9120000032, 432110000000032, '35000001000051', 0, 1, '2026-03-20'),
                    (9120000033, 432110000000033, '35200001000060', 0, 1, '2026-02-10'),
                    (9120000033, 432110000000033, '35000001000061', 0, 1, '2026-03-25'),
                    (9120000034, 432110000000034, '35200001000070', 0, 1, '2026-03-01'),
                    (9120000034, 432110000000034, '35200001000071', 0, 1, '2026-03-01'),
                    (9120000034, 432110000000034, '35000001000072', 0, 1, '2026-03-15'),
                    (9120000035, 432110000000035, '35100001000080', 0, 1, '2026-02-05'),
                    (9120000035, 432110000000035, '35000001000081', 0, 1, '2026-02-20'),
                    (9120000036, 432110000000036, '35000001000090', 0, 1, '2026-02-20'),
                    (9120000036, 432110000000036, '35000001000095', 0, 1, '2026-03-01'),
                    (9120000037, 432110000000037, '35100001000100', 0, 1, '2026-03-01'),
                    (9120000037, 432110000000037, '35000001000105', 0, 1, '2026-03-01'),
                    (9120000037, 432110000000037, '35000001000110', 0, 1, '2026-03-20')
                """);
            await QueryAsync($"""
                INSERT INTO {_database}.binding_history (month, msisdn, imsi, imei, in_dump, first_date, last_date, adds, removes, events) VALUES
                    (202602, 9120000001, 432110000000001, '35000001000001', 0, '2026-02-01', '2026-02-01', 1, 0, []),
                    (202602, 9120000030, 432110000000030, '35100001000030', 0, '2026-02-01', '2026-03-02', 1, 1, []),
                    (202603, 9120000030, 432110000000030, '35000001000031', 0, '2026-03-01', '2026-04-10', 1, 1, []),
                    (202602, 9120000031, 432110000000031, '35100001000040', 0, '2026-02-01', '2026-04-01', 1, 1, []),
                    (202602, 9120000031, 432110000000031, '35000001000041', 0, '2026-02-15', '2026-04-20', 1, 1, []),
                    (202602, 9120000032, 432110000000032, '35200001000050', 0, '2026-02-01', '2026-02-10', 1, 1, []),
                    (202603, 9120000032, 432110000000032, '35000001000051', 0, '2026-03-03', '2026-03-20', 1, 1, []),
                    (202602, 9120000033, 432110000000033, '35200001000060', 0, '2026-02-01', '2026-02-10', 1, 1, []),
                    (202603, 9120000033, 432110000000033, '35000001000061', 0, '2026-03-18', '2026-03-25', 1, 1, []),
                    (202602, 9120000034, 432110000000034, '35200001000070', 0, '2026-02-01', '2026-03-01', 1, 1, []),
                    (202602, 9120000034, 432110000000034, '35200001000071', 0, '2026-02-01', '2026-03-01', 1, 1, []),
                    (202603, 9120000034, 432110000000034, '35000001000072', 0, '2026-03-01', '2026-03-15', 1, 1, []),
                    (0, 9120000035, 432110000000035, '35100001000080', 1, '2025-12-27', '2026-02-05', 0, 1, []),
                    (202602, 9120000035, 432110000000035, '35000001000081', 0, '2026-02-04', '2026-02-20', 1, 1, []),
                    (202602, 9120000036, 432110000000036, '35000001000090', 0, '2026-02-01', '2026-02-20', 1, 1, []),
                    (202602, 9120000036, 432110000000036, '35000001000095', 0, '2026-02-20', '2026-03-01', 1, 1, []),
                    (202602, 9120000037, 432110000000037, '35100001000100', 0, '2026-02-01', '2026-03-01', 1, 1, []),
                    (202602, 9120000037, 432110000000037, '35000001000105', 0, '2026-02-28', '2026-03-01', 1, 1, []),
                    (202603, 9120000037, 432110000000037, '35000001000110', 0, '2026-03-02', '2026-03-20', 1, 1, [])
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

    [Fact]
    public async Task A_change_of_handset_is_a_successor_within_the_windows_and_nothing_else()
    {
        Assert.SkipWhen(_unavailable is not null, _unavailable ?? "");
        var ct = TestContext.Current.CancellationToken;

        var run = await BuildAsync(ct);

        // cohort | days | outcome (1 replaced, 2 ended, 3 still held) | kind (1 newer, 4 same model) | spells
        var tenure = await QueryAsync($"""
            SELECT concatWithSeparator('|', toString(cohort), toString(days), toString(outcome), toString(kind), toString(sum(spells)))
            FROM {_database}.planning_tenure WHERE run_id = {run.RunId}
            GROUP BY cohort, days, outcome, kind ORDER BY cohort, days FORMAT TSVRaw
            """);
        Assert.Equal(
            [
                "0|39|1|1|1",        // 35: a handset from the initial dump, replaced - cohort 0, never a month
                "202602|9|2|0|2",    // 33: gone 36 days before the next arrived - ended; and 36's second phone
                "202602|16|2|0|1",   // 35: its new phone, ended with nothing after it
                "202602|19|1|4|1",   // 36: the same model again
                "202602|28|1|1|2",   // 30: the old phone left the day after the new one came; 34 likewise, its twin set aside
                "202602|29|1|1|1",   // 37: a one-day sighting in between is no handset - the change is to the next
                "202602|30|1|1|1",   // 32: a 21-day gap is still a change
                "202602|59|2|0|1",   // 31: two phones side by side for 45 days - no change
                "202602|64|2|0|1",   // 31: the other one
                "202602|89|3|0|1",   // 1: still held at the delivery, censored there
                "202603|7|2|0|1",
                "202603|14|2|0|1",
                "202603|17|2|0|1",
                "202603|18|2|0|1",
                "202603|40|2|0|1",
            ],
            tenure.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

        // month | from | to | kind | changes
        var changes = await QueryAsync($"""
            SELECT concatWithSeparator('|', toString(month), from_tac, to_tac, toString(kind), toString(sum(changes)))
            FROM {_database}.planning_change WHERE run_id = {run.RunId}
            GROUP BY month, from_tac, to_tac, kind ORDER BY month, from_tac, to_tac FORMAT TSVRaw
            """);
        Assert.Equal(
            ["202602|35000001|35000001|4|1", "202602|35100001|35000001|1|1", "202603|35100001|35000001|1|2", "202603|35200001|35000001|1|2"],
            changes.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

        // Handsets held now, by when the network first saw them: number 1's phone, and the modem.
        var age = await QueryAsync($"""
            SELECT concatWithSeparator('|', tac, toString(first_month), toString(sum(handsets)))
            FROM {_database}.planning_device_age WHERE run_id = {run.RunId}
            GROUP BY tac, first_month ORDER BY tac FORMAT TSVRaw
            """);
        Assert.Equal(
            ["35000001|202602|1", "86000001|202603|1"],
            age.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

        // As the page reads it. February's eleven spells: five changed (days 19, 28, 28, 29, 30), six
        // censored (9, 9, 16, 59, 64, 89). Kaplan-Meier: 1 - (7/8)(5/7)(4/5)(3/4) = 62.5% changed within 30
        // days, half by day 29, and no answer at 90: nobody in it was followed that long.
        var view = (await Reader().GetLifecycleAsync(run.RunId, PlanningFilter.None, ct))!;
        Assert.Equal(6, view.Changes);
        Assert.Equal(["newer|5", "older|0", "not-stated|0", "same-model|1"], view.Kinds.Select(k => $"{k.Kind}|{k.Changes}"));
        Assert.Equal(("2026-02", "2026-03"), (view.FirstMonth, view.LastMonth));
        Assert.Equal(["2026-02|11|5|62.5||29", "2026-03|5|0|0||"],
            view.Cohorts.Select(c => $"{c.Cohort}|{c.Spells}|{c.Changed}|{c.ChangedBy30}|{c.ChangedBy90}|{c.MedianDays}"));
        Assert.Equal((1L, 1L), (view.Dump!.Spells, view.Dump.Changed));
        Assert.Equal(["2026-02|1", "2026-03|1"], view.DeviceAge.Select(a => $"{a.Key}|{a.Handsets}"));
        Assert.Equal(["2021|1", "2023|1"], view.ModelAge.Select(a => $"{a.Key}|{a.Handsets}"));

        // A run being built reports its parts across every table: two chunks of four tables.
        var next = await Store().PlanPlanningRunAsync(await Store().ReadPlanningInputsAsync(ct), new PlanningOptions { Chunks = 2 }, ct);
        var progress = (await Reader().GetStatusAsync(ct)).Running!;
        Assert.Equal((0, 8), (progress.ChunksDone, progress.ChunksTotal));
        await Store().EndPlanningRunAsync(next, "abandoned", "a test of the progress", ct);

        // A run without the lifecycle - built before it existed - says so instead of answering zero.
        Assert.True(view.Built);
        foreach (var chunk in new[] { 0, 1 })
        {
            await QueryAsync($"ALTER TABLE {_database}.planning_tenure DROP PARTITION ({run.RunId}, {chunk})");
        }

        var before = (await Reader().GetLifecycleAsync(run.RunId, PlanningFilter.None, ct))!;
        Assert.False(before.Built);
        Assert.Equal(0, before.Changes);
        Assert.Contains(before.NoData, n => n.Topic == "This run");
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

    private ClickHousePlanningReader Reader() => new(
        Options.Create(new ClickHouseOptions { ConnectionString = ConnectionString }), new Factory());

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
