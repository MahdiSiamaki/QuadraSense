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
/// The band (N2) and satellite (N6) views: a model meets the operator's bands in exactly one way, a
/// match is never made against a list nobody configured, SA and NSA come only from what a record
/// states, and a satellite radio is not 3GPP NTN.
/// </summary>
/// <remarks>
/// Against the real ClickHouse through the real <see cref="ClickHousePlanningReader"/> and radio
/// profile parse, in a scratch database that migrations 029 and 030 are applied to as they ship. The
/// population is eight synthetic numbers, few enough that uniq() is exact.
/// </remarks>
[Collection("clickhouse-scratch")]
public sealed class PlanningModelViewTests : IAsyncLifetime
{
    private const string Endpoint = "http://localhost:18123/";
    private const string User = "sqm_ingest";
    private const string Password = "sqm_dev";

    private static readonly OperatorBandsOptions Operator = Bands([3, 7], [78]);

    private readonly string _database = "itest_planmodel_" + Guid.NewGuid().ToString("N")[..12];
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
            if (await QueryAsync("EXISTS TABLE sqm.agg_tac_population") != "1")
            {
                _unavailable = "ClickHouse is up but has no sqm schema to copy";
                return;
            }

            await QueryAsync($"CREATE DATABASE {_database}");
            foreach (var table in new[] { "tac_all", "tac_active", "agg_tac_population", "tac_vendor_map", "agg_device_model", "agg_change_summary_daily", "mart_ready" })
            {
                await QueryAsync($"CREATE TABLE {_database}.{table} AS sqm.{table}");
            }

            foreach (var migration in new[] { "029_tac_radio.sql", "030_planning_snapshot.sql" })
            {
                foreach (var statement in SqlScript.Split(MigrationText(migration).Replace("sqm.", _database + ".", StringComparison.Ordinal)))
                {
                    await QueryAsync(statement);
                }
            }

            // Six models, each a case. Their records are real GSMA shapes, shortened.
            await QueryAsync($"""
                INSERT INTO {_database}.tac_all (version_id, tac, manufacturer, marketingName, deviceType, removableEUICC, nonremovableEUICC, ntnConnectivity, allocationDate, authenticatedIMSEmergencyCallSupport, bandDetails) VALUES
                    (1, '35000001', 'Samsung', 'Galaxy One', 'Smartphone', '0', '1', '3GPP NTN & TN(GSO)', '01-Jan-2024', 'Y',
                        '5G NR: n1, n78 | 5G NA Options: 5G-2Series,5G-3Series | 4G: LTE FDD BAND 3, LTE FDD BAND 7 | 2G/3G: GSM 900 | Radio Interface: NONE | NTN: E-UTRA B256'),
                    (1, '35000002', 'Xiaomi', 'Redmi Two', 'Smartphone', '0', '0', 'No', '01-Jan-2022', 'Y',
                        '4G: LTE FDD BAND 3 | 2G/3G: GSM 900 | Radio Interface: NONE'),
                    (1, '35000003', 'Nokia', 'Old LTE', 'Mobile Phone/Feature phone', '00', '00', 'Not Known', '01-Jan-2016', 'N',
                        '4G: LTE FDD BAND 20 | 2G/3G: GSM 900 | Radio Interface: NONE'),
                    (1, '35000004', 'Thuraya', 'Sat Phone', 'Handheld', '00', '00', 'Not Known', '01-Jan-2018', 'N',
                        '4G: LTE FDD BAND 1, LTE TDD BAND 38 | 2G/3G: GSM 900,WCDMA FDD Band 1 | Radio Interface: SATELLITE'),
                    (1, '35000005', 'Acme', 'Plain LTE', 'Modem', '0', '0', 'No', '01-Jan-2020', 'N',
                        '2G/3G: GSM 900 | Radio Interface: LTE'),
                    (1, '35000006', 'Acme', 'NSA Five', 'Smartphone', '0', '0', 'No', '01-Jan-2023', 'Y',
                        '5G NR: n78 | 5G NA Options: 5G-3Series | 4G: LTE FDD BAND 3, LTE BAND X | Radio Interface: NONE')
                """);
            await QueryAsync($"INSERT INTO {_database}.tac_active (singleton, version_id, activated_at) VALUES (1, 1, now64(3))");
            await QueryAsync($"CREATE VIEW {_database}.tac AS SELECT * FROM {_database}.tac_all WHERE version_id = 1");

            // Delivery 7's per-model population, and a published run for it with a row per number and
            // its summary - the views' denominator. Number 4 holds two models; 6 an unknown device; 7 a
            // TAC the catalogue lacks; 8, Acme's two models, is put in the exposed class. As the snapshot
            // writes them, ready numbers keep no models.
            await QueryAsync($"""
                INSERT INTO {_database}.agg_tac_population (seq, tac, bindings, unknown_device, malformed_imei, subscribers, sims, handsets, first_seen, last_seen)
                SELECT 7, tac, count(), 0, 0, uniqState(msisdn), uniqState(msisdn), uniqState(imei), NULL, NULL
                FROM values('tac String, msisdn UInt64, imei String',
                    ('35000001', 1, '35000001000001'), ('35000002', 2, '35000002000002'), ('35000003', 3, '35000003000003'),
                    ('35000001', 4, '35000001000004'), ('35000002', 4, '35000002000004'), ('35000004', 5, '35000004000005'),
                    ('', 6, '00000000000000'), ('99999999', 7, '99999999000007'), ('35000005', 8, '35000005000008'),
                    ('35000006', 8, '35000006000008'))
                GROUP BY tac
                """);

            await QueryAsync($"""
                INSERT INTO {_database}.planning_number (run_id, chunk, msisdn, sunset, reason, esim, iot, gsm_only, handsets, bindings, paired, tacs) VALUES
                    (1, 1, 1, 1, 0, 0, 0, 0, 1, 1, 0, []), (1, 1, 2, 1, 0, 0, 0, 0, 1, 1, 1, []),
                    (1, 1, 3, 1, 0, 0, 0, 0, 1, 1, 0, []), (1, 1, 4, 1, 0, 0, 0, 0, 2, 2, 0, []),
                    (1, 1, 5, 1, 0, 0, 0, 0, 1, 1, 0, []), (1, 1, 6, 4, 2, 0, 0, 0, 1, 1, 0, ['']),
                    (1, 1, 7, 4, 3, 0, 0, 0, 1, 1, 0, ['99999999']), (1, 1, 8, 3, 0, 0, 1, 0, 2, 2, 0, ['35000005', '35000006'])
                """);
            await QueryAsync($"""
                INSERT INTO {_database}.planning_breakdown (run_id, chunk, sunset, reason, esim, tac, numbers, bindings, handsets)
                SELECT 1, 1, multiIf(tac IN ('', '99999999'), 4, tac IN ('35000005', '35000006'), 3, 1),
                       multiIf(tac = '', 2, tac = '99999999', 3, 0), 0, tac,
                       uniqState(msisdn), count(), uniqState(imei)
                FROM values('tac String, msisdn UInt64, imei String',
                    ('35000001', 1, '35000001000001'), ('35000002', 2, '35000002000002'), ('35000003', 3, '35000003000003'),
                    ('35000001', 4, '35000001000004'), ('35000002', 4, '35000002000004'), ('35000004', 5, '35000004000005'),
                    ('', 6, '00000000000000'), ('99999999', 7, '99999999000007'), ('35000005', 8, '35000005000008'),
                    ('35000006', 8, '35000006000008'))
                GROUP BY tac
                """);
            await QueryAsync($"""
                INSERT INTO {_database}.planning_summary (run_id, as_of_seq, as_of_date, measure, class, reason, numbers, bindings, handsets)
                VALUES (1, 7, '2026-10-01', 'sunset', 1, 0, 8, 10, 10)
                """);
            await QueryAsync($"""
                INSERT INTO {_database}.planning_run (run_id, as_of_seq, as_of_date, fingerprint, tac_version_id, parser_version, state, chunks, msisdn_cuts, imei_cuts, done, note, updated_at)
                VALUES (1, 7, '2026-10-01', 0, 1, 1, 'published', 1, [], [], [], '', now64(3))
                """);

            await new ClickHouseTacVersionStore(
                Options.Create(new ClickHouseOptions { ConnectionString = ConnectionString }),
                new Factory(),
                NullLogger<ClickHouseTacVersionStore>.Instance).ParseRadioAsync(1, TestContext.Current.CancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _unavailable = $"no ClickHouse at {Endpoint} ({ex.Message})";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_unavailable is null)
        {
            try
            {
                await QueryAsync($"DROP DATABASE IF EXISTS {_database}");
            }
            catch (HttpRequestException)
            {
                // The stack went away mid-run; nothing left to clean.
            }
        }

        _http.Dispose();
    }

    [Fact]
    public async Task Each_model_meets_the_operators_bands_in_one_way_and_sa_nsa_are_as_stated()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var bands = (await Reader().GetBandsAsync(null, PlanningFilter.None, Operator, TestContext.Current.CancellationToken))!;

        Assert.Equal(8, bands.TotalNumbers);
        Assert.Equal([3, 7], bands.OperatorLte);

        // Galaxy One lists 3 and 7: all. Redmi Two and NSA Five list 3: some. Old LTE (20) and Sat
        // Phone (1, 38): none. Plain LTE states LTE with no band at all: nothing to match.
        Assert.Equal(["lte-all|2|2", "lte-some|3|3", "lte-none|2|2", "lte-no-bands|1|1"],
            bands.LteMatch.Select(c => $"{c.Class}|{c.Numbers}|{c.Bindings}"));
        Assert.Equal(["nr-all|3|3","nr-some|0|0", "nr-none|0|0", "nr-no-bands|0|0"],
            bands.NrMatch.Select(c => $"{c.Class}|{c.Numbers}|{c.Bindings}"));

        // Galaxy One states 5G-2Series and 5G-3Series; NSA Five only the 3 series.
        Assert.Equal(["arch-both|2", "arch-sa|0", "arch-nsa|1", "arch-not-stated|0"],
            bands.Architecture.Select(c => $"{c.Class}|{c.Numbers}"));

        Assert.Equal(1, bands.Ambiguous.Numbers);
        Assert.Equal(["3|True|4", "7|True|2", "1|False|1", "20|False|1", "38|False|1"],
            bands.LteBands.Select(b => $"{b.Band}|{b.Operator}|{b.Numbers}"));
        Assert.Equal(["78|True|3", "1|False|2"], bands.NrBands.Select(b => $"{b.Band}|{b.Operator}|{b.Numbers}"));
        Assert.DoesNotContain(bands.NoData, n => n.Topic == "The operator's bands");
    }

    [Fact]
    public async Task Without_the_operators_bands_nothing_is_matched_and_the_view_says_so()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        foreach (var options in new[] { new OperatorBandsOptions(), Bands([0, 3], [78]) })
        {
            var bands = (await Reader().GetBandsAsync(null, PlanningFilter.None, options, ct))!;

            Assert.Empty(bands.OperatorLte);
            Assert.Empty(bands.LteMatch);
            Assert.Empty(bands.NrMatch);
            Assert.All(bands.LteBands, b => Assert.False(b.Operator));
            Assert.Contains(bands.NoData, n => n.Topic == "The operator's bands");

            var breakdown = (await Reader().GetBreakdownAsync(null, "bands", "lte-all", "model", PlanningFilter.None, 50, options, ct))!;
            Assert.Empty(breakdown.Rows);
        }

        // One band's population needs no list.
        var band = (await Reader().GetBreakdownAsync(null, "bands", "lte-3", "vendor", PlanningFilter.None, 50, new OperatorBandsOptions(), ct))!;
        Assert.Equal(["Samsung|2", "Xiaomi|2", "Acme|1"], band.Rows.Select(r => $"{r.Key}|{r.Numbers}"));
    }

    [Fact]
    public async Task A_satellite_radio_is_not_3gpp_ntn_and_unknowns_are_kept_apart()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var ntn = (await Reader().GetNtnAsync(null, PlanningFilter.None, ct))!;

        Assert.Equal(["ntn|2", "satellite|1", "not-capable|3", "not-stated|1"], ntn.Classes.Select(c => $"{c.Class}|{c.Numbers}"));
        Assert.Equal(["NTN and TN|2"], ntn.ByMode.Select(r => $"{r.Key}|{r.Numbers}"));
        Assert.Equal(["GSO|2"], ntn.ByOrbit.Select(r => $"{r.Key}|{r.Numbers}"));
        Assert.Equal(["B256|LTE band 256|2"], ntn.ByBand.Select(r => $"{r.Key}|{r.Label}|{r.Numbers}"));
        Assert.Equal(2, ntn.NotClassified.Numbers);

        // Every binding is in exactly one class or not classified.
        Assert.Equal(10, ntn.Classes.Sum(c => c.Bindings) + ntn.NotClassified.Bindings);

        var breakdown = (await Reader().GetBreakdownAsync(null, "ntn", "satellite", "model", PlanningFilter.None, 50, new OperatorBandsOptions(), ct))!;
        Assert.Equal(["Sat Phone|1"], breakdown.Rows.Select(r => $"{r.Key}|{r.Numbers}"));

        // By TAC, each row is one model, named.
        var byTac = (await Reader().GetBreakdownAsync(null, "ntn", "not-capable", "tac", PlanningFilter.None, 50, new OperatorBandsOptions(), ct))!;
        Assert.Equal(["35000002|Redmi Two|2", "35000005|Plain LTE|1", "35000006|NSA Five|1"], byTac.Rows.Select(r => $"{r.Key}|{r.Label}|{r.Numbers}"));
    }

    [Fact]
    public async Task A_filter_matching_several_models_narrows_the_numbers_to_those_holding_one()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        // Acme makes two models; number 8 holds both. A filter matching more than one model once
        // failed outright, in the sunset view and in a campaign list's count alike.
        var ct = TestContext.Current.CancellationToken;
        var acme = new PlanningFilter("Acme", null, null);

        var sunset = (await Reader().GetSunsetAsync(null, acme, ct))!;
        Assert.True(sunset.Filtered);
        Assert.Equal(1, sunset.TotalNumbers);
        Assert.Equal(1, sunset.Classes.Single(c => c.Class == "exposed").Numbers);
        Assert.Equal(1, sunset.ExposedIot);
        Assert.Equal(0, sunset.Paired);
        Assert.Equal(1, (await Reader().GetSunsetAsync(null, PlanningFilter.None, ct))!.Paired);

        Assert.Equal(1, await Reader().CountNumbersAsync(1, "sunset", "exposed", null, acme, ct));

        // A ready number keeps no models, so a list of ready numbers is not narrowed - not answered as empty.
        Assert.Null(await Reader().CountNumbersAsync(1, "sunset", "ready-lte", null, new PlanningFilter("Xiaomi", null, null), ct));
        Assert.Null(await Reader().CountNumbersAsync(1, "esim", "not-capable", null, acme, ct));
    }

    private static OperatorBandsOptions Bands(int[] lte, int[] nr)
    {
        var options = new OperatorBandsOptions();
        foreach (var b in lte)
        {
            options.Lte.Add(b);
        }

        foreach (var b in nr)
        {
            options.Nr.Add(b);
        }

        return options;
    }

    private static string MigrationText(string name) => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Migrations", "analytics", name));

    private ClickHousePlanningReader Reader() => new(
        Options.Create(new ClickHouseOptions { ConnectionString = ConnectionString }), new Factory());

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
