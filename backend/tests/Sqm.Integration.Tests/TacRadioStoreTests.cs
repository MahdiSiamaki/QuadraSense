using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Application.Sql;
using Sqm.Domain.Devices;
using Sqm.Infrastructure.ClickHouse;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Integration.Tests;

/// <summary>
/// A TAC version's radio profiles are parsed into the store whole, and the dashboard's capability
/// mart, built from them, counts what the catalogue does not state as unknown - never as "no".
/// </summary>
/// <remarks>
/// Against the real ClickHouse, through the real store and the real refresh statements, in a scratch
/// database that migration 029 is applied to as it ships. The records are real GSMA shapes: Galaxy A54
/// (35004012), a feature phone listing a 5G architecture option and no NR band (01453000), a GSM-only
/// handheld whose eUICC fields read "00" (00100100), an NTN phone with an embedded eUICC (35405595), and
/// a record that lists no band at all (01010900).
/// </remarks>
[Collection("clickhouse-scratch")]
public sealed class TacRadioStoreTests : IAsyncLifetime
{
    private const string Endpoint = "http://localhost:18123/";
    private const string User = "sqm_ingest";
    private const string Password = "sqm_dev";

    private readonly string _database = "itest_tacradio_" + Guid.NewGuid().ToString("N")[..12];
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
            if (await QueryAsync("EXISTS TABLE sqm.tac_all") != "1")
            {
                _unavailable = "ClickHouse is up but has no sqm schema to copy";
                return;
            }

            await QueryAsync($"CREATE DATABASE {_database}");
            foreach (var table in new[] { "tac_all", "tac_active", "binding_current", "agg_capability_daily" })
            {
                await QueryAsync($"CREATE TABLE {_database}.{table} AS sqm.{table}");
            }

            foreach (var migration in new[] { "011_tac_capability.sql", "029_tac_radio.sql" })
            {
                foreach (var statement in SqlScript.Split(MigrationText("analytics", migration).Replace("sqm.", _database + ".", StringComparison.Ordinal)))
                {
                    await QueryAsync(statement);
                }
            }

            await QueryAsync($"""
                INSERT INTO {_database}.tac_all (version_id, tac, deviceType, removableEUICC, nonremovableEUICC, ntnConnectivity, allocationDate, authenticatedIMSEmergencyCallSupport, bandDetails) VALUES
                    (1, '35004012', 'Smartphone', '0', '0', 'Not Known', '01-Jan-2023', 'Not Known',
                     '5G NR: n1, n3, n78 | 5G DC BC EN-DC FR1 2B: DC_1A_n3A | 5G NA Options: 5G-5Series | 4G: LTE FDD BAND 1, LTE FDD BAND 3, LTE TDD BAND 40 | 4G Cat: LTE 12 | 2G/3G: GSM 900,GSM 1800,WCDMA FDD Band 1 | Radio Interface: NONE'),
                    (1, '01453000', 'Mobile Phone/Feature phone', '00', '00', 'Not Known', '05-Jun-2021', 'Not Known',
                     '5G NA Options: 5G-5Series | 4G: LTE FDD BAND 2, LTE FDD BAND 4 | 4G Cat: LTE 4 | 2G/3G: GSM 900,WCDMA FDD Band 1 | Radio Interface: NONE'),
                    (1, '00100100', 'Handheld', '00', '00', 'Not Known', '03-Nov-1999', 'Not Known',
                     '2G/3G: GSM 900,GSM 1800 | Radio Interface: NONE'),
                    (1, '35405595', 'Smartphone', '0', '1', '3GPP NTN & TN(GSO)', '14-Feb-2025', 'Y',
                     '5G NR: n1, n78 | 5G NA Options: 5G-2Series,5G-3Series | 4G: LTE FDD BAND 1, LTE FDD BAND 3 | 2G/3G: GSM 900 | Radio Interface: NONE | NTN: E-UTRA B256'),
                    (1, '01010900', 'Handheld', '00', '00', 'Not Known', '20-Apr-2000', 'Not Known',
                     'Radio Interface: NONE')
                """);
            await QueryAsync($"INSERT INTO {_database}.tac_active (singleton, version_id, activated_at) VALUES (1, 1, now64(3))");

            // Active bindings: 2 on the A54, 3 on the feature phone, 1 each on the GSM handheld, the
            // NTN phone and the record with no bands, 1 unknown device and 1 TAC the catalogue lacks.
            await QueryAsync($"""
                INSERT INTO {_database}.binding_current (msisdn, imsi, imei, active, last_change_seq, last_change_date) VALUES
                    (9120000001, 432110000000001, '35004012000001', 1, 1, '2026-05-01'),
                    (9120000002, 432110000000002, '35004012000002', 1, 1, '2026-05-01'),
                    (9120000003, 432110000000003, '01453000000001', 1, 1, '2026-05-01'),
                    (9120000004, 432110000000004, '01453000000002', 1, 1, '2026-05-01'),
                    (9120000005, 432110000000005, '01453000000003', 1, 1, '2026-05-01'),
                    (9120000006, 432110000000006, '00100100000001', 1, 1, '2026-05-01'),
                    (9120000007, 432110000000007, '35405595000001', 1, 1, '2026-05-01'),
                    (9120000008, 432110000000008, '01010900000001', 1, 1, '2026-05-01'),
                    (9120000009, 432110000000009, '000000', 1, 1, '2026-05-01'),
                    (9120000010, 432110000000010, '99999999000001', 1, 1, '2026-05-01'),
                    (9120000011, 432110000000011, '35004012000003', 0, 2, '2026-05-02')
                """);
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
    public async Task A_version_is_parsed_whole_and_reads_back_as_the_parser_said()
    {
        Assert.SkipWhen(_unavailable is not null, _unavailable ?? "");
        var ct = TestContext.Current.CancellationToken;

        var parse = await Store().ParseRadioAsync(1, ct);

        Assert.NotNull(parse);
        Assert.Equal(5, parse.Rows);
        Assert.Equal(0, parse.UnparsedTacs);
        Assert.Equal(RadioProfileParser.Version, (await Store().GetRadioParseAsync(1, ct))?.ParserVersion);

        // tac | lte | five_g | esim | sunset | sa | nsa | ntn | ntn mode | LTE bands | device class
        var rows = await QueryAsync($"""
            SELECT concatWithSeparator('|', tac, toString(lte), toString(five_g), toString(esim), toString(sunset),
                   toString(sa), toString(nsa), toString(ntn), toString(ntn_mode), arrayStringConcat(lte_bands, ','),
                   toString(device_class))
            FROM {_database}.tac_radio_active ORDER BY tac FORMAT TSVRaw
            """);

        Assert.Equal(
            [
                "00100100|0|0|2|3|2|2|2|||Other",
                "01010900|2|2|2|0|2|2|2|||Other",
                "01453000|1|0|2|1|2|2|2||2,4|Feature phone",
                "35004012|1|1|0|1|2|1|2||1,3,40|Smartphone",
                "35405595|1|1|1|1|1|1|1|NTN and TN|1,3|Smartphone",
            ],
            rows.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Parsing_again_replaces_the_version_and_dropping_it_removes_the_profiles()
    {
        Assert.SkipWhen(_unavailable is not null, _unavailable ?? "");
        var ct = TestContext.Current.CancellationToken;

        await Store().ParseRadioAsync(1, ct);
        await Store().ParseRadioAsync(1, ct);

        Assert.Equal("5", await QueryAsync($"SELECT count() FROM {_database}.tac_radio WHERE version_id = 1"));
        Assert.Equal("0", await QueryAsync($"SELECT count() FROM {_database}.tac_radio_staging"));

        await Store().DropVersionAsync(1, ct);

        Assert.Equal("0", await QueryAsync($"SELECT count() FROM {_database}.tac_radio WHERE version_id = 1"));
    }

    [Fact]
    public async Task The_capability_mart_counts_what_the_catalogue_does_not_state_as_unknown()
    {
        Assert.SkipWhen(_unavailable is not null, _unavailable ?? "");
        var ct = TestContext.Current.CancellationToken;

        await Store().ParseRadioAsync(1, ct);

        // The refresh's own statements for the capability flags and the bindings measure, as they ship.
        var statements = SqlScript.Split(MigrationText("jobs", "refresh_marts.sql"))
            .Where(s => SqlScript.Label(s, 0) switch
            {
                var l when l.StartsWith("TRUNCATE TABLE sqm.tac_capability", StringComparison.Ordinal) => true,
                var l when l.StartsWith("INSERT INTO sqm.tac_capability", StringComparison.Ordinal) => true,
                var l when l.StartsWith("ALTER TABLE sqm.agg_capability_daily DROP PARTITION", StringComparison.Ordinal) => true,
                var l when l.StartsWith("INSERT INTO sqm.agg_capability_daily", StringComparison.Ordinal) =>
                    s.Contains("'bindings'", StringComparison.Ordinal),
                _ => false,
            })
            .ToList();
        Assert.Equal(4, statements.Count);

        foreach (var statement in statements)
        {
            await QueryAsync(statement.Replace("sqm.", _database + ".", StringComparison.Ordinal), seq: 7);
        }

        var figures = await QueryAsync($"""
            SELECT concatWithSeparator('|', capability, toString(supported), toString(unsupported), toString(unknown))
            FROM {_database}.agg_capability_daily
            WHERE seq = 7 AND measure = 'bindings' AND capability != 'IMS emergency calling' ORDER BY capability FORMAT TSVRaw
            """);

        // 5G: the A54's two and the NTN phone's one. The feature phone's option is not a band.
        // eSIM: the "00" records are not stated, so the A54's two are the only "no".
        // Unknown always holds the unknown device and the TAC the catalogue lacks.
        Assert.Equal(
            ["5G|3|4|3", "LTE|6|1|3", "eSIM|1|2|7"],
            figures.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    }

    private static string MigrationText(string folder, string name) => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Migrations", folder, name));

    private ClickHouseTacVersionStore Store() => new(
        Options.Create(new ClickHouseOptions
        {
            ConnectionString = $"Host=localhost;Port=18123;Database={_database};Username={User};Password={Password}",
        }),
        new Factory(),
        NullLogger<ClickHouseTacVersionStore>.Instance);

    private async Task<string> QueryAsync(string sql, int? seq = null)
    {
        var url = seq is { } s ? $"{Endpoint}?param_seq={s}" : Endpoint;
        using var response = await _http.PostAsync(
            url, new StringContent(sql, Encoding.UTF8, "text/plain"), TestContext.Current.CancellationToken);
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
