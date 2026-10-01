using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Sqm.Application.Explorer;
using Sqm.Contracts.Explorer;
using Sqm.Infrastructure.ClickHouse;
using Sqm.Infrastructure.ClickHouse.Explorer;

namespace Sqm.Integration.Tests;

/// <summary>
/// The Explorer engine returns exactly the rows a query describes, reads the copy it should, and
/// refuses what is over its budget.
/// </summary>
/// <remarks>
/// Against the real ClickHouse, through the real engine, in a scratch database whose tables are
/// copied from the live schema and filled with a few bindings built to tell the cases apart.
/// Connected as the API's user, so the read-only path is the one under test.
/// </remarks>
[Collection("clickhouse-scratch")]
public sealed class ExplorerEngineTests : IAsyncLifetime
{
    private const string Endpoint = "http://localhost:18123/";

    private readonly string _database = "itest_explorer_" + Guid.NewGuid().ToString("N")[..12];
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private string? _unavailable;

    // Three models GSMA knows, and 99999999, which it does not.
    private const string GalaxyA01 = "35480111";
    private const string Redmi = "86453906";
    private const string Tracker = "01518100";

    public async ValueTask InitializeAsync()
    {
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("sqm_ingest:sqm_dev")));

        try
        {
            if (await QueryAsync("EXISTS TABLE sqm.binding_by_imei") != "1")
            {
                _unavailable = "ClickHouse is up but has no sqm schema to copy";
                return;
            }

            await QueryAsync($"CREATE DATABASE {_database}");
            foreach (var table in new[] { "binding_current", "binding_by_imsi", "binding_by_imei", "binding_event" })
            {
                await QueryAsync($"CREATE TABLE {_database}.{table} AS sqm.{table}");
            }

            await QueryAsync($"""
                CREATE TABLE {_database}.tac
                    (tac String, brandName String, marketingName String, manufacturer String, deviceType String, operatingSystem String)
                ENGINE = MergeTree ORDER BY tac
                """);
            await QueryAsync($"""
                INSERT INTO {_database}.tac VALUES
                    ('{GalaxyA01}', 'Samsung', 'Galaxy A01', 'Samsung Korea', 'Smartphone', 'Android'),
                    ('{Redmi}', 'Redmi', 'Redmi Note 12S', 'Xiaomi Communications Co Ltd', 'Smartphone', 'Android'),
                    ('{Tracker}', 'Queclink', 'GL300MA', 'Queclink Wireless Solutions', 'Dongle', '')
                """);

            // Current state, the same rows in all three sorted copies, as the materialized views keep them.
            const string Bindings = $"""
                (9120000001, 432110000000001, '{GalaxyA01}000001', 1, 1, '2026-09-20'),
                (9120000001, 432110000000001, '{Redmi}000001',     0, 1, '2026-09-10'),
                (9120000001, 432110000000002, '{Redmi}000001',     1, 1, '2026-09-21'),
                (9120000002, 432110000000003, '{GalaxyA01}000001', 1, 1, '2026-09-22'),
                (9120000003, 432110000000004, '{GalaxyA01}000001', 1, 1, '2026-09-23'),
                (9120000004, 432110000000005, '99999999000010',    1, 1, '2026-09-24'),
                (9120000005, 432110000000006, '{Tracker}000001',   1, 0, NULL),
                (9130000001, 432110000000007, '{Redmi}000002',     1, 1, '2026-09-25')
                """;
            await QueryAsync($"INSERT INTO {_database}.binding_current (msisdn, imsi, imei, active, last_change_seq, last_change_date) VALUES {Bindings}");
            await QueryAsync($"INSERT INTO {_database}.binding_by_imsi (msisdn, imsi, imei, active, last_change_seq, last_change_date) VALUES {Bindings}");
            await QueryAsync($"INSERT INTO {_database}.binding_by_imei (msisdn, imsi, imei, active, last_change_seq, last_change_date) VALUES {Bindings}");

            // Events: one SIM on four handsets on 20 September, and a day outside every range below.
            await QueryAsync($"""
                INSERT INTO {_database}.binding_event (seq, data_date, msisdn, imsi, imei, label) VALUES
                    (1, '2026-09-20', 9120000009, 432110000000009, '{GalaxyA01}000011', 'add'),
                    (1, '2026-09-20', 9120000009, 432110000000009, '{GalaxyA01}000012', 'add'),
                    (1, '2026-09-20', 9120000009, 432110000000009, '{GalaxyA01}000013', 'add'),
                    (1, '2026-09-20', 9120000009, 432110000000009, '{GalaxyA01}000014', 'add'),
                    (1, '2026-09-20', 9120000001, 432110000000001, '{GalaxyA01}000001', 'add'),
                    (2, '2026-09-21', 9120000009, 432110000000009, '{GalaxyA01}000011', 'remove'),
                    (3, '2026-08-01', 9120000009, 432110000000009, '{GalaxyA01}000015', 'add')
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

    private async Task<string> QueryAsync(string sql)
    {
        using var response = await _http.PostAsync(Endpoint, new StringContent(sql, Encoding.UTF8, "text/plain"),
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return response.IsSuccessStatusCode ? body.Trim() : throw new InvalidOperationException($"ClickHouse refused: {body}");
    }

    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        });
    }

    private ClickHouseExplorerEngine Engine(ExplorerOptions? options = null) => new(
        Microsoft.Extensions.Options.Options.Create(new ClickHouseOptions
        {
            ConnectionString = $"Host=localhost;Port=18123;Database={_database};Username=sqm_app;Password=sqm_dev",
        }),
        Microsoft.Extensions.Options.Options.Create(options ?? new ExplorerOptions()),
        new Factory(),
        NullLogger<ClickHouseExplorerEngine>.Instance);

    private static ExplorerFilter Is(string field, ExplorerOperator op, params string[] values) => new(Field: field, Operator: op, Values: values);

    private static ExplorerFilter All(params ExplorerFilter[] children) => new(ExplorerLogic.And, children);

    private static ExplorerFilter Any(params ExplorerFilter[] children) => new(ExplorerLogic.Or, children);

    private static CheckedExplorerQuery Checked(ExplorerQueryRequest request, ExplorerOptions? options = null)
    {
        var (query, problems) = ExplorerValidator.Check(request, options ?? new ExplorerOptions());
        Assert.True(query is not null, string.Join("; ", problems.Select(p => $"{p.Path}: {p.Message}")));
        return query!;
    }

    private async Task<ExplorerRows> RunAsync(ExplorerQueryRequest request, ExplorerOptions? options = null)
    {
        using var engine = Engine(options);
        return await engine.RunAsync(Checked(request, options), TestContext.Current.CancellationToken);
    }

    /// <summary>The column's values, in row order, as the API returns them.</summary>
    private static List<object?> Column(ExplorerRows rows, CheckedExplorerQuery query, string name)
    {
        var index = query.Columns.Select(c => c.Name).ToList().IndexOf(name);
        return [.. rows.Rows.Select(r => r[index])];
    }

    [Fact]
    public async Task Each_identifier_reads_its_own_copy_and_returns_exactly_its_bindings()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var byNumber = new ExplorerQueryRequest(ExplorerDataset.Bindings, Is("msisdn", ExplorerOperator.Equals, "0912 000 0001"));
        var byHandset = new ExplorerQueryRequest(ExplorerDataset.Bindings, Is("imei", ExplorerOperator.Equals, GalaxyA01 + "000001"));

        var number = await RunAsync(byNumber);
        var handset = await RunAsync(byHandset);

        Assert.Equal(("Current state, read by number", "KeyRead"), (number.Plan.Source, number.Plan.Access));
        Assert.Equal(3, number.Total);
        Assert.Equal(["432110000000001", "432110000000001", "432110000000002"], Column(number, Checked(byNumber), "imsi"));
        Assert.Equal([true, false, true], Column(number, Checked(byNumber), "active"));
        Assert.Equal(["Galaxy A01", "Redmi Note 12S", "Redmi Note 12S"], Column(number, Checked(byNumber), "model"));

        Assert.Equal(("Current state, read by handset", "KeyRead"), (handset.Plan.Source, handset.Plan.Access));
        Assert.Equal(["9120000001", "9120000002", "9120000003"], Column(handset, Checked(byHandset), "msisdn"));
    }

    [Fact]
    public async Task Nested_and_or_not_select_exactly_what_they_say()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        // Numbers starting 912, and: (a Samsung still active) or a dongle - but not number 9120000002.
        var request = new ExplorerQueryRequest(ExplorerDataset.Bindings, All(
            Is("msisdn", ExplorerOperator.StartsWith, "912"),
            Any(
                All(Is("brand", ExplorerOperator.Equals, "Samsung"), Is("active", ExplorerOperator.Equals, "true")),
                Is("deviceType", ExplorerOperator.Equals, "Dongle")),
            new ExplorerFilter(Field: "msisdn", Operator: ExplorerOperator.Equals, Values: ["9120000002"], Not: true)),
            Columns: ["msisdn", "imei"]);

        var rows = await RunAsync(request);

        Assert.Equal("RangeRead", rows.Plan.Access);
        Assert.Equal(
            [["9120000001", GalaxyA01 + "000001"], ["9120000003", GalaxyA01 + "000001"], ["9120000005", Tracker + "000001"]],
            rows.Rows.Select(r => r.ToArray()));
    }

    /// <summary>"Model is not Galaxy A01" includes handsets whose model nobody knows.</summary>
    [Fact]
    public async Task Not_equal_on_a_model_keeps_the_handsets_GSMA_does_not_know()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var request = new ExplorerQueryRequest(ExplorerDataset.Bindings, All(
            Is("msisdn", ExplorerOperator.StartsWith, "912"),
            Is("model", ExplorerOperator.NotEquals, "Galaxy A01")), Columns: ["imei", "model"]);

        var rows = await RunAsync(request);

        Assert.Contains(rows.Rows, r => (string?)r[0] == "99999999000010" && r[1] is null);
        Assert.DoesNotContain(rows.Rows, r => (string?)r[1] == "Galaxy A01");
    }

    [Fact]
    public async Task A_brand_is_read_as_its_TACs_and_one_nobody_makes_matches_nothing_without_a_query()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var samsung = await RunAsync(new(ExplorerDataset.Bindings, Is("brand", ExplorerOperator.Contains, "sams"), Columns: ["imei"]));
        var nobody = await RunAsync(new(ExplorerDataset.Bindings, Is("brand", ExplorerOperator.Equals, "Nokla"), Columns: ["imei"]));

        Assert.Equal(("Current state, read by handset", "RangeRead"), (samsung.Plan.Source, samsung.Plan.Access));
        Assert.Equal(3, samsung.Total);
        Assert.All(samsung.Rows, r => Assert.StartsWith(GalaxyA01, (string)r[0]!, StringComparison.Ordinal));

        Assert.Equal(0, nobody.Total);
        Assert.Equal(0, nobody.RowsRead);
        Assert.Contains("No model in the GSMA database matches", nobody.Plan.Notes[0], StringComparison.Ordinal);
    }

    /// <summary>"Which handsets have more than one active SIM?" - grouped, counted, filtered by the count.</summary>
    [Fact]
    public async Task Groups_are_counted_exactly_and_having_filters_them()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var request = new ExplorerQueryRequest(ExplorerDataset.Bindings, Is("tac", ExplorerOperator.In, GalaxyA01, Redmi),
            GroupBy: ["imei", "model"],
            Measures: [new("activeSims", ExplorerAggregate.CountDistinct, "imsi", ActiveOnly: true), new("bindings", ExplorerAggregate.Count)],
            // > 1, not > 2: Redmi 000001 has had two SIMs but only one is active, so it is the row an
            // active-only count must leave out.
            Having: Is("activeSims", ExplorerOperator.GreaterThan, "1"));

        var rows = await RunAsync(request);

        var row = Assert.Single(rows.Rows);
        Assert.Equal(new object?[] { GalaxyA01 + "000001", "Galaxy A01", 3L, 3L }, row.ToArray());
        Assert.Equal(1, rows.Total);
    }

    /// <summary>"How many SIMs has this number had?" - measures with no grouping: one row.</summary>
    [Fact]
    public async Task Measures_without_grouping_answer_in_one_row()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var rows = await RunAsync(new(ExplorerDataset.Bindings, Is("msisdn", ExplorerOperator.Equals, "9120000001"),
            Measures: [new("sims", ExplorerAggregate.CountDistinct, "imsi"), new("handsets", ExplorerAggregate.CountDistinct, "imei"),
                       new("lastChange", ExplorerAggregate.Max, "lastChangeDate")]));

        Assert.Equal(new object?[] { 2L, 2L, "2026-09-21" }, Assert.Single(rows.Rows).ToArray());
    }

    /// <summary>"Which SIMs were seen on more than three handsets in one day?"</summary>
    [Fact]
    public async Task Events_are_read_only_within_their_dates_and_group_by_day()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var request = new ExplorerQueryRequest(ExplorerDataset.Events,
            All(Is("date", ExplorerOperator.Between, "2026-09-15", "2026-09-30"), Is("change", ExplorerOperator.Equals, "add")),
            GroupBy: ["imsi", "date"],
            Measures: [new("handsets", ExplorerAggregate.CountDistinct, "imei")],
            Having: Is("handsets", ExplorerOperator.GreaterThan, "3"));

        var rows = await RunAsync(request);

        // 1 August's fifth handset is outside the range and must not make it five.
        Assert.Equal(new object?[] { "432110000000009", "2026-09-20", 4L }, Assert.Single(rows.Rows).ToArray());
        Assert.Contains(rows.Plan.Notes, n => n.StartsWith("16 day(s) of the event log", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pages_follow_on_without_repeating_or_skipping_a_row()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        ExplorerQueryRequest Page(int page) => new(ExplorerDataset.Bindings, Is("msisdn", ExplorerOperator.StartsWith, "912"),
            Columns: ["msisdn", "imsi", "imei"], Page: page, PageSize: 3);

        var pages = new List<ExplorerRows>();
        for (var p = 1; p <= 3; p++)
        {
            pages.Add(await RunAsync(Page(p)));
        }

        var seen = pages.SelectMany(p => p.Rows).Select(r => string.Join("|", r)).ToList();

        Assert.All(pages, p => Assert.Equal(7, p.Total));
        Assert.Equal(7, seen.Count);
        Assert.Equal(7, seen.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task A_query_over_the_budget_is_refused_before_it_runs()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var tight = new ExplorerOptions { BudgetRows = 1 };

        var ex = await Assert.ThrowsAsync<ExplorerRefusedException>(() =>
            RunAsync(new(ExplorerDataset.Bindings, Is("msisdn", ExplorerOperator.Equals, "9120000001")), tight));

        Assert.Equal("Refused", ex.Plan!.Verdict);
        Assert.True(ex.Plan.EstimatedRows > 1);
    }

    /// <summary>A value is a value: SQL in it is searched for, not run.</summary>
    [Fact]
    public async Task Sql_typed_as_a_value_is_only_ever_a_value()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var rows = await RunAsync(new(ExplorerDataset.Bindings, All(
            Is("msisdn", ExplorerOperator.StartsWith, "912"),
            Is("model", ExplorerOperator.Equals, "x' OR 1=1 --"))));

        Assert.Equal(0, rows.Total);
        Assert.Equal("8", await QueryAsync($"SELECT count() FROM {_database}.binding_current"));
    }

    [Fact]
    public void Every_catalogue_field_has_somewhere_to_be_read_from()
    {
        foreach (var dataset in ExplorerCatalogue.All)
        {
            Assert.Equal(
                dataset.Fields.Keys.Order(StringComparer.Ordinal),
                ExplorerSqlCompiler.Expressions[dataset.Dataset].Keys.Order(StringComparer.Ordinal));
        }
    }
}
