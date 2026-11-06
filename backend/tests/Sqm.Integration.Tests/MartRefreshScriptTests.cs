using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Application.Sql;
using Sqm.Contracts.Dashboard;
using Sqm.Infrastructure.ClickHouse;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Integration.Tests;

/// <summary>
/// The whole mart refresh, as it ships, in a scratch database, over two deliveries. Every mart is merged
/// from the per-model states, so each is compared here with a direct read of current state: the
/// statements the refresh used to run, kept as the definition of what the marts mean.
/// </summary>
/// <remarks>
/// Two deliveries on purpose. The first version of the merged KPI row summed every delivery in the
/// table: <c>{seq} AS seq</c> made <c>WHERE seq = {seq}</c> true for all of them, which one delivery
/// cannot show. A real run on the laptop caught it at 234,517,699 active bindings against 116,124,016.
/// </remarks>
[Collection("clickhouse-scratch")]
public sealed class MartRefreshScriptTests : IAsyncLifetime
{
    private const string Endpoint = "http://localhost:18123/";
    private const string User = "sqm_ingest";
    private const string Password = "sqm_dev";

    private static readonly string[] Tables =
    [
        "binding_current", "tac_all", "tac_active", "tac_radio", "tac_radio_staging", "tac_radio_parsed", "tac_vendor_map", "tac_capability",
        "agg_tac_population", "agg_device_model", "agg_filter_population", "agg_kpi_daily",
        "agg_device_class_daily", "agg_capability_daily", "agg_dimension_daily",
    ];

    private static readonly string[] Views = ["tac", "tac_radio_active"];

    /// <summary>
    /// Handsets of six catalogued models, TACs the catalogue lacks, rows with no handset, a malformed
    /// IMEI, the three kinds of IMEI no handset could carry, subscribers on two models, of one class and
    /// of two, and a binding already ended.
    /// </summary>
    private const string FirstState = """
        (9120000001, 432110000000001, '35004012000001', 1, 1, '2026-05-01'),
        (9120000002, 432110000000002, '35004012000002', 1, 1, '2026-05-01'),
        (9120000002, 432110000000002, '01453000000001', 1, 1, '2026-05-01'),
        (9120000003, 432110000000003, '01453000000002', 1, 1, '2026-05-01'),
        (9120000004, 432110000000004, '00100100000001', 1, 1, '2026-05-01'),
        (9120000005, 432110000000005, '35405595000001', 1, 1, '2026-05-01'),
        (9120000005, 432110000000005, '35004012000005', 1, 1, '2026-05-01'),
        (9120000006, 432110000000006, '01010900000001', 1, 1, '2026-05-01'),
        (9120000007, 432110000000007, '000000', 1, 1, '2026-05-01'),
        (9120000001, 432110000000001, '000000', 1, 1, '2026-05-01'),
        (9120000008, 432110000000008, '000100', 1, 1, '2026-05-01'),
        (9120000009, 432110000000009, '99999999000001', 1, 1, '2026-05-01'),
        (9120000010, 432110000000010, '35004012000003', 0, 1, '2026-05-01'),
        (9120000014, 432110000000014, '00000000000000', 1, 1, '2026-05-01'),
        (9120000015, 432110000000015, '00000000123456', 1, 1, '2026-05-01'),
        (9120000016, 432110000000016, '50040120000010', 1, 1, '2026-05-01'),
        (9120000017, 432110000000017, '50040120000011', 1, 1, '2026-05-01'),
        (9120000018, 432110000000018, '77123456000001', 1, 1, '2026-05-01'),
        (9120000019, 432110000000019, '77000001000001', 1, 1, '2026-05-01'),
        (9120000020, 432110000000020, '35999999000001', 1, 1, '2026-05-01')
        """;

    /// <summary>The next delivery: one binding ends, three arrive, one of them with no handset.</summary>
    private const string SecondDay = """
        (9120000003, 432110000000003, '01453000000002', 0, 2, '2026-05-02'),
        (9120000011, 432110000000011, '35004012000004', 1, 2, '2026-05-02'),
        (9120000012, 432110000000012, '35405595000002', 1, 2, '2026-05-02'),
        (9120000013, 432110000000013, '000000', 1, 2, '2026-05-02')
        """;

    private readonly string _database = "itest_marts_" + Guid.NewGuid().ToString("N")[..12];
    private readonly HttpClient _http = new(new SocketsHttpHandler { PooledConnectionIdleTimeout = TimeSpan.FromSeconds(5) })
    {
        Timeout = TimeSpan.FromMinutes(2),
    };
    private string? _unavailable;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
            foreach (var table in Tables.Concat(Views))
            {
                var create = await QueryAsync(
                    $"SELECT create_table_query FROM system.tables WHERE database = 'sqm' AND name = '{table}' FORMAT TSVRaw");
                await QueryAsync(create.Replace("sqm.", _database + ".", StringComparison.Ordinal));
            }

            await QueryAsync($"""
                INSERT INTO {_database}.tac_all (version_id, tac, manufacturer, marketingName, deviceType, operatingSystem, removableEUICC, nonremovableEUICC, ntnConnectivity, allocationDate, authenticatedIMSEmergencyCallSupport, bandDetails) VALUES
                    (1, '35004012', 'Samsung Korea', 'Galaxy A54', 'Smartphone', ' Android ', '0', '0', 'Not Known', '01-Jan-2023', 'Y',
                     '5G NR: n1, n3, n78 | 4G: LTE FDD BAND 1, LTE FDD BAND 3 | 2G/3G: GSM 900'),
                    (1, '01453000', 'Nokia Oy', 'Nokia 105', 'Mobile Phone/Feature phone', '', '00', '00', 'Not Known', '05-Jun-2021', 'N',
                     '4G: LTE FDD BAND 2 | 2G/3G: GSM 900'),
                    (1, '00100100', 'Old Maker', '', 'Handheld', 'Proprietary', '00', '00', 'Not Known', '03-Nov-1999', 'Not Known',
                     '2G/3G: GSM 900,GSM 1800 | Radio Interface: NONE'),
                    (1, '35405595', 'Samsung Vietnam', 'Galaxy S25', 'Smartphone', 'Android', '0', '1', '3GPP NTN & TN(GSO)', '14-Feb-2025', 'Y',
                     '5G NR: n1, n78 | 4G: LTE FDD BAND 1 | 2G/3G: GSM 900'),
                    (1, '01010900', '', '', '', '', '00', '00', 'Not Known', '20-Apr-2000', 'Not Known', 'Radio Interface: NONE'),
                    (1, '77000001', 'Odd Maker', 'Odd One', 'Smartphone', 'Android', '00', '00', 'Not Known', '01-Jan-2024', 'Not Known', '4G: LTE FDD BAND 3')
                """);
            await QueryAsync($"INSERT INTO {_database}.tac_active (singleton, version_id, activated_at) VALUES (1, 1, now64(3))");
            await QueryAsync($"""
                INSERT INTO {_database}.tac_vendor_map (raw_manufacturer, vendor_canonical, vendor_group) VALUES
                    ('Samsung Korea', 'Samsung', 'Samsung'), ('Samsung Vietnam', 'Samsung', 'Samsung')
                """);

            // Without a profile every model reads as unregistered, and each comparison below holds
            // without proving anything: the parse answers null, not an error, if a table is missing.
            _ = await Store().ParseRadioAsync(1, Ct)
                ?? throw new InvalidOperationException("The radio profiles were not parsed: a table is missing from the copy.");
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

    private async Task RefreshAsync(int seq)
    {
        foreach (var statement in SqlScript.Split(Script()))
        {
            await QueryAsync(statement.Replace("sqm.", _database + ".", StringComparison.Ordinal), seq);
        }
    }

    private async Task TwoDeliveriesAsync()
    {
        await QueryAsync($"INSERT INTO {_database}.binding_current (msisdn, imsi, imei, active, last_change_seq, last_change_date) VALUES {FirstState}");
        await RefreshAsync(1);
        await QueryAsync($"INSERT INTO {_database}.binding_current (msisdn, imsi, imei, active, last_change_seq, last_change_date) VALUES {SecondDay}");
        await RefreshAsync(2);
    }

    private static string Script() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "jobs", "refresh_marts.sql"));

    private string Sql(string sql) => sql.Replace("{db}", _database, StringComparison.Ordinal);

    private async Task<string[]> RowsAsync(string sql) =>
        (await QueryAsync(Sql(sql) + " FORMAT TSVRaw"))
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The rule as what reads the rows themselves has it, in this database.</summary>
    private string NoHandset => AnalyticsSchema.NoHandset("b.imei", "b.tac", $"{_database}.tac");

    private const string Kpi = """
        SELECT active_bindings, distinct_subscribers, distinct_sims, distinct_devices,
               unknown_device_bindings, malformed_imei_bindings, invalid_imei_bindings, tac_matched_bindings
        FROM {db}.agg_kpi_daily WHERE seq = 2
        """;

    private string KpiTruth => $"""
        SELECT count(), uniq(b.msisdn), uniq(b.imsi), uniqIf(b.imei, NOT {NoHandset}),
               countIf(b.imei = '000000'), countIf(length(b.imei) != 14 AND b.imei != '000000'),
               countIf(length(b.imei) = 14 AND {NoHandset}),
               countIf(b.tac != '' AND b.tac IN (SELECT tac FROM {_database}.tac))
        FROM {_database}.binding_current AS b FINAL WHERE b.active = 1
        """;

    [Fact]
    public async Task The_kpi_row_holds_its_own_delivery_and_no_other()
    {
        Assert.SkipWhen(_unavailable is not null, _unavailable ?? "");

        await QueryAsync($"INSERT INTO {_database}.binding_current (msisdn, imsi, imei, active, last_change_seq, last_change_date) VALUES {FirstState}");
        await RefreshAsync(1);
        var first = await RowsAsync(KpiTruth);

        await QueryAsync($"INSERT INTO {_database}.binding_current (msisdn, imsi, imei, active, last_change_seq, last_change_date) VALUES {SecondDay}");
        await RefreshAsync(2);

        Assert.Equal(await RowsAsync(KpiTruth), await RowsAsync(Kpi));
        Assert.Equal(first, await RowsAsync(Kpi.Replace("seq = 2", "seq = 1", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("vendor", "coalesce(coalesce(nullIf(v.vendor_canonical, ''), nullIf(t.manufacturer, '')), '(unknown TAC)')")]
    [InlineData("manufacturer", "coalesce(nullIf(t.manufacturer, ''), '(unknown TAC)')")]
    [InlineData("model", "coalesce(nullIf(t.marketingName, ''), '(unknown TAC)')")]
    [InlineData("deviceType", "coalesce(nullIf(t.deviceType, ''), '(unknown TAC)')")]
    [InlineData("os", "coalesce(nullIf(trim(t.operatingSystem), ''), '(unknown TAC)')")]
    public async Task A_dimension_agrees_with_a_direct_read_of_current_state(string dimension, string value)
    {
        Assert.SkipWhen(_unavailable is not null, _unavailable ?? "");
        await TwoDeliveriesAsync();

        var truth = await RowsAsync($"""
            SELECT multiIf({NoHandset}, '(unknown device)', t.tac = '', '(unknown TAC)', {value}) AS dim_value,
                   count(), uniq(b.msisdn), uniqIf(b.imei, NOT {NoHandset})
            FROM {_database}.binding_current AS b FINAL
            LEFT JOIN {_database}.tac AS t ON t.tac = b.tac
            LEFT JOIN (SELECT * FROM {_database}.tac_vendor_map FINAL) AS v ON v.raw_manufacturer = t.manufacturer
            WHERE b.active = 1
            GROUP BY dim_value ORDER BY dim_value
            """);

        var mart = await RowsAsync($"""
            SELECT dim_value, bindings, subscribers, handsets FROM {_database}.agg_dimension_daily
            WHERE seq = 2 AND dimension = '{dimension}' ORDER BY dim_value
            """);

        Assert.NotEmpty(truth);
        Assert.Equal(truth, mart);
    }

    [Fact]
    public async Task The_tac_slice_agrees_with_a_direct_read_of_current_state()
    {
        Assert.SkipWhen(_unavailable is not null, _unavailable ?? "");
        await TwoDeliveriesAsync();

        var truth = await RowsAsync($"""
            SELECT if({NoHandset}, '(unknown device)', b.tac) AS dim_value, count(), uniq(b.msisdn), uniqIf(b.imei, NOT {NoHandset})
            FROM {_database}.binding_current AS b FINAL
            WHERE b.active = 1
            GROUP BY dim_value ORDER BY dim_value
            """);

        var mart = await RowsAsync("""
            SELECT dim_value, bindings, subscribers, handsets FROM {db}.agg_dimension_daily
            WHERE seq = 2 AND dimension = 'tac' ORDER BY dim_value
            """);

        Assert.Equal(truth, mart);
    }

    [Fact]
    public async Task The_class_mix_agrees_with_a_direct_read_of_current_state()
    {
        Assert.SkipWhen(_unavailable is not null, _unavailable ?? "");
        await TwoDeliveriesAsync();

        var truth = await RowsAsync($"""
            SELECT multiIf({NoHandset}, 'Unknown device', r.tac = '', 'Unregistered TAC', r.device_class) AS device_class,
                   count(), uniq(b.msisdn), uniqIf(b.imei, NOT {NoHandset})
            FROM {_database}.binding_current AS b FINAL
            LEFT JOIN (SELECT tac, device_class FROM {_database}.tac_radio_active) AS r ON r.tac = b.tac
            WHERE b.active = 1
            GROUP BY device_class ORDER BY device_class
            """);

        var mart = await RowsAsync("""
            SELECT device_class, sumIf(n, measure = 'bindings'), sumIf(n, measure = 'subscribers'), sumIf(n, measure = 'handsets')
            FROM {db}.agg_device_class_daily WHERE seq = 2
            GROUP BY device_class ORDER BY device_class
            """);

        Assert.Equal(truth, mart);
    }

    [Theory]
    [InlineData("bindings", "countIf({0})")]
    [InlineData("subscribers", "uniqIf(b.msisdn, {0})")]
    [InlineData("handsets", "uniqIf(b.imei, length(b.imei) = 14 AND ({0}))")]
    public async Task Capability_agrees_with_a_direct_read_and_counts_no_row_without_a_handset(string measure, string aggregate)
    {
        Assert.SkipWhen(_unavailable is not null, _unavailable ?? "");
        await TwoDeliveriesAsync();

        foreach (var (capability, column) in new[]
                 {
                     ("LTE", "has_lte"), ("5G", "has_5g"), ("eSIM", "has_esim"), ("IMS emergency calling", "ims_emergency"),
                 })
        {
            var supported = string.Format(System.Globalization.CultureInfo.InvariantCulture, aggregate, $"c.tac != '' AND c.{column} = 1");
            var unsupported = string.Format(System.Globalization.CultureInfo.InvariantCulture, aggregate, $"c.tac != '' AND c.{column} = 0");
            var unknown = string.Format(System.Globalization.CultureInfo.InvariantCulture, aggregate, $"NOT {NoHandset} AND (c.tac = '' OR c.{column} = 2)");

            var truth = await RowsAsync($"""
                SELECT {supported}, {unsupported}, {unknown}
                FROM {_database}.binding_current AS b FINAL
                LEFT JOIN {_database}.tac_capability AS c ON c.tac = b.tac
                WHERE b.active = 1
                """);

            var mart = await RowsAsync($"""
                SELECT supported, unsupported, unknown FROM {_database}.agg_capability_daily
                WHERE seq = 2 AND measure = '{measure}' AND capability = '{capability}'
                """);

            Assert.Equal(truth, mart);
        }
    }

    [Fact]
    public async Task A_row_no_handset_could_carry_joins_the_empty_tac_and_a_tac_merely_unknown_does_not()
    {
        Assert.SkipWhen(_unavailable is not null, _unavailable ?? "");
        await TwoDeliveriesAsync();

        // tac | bindings | invalid | handsets
        var rows = await RowsAsync("""
            SELECT if(tac = '', 'no TAC', tac), bindings, invalid_imei, finalizeAggregation(handsets) FROM {db}.agg_tac_population
            WHERE seq = 2 AND tac IN ('', '00000000', '50040120', '77123456', '77000001', '35999999', '99999999')
            ORDER BY tac = '' DESC, tac
            """);

        Assert.Equal(
            [
                // 000000 three times, a malformed one, both zero TACs, the shifted IMEI, the unknown body.
                "no TAC\t8\t4\t0",
                "35999999\t1\t0\t1",  // a good body the catalogue lacks: a handset
                "50040120\t1\t0\t1",  // that shape, but not ending in 0: not shifted
                "77000001\t1\t0\t1",  // a body Annex A lacks, but a TAC the catalogue holds
                "99999999\t1\t0\t1",
            ],
            rows);
    }

    [Fact]
    public async Task A_read_of_the_rows_leaves_out_what_the_marts_leave_out()
    {
        Assert.SkipWhen(_unavailable is not null, _unavailable ?? "");
        await TwoDeliveriesAsync();

        var filter = FilterBuilder.ForCurrentState(new DashboardFilter()).WhereClause
            .Replace("sqm.", _database + ".", StringComparison.Ordinal);

        var read = await RowsAsync($"SELECT count() FROM {_database}.binding_current AS b FINAL WHERE b.active = 1 AND {filter}");
        var mart = await RowsAsync("SELECT bindings FROM {db}.agg_filter_population WHERE seq = 2 AND dimension = 'knownDevice'");

        Assert.Equal(mart, read);
    }

    [Fact]
    public async Task Running_it_again_changes_nothing()
    {
        Assert.SkipWhen(_unavailable is not null, _unavailable ?? "");
        await TwoDeliveriesAsync();

        const string All = """
            SELECT * FROM (
                SELECT 'kpi' AS mart, active_bindings AS a, distinct_subscribers AS b, 0 AS c, 0 AS d FROM {db}.agg_kpi_daily WHERE seq = 2
                UNION ALL SELECT dimension, sum(bindings), sum(subscribers), sum(handsets), count() FROM {db}.agg_dimension_daily WHERE seq = 2 GROUP BY dimension
                UNION ALL SELECT measure, sum(n), count(), 0, 0 FROM {db}.agg_device_class_daily WHERE seq = 2 GROUP BY measure
                UNION ALL SELECT 'filter', sum(bindings), count(), 0, 0 FROM {db}.agg_filter_population WHERE seq = 2
            )
            ORDER BY mart
            """;
        var once = await RowsAsync(All);

        await RefreshAsync(2);

        Assert.Equal(once, await RowsAsync(All));
    }

    [Fact]
    public void The_script_reads_current_state_once()
    {
        var code = Script().ReplaceLineEndings().Split(Environment.NewLine)
            .Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal));

        Assert.Single(code, line => line.Contains("sqm.binding_current", StringComparison.Ordinal));
    }

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
        using var response = await _http.PostAsync(url, new StringContent(sql, Encoding.UTF8, "text/plain"), Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

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
