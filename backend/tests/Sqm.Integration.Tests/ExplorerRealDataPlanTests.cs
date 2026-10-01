using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Application.Explorer;
using Sqm.Contracts.Explorer;
using Sqm.Infrastructure.ClickHouse;
using Sqm.Infrastructure.ClickHouse.Explorer;

namespace Sqm.Integration.Tests;

/// <summary>
/// The query shapes the Explorer exists for stay index reads on the real data.
/// </summary>
/// <remarks>
/// <para>
/// Performance, guarded: each test plans a query with <c>EXPLAIN ESTIMATE</c> - indexes only, no
/// data read - against the real <c>sqm</c> database, and asserts it reads a small part of it. A
/// change that quietly turned one into a scan of 700 million rows fails here, not in front of a
/// user. Skipped, with the reason, where there is no data (a cloud session, CI, a fresh clone).
/// </para>
/// <para>
/// The sample identifiers are read from the data when the test runs, never written here: this
/// repository does not hold subscriber identifiers.
/// </para>
/// </remarks>
public sealed class ExplorerRealDataPlanTests : IAsyncLifetime
{
    private const string Endpoint = "http://localhost:18123/";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private string? _unavailable;
    private string _msisdn = string.Empty;
    private string _imsi = string.Empty;
    private string _imei = string.Empty;
    private DateOnly _lastDay;

    public async ValueTask InitializeAsync()
    {
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("sqm_app:sqm_dev")));

        try
        {
            var sample = await QueryAsync(
                "SELECT toString(msisdn), toString(imsi), imei FROM sqm.binding_current WHERE length(imei) = 14 LIMIT 1 OFFSET 1000000");
            if (string.IsNullOrEmpty(sample))
            {
                _unavailable = "no real data in sqm.binding_current";
                return;
            }

            var parts = sample.Split('\t');
            (_msisdn, _imsi, _imei) = (parts[0], parts[1], parts[2]);
            _lastDay = DateOnly.Parse(await QueryAsync("SELECT max(data_date) FROM sqm.binding_event"), CultureInfo.InvariantCulture);
        }
        catch (HttpRequestException ex)
        {
            _unavailable = $"no ClickHouse at {Endpoint}: {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            _unavailable = $"no sqm schema: {ex.Message}";
        }
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<string> QueryAsync(string sql)
    {
        using var response = await _http.PostAsync(Endpoint, new StringContent(sql, Encoding.UTF8, "text/plain"),
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return response.IsSuccessStatusCode ? body.Trim() : throw new InvalidOperationException(body);
    }

    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        });
    }

    private static readonly ExplorerOptions Options = new();

    private static ClickHouseExplorerEngine Engine() => new(
        Microsoft.Extensions.Options.Options.Create(new ClickHouseOptions
        {
            ConnectionString = "Host=localhost;Port=18123;Database=sqm;Username=sqm_app;Password=sqm_dev",
        }),
        Microsoft.Extensions.Options.Options.Create(Options),
        new Factory(),
        NullLogger<ClickHouseExplorerEngine>.Instance);

    private static ExplorerFilter Is(string field, ExplorerOperator op, params string[] values) =>
        new(Field: field, Operator: op, Values: values);

    private static async Task<ExplorerPlanInfo> PlanAsync(ExplorerQueryRequest request)
    {
        var (query, problems) = ExplorerValidator.Check(request, Options);
        Assert.True(query is not null, string.Join("; ", problems.Select(p => $"{p.Path}: {p.Message}")));

        using var engine = Engine();
        var plan = await engine.PlanAsync(query!, TestContext.Current.CancellationToken);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{plan.Source} | {plan.Access} | {plan.EstimatedRows:N0} rows | {plan.Verdict}");
        return plan;
    }

    [Fact]
    public async Task One_number_one_SIM_or_one_handset_is_a_key_read_of_its_own_copy()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var number = await PlanAsync(new(ExplorerDataset.Bindings, Is("msisdn", ExplorerOperator.Equals, _msisdn)));
        var sim = await PlanAsync(new(ExplorerDataset.Bindings, Is("imsi", ExplorerOperator.Equals, _imsi)));
        var handset = await PlanAsync(new(ExplorerDataset.Bindings, Is("imei", ExplorerOperator.Equals, _imei)));

        Assert.Equal(("Current state, read by number", "KeyRead"), (number.Source, number.Access));
        Assert.Equal(("Current state, read by SIM", "KeyRead"), (sim.Source, sim.Access));
        Assert.Equal(("Current state, read by handset", "KeyRead"), (handset.Source, handset.Access));

        // Measured 0.3-0.5 million rows each (a granule per part, plus the GSMA join): a scan is 700 million.
        Assert.All([number, sim, handset], p => Assert.InRange(p.EstimatedRows, 1, 2_000_000));
    }

    /// <summary>"How many SIMs has this number had?" - one row of measures, still a key read.</summary>
    [Fact]
    public async Task Counting_a_numbers_SIMs_reads_only_that_number()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var plan = await PlanAsync(new(ExplorerDataset.Bindings, Is("msisdn", ExplorerOperator.Equals, _msisdn),
            Measures: [new("sims", ExplorerAggregate.CountDistinct, "imsi")]));

        Assert.Equal("KeyRead", plan.Access);
        Assert.InRange(plan.EstimatedRows, 1, 2_000_000);
    }

    /// <summary>"Every Galaxy A01 with more than 10 SIMs" - a model becomes TAC ranges of the handset copy.</summary>
    [Fact]
    public async Task A_model_is_read_as_ranges_of_its_TACs_not_as_a_scan()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var plan = await PlanAsync(new(ExplorerDataset.Bindings, Is("model", ExplorerOperator.Equals, "Galaxy A01"),
            GroupBy: ["imei"],
            Measures: [new("sims", ExplorerAggregate.CountDistinct, "imsi")],
            Having: Is("sims", ExplorerOperator.GreaterThan, "10")));

        Assert.Equal(("Current state, read by handset", "RangeRead"), (plan.Source, plan.Access));
        Assert.InRange(plan.EstimatedRows, 1, Options.BudgetRows);
    }

    [Fact]
    public async Task A_query_with_no_key_every_row_must_match_is_refused_before_it_runs()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        // Active handsets per device type: meaningful, and a pass over all 700 million rows.
        var plan = await PlanAsync(new(ExplorerDataset.Bindings, Is("active", ExplorerOperator.Equals, "true"),
            GroupBy: ["deviceType"], Measures: [new("handsets", ExplorerAggregate.CountDistinct, "imei")]));

        Assert.Equal(("Scan", "Refused"), (plan.Access, plan.Verdict));
        Assert.True(plan.EstimatedRows > Options.BudgetRows);
        Assert.Contains(plan.Notes, n => n.StartsWith("To bring it within the limit", StringComparison.Ordinal));
    }

    /// <summary>A number's events over 30 days: the date range prunes days, the number seeks within each.</summary>
    [Fact]
    public async Task A_numbers_events_over_a_month_read_a_small_part_of_each_day()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var plan = await PlanAsync(new(ExplorerDataset.Events, new ExplorerFilter(ExplorerLogic.And,
        [
            Is("msisdn", ExplorerOperator.Equals, _msisdn),
            Is("date", ExplorerOperator.Between,
                _lastDay.AddDays(-29).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                _lastDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        ])));

        Assert.Equal("KeyRead", plan.Access);
        Assert.InRange(plan.EstimatedRows, 1, 2_000_000);
    }

    /// <summary>
    /// The Explorer's templates (<c>frontend/src/features/explorer/templates.ts</c>), with their
    /// default parameters, plan within the budget: a template that is refused the moment it is
    /// opened teaches nobody anything. The shapes are repeated here, not shared - keep the two in step.
    /// </summary>
    [Fact]
    public async Task Every_template_plans_within_the_budget_with_its_defaults()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        string Day(int back) => _lastDay.AddDays(-back).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var prefix = _msisdn[..6];

        var templates = new Dictionary<string, ExplorerQueryRequest>(StringComparer.Ordinal)
        {
            ["number-history (90 days)"] = new(ExplorerDataset.Events, new ExplorerFilter(ExplorerLogic.And,
                [Is("msisdn", ExplorerOperator.Equals, _msisdn), Is("date", ExplorerOperator.Between, Day(89), Day(0))])),
            ["sim-history (90 days)"] = new(ExplorerDataset.Events, new ExplorerFilter(ExplorerLogic.And,
                [Is("imsi", ExplorerOperator.Equals, _imsi), Is("date", ExplorerOperator.Between, Day(89), Day(0))])),
            ["handset-history (90 days)"] = new(ExplorerDataset.Events, new ExplorerFilter(ExplorerLogic.And,
                [Is("imei", ExplorerOperator.Equals, _imei), Is("date", ExplorerOperator.Between, Day(89), Day(0))])),
            ["sims-on-many-handsets (1 day)"] = new(ExplorerDataset.Events,
                Is("date", ExplorerOperator.Between, Day(0), Day(0)),
                GroupBy: ["imsi"], Measures: [new("handsets", ExplorerAggregate.CountDistinct, "imei")],
                Having: Is("handsets", ExplorerOperator.GreaterThan, "5")),
            ["model-handsets-many-sims (Galaxy A01)"] = new(ExplorerDataset.Bindings,
                new ExplorerFilter(ExplorerLogic.And, [Is("model", ExplorerOperator.Equals, "Galaxy A01")]),
                GroupBy: ["imei"], Measures: [new("sims", ExplorerAggregate.CountDistinct, "imsi")],
                Having: Is("sims", ExplorerOperator.GreaterThan, "10")),
            ["numbers-with-many-sims (prefix)"] = new(ExplorerDataset.Bindings,
                Is("msisdn", ExplorerOperator.StartsWith, prefix),
                GroupBy: ["msisdn"], Measures: [new("sims", ExplorerAggregate.CountDistinct, "imsi")],
                Having: Is("sims", ExplorerOperator.GreaterThan, "3")),
            ["unknown-tac (prefix)"] = new(ExplorerDataset.Bindings, new ExplorerFilter(ExplorerLogic.And,
                [Is("msisdn", ExplorerOperator.StartsWith, prefix), Is("model", ExplorerOperator.IsNull)])),
        };

        var refused = new List<string>();
        foreach (var (name, request) in templates)
        {
            var plan = await PlanAsync(request);
            TestContext.Current.TestOutputHelper?.WriteLine($"  ^ {name}");
            if (plan.Verdict == "Refused")
            {
                refused.Add($"{name}: {plan.EstimatedRows:N0} rows");
            }
        }

        // A handset's history is cheap only with the bloom index on imei that analytics migration
        // 021 adds: without it every day of the range is read whole - measured 647,523,106 rows
        // for 90 days, against 1,024,549 for a SIM's history through idx_imsi.
        var hasImeiIndex = await QueryAsync(
            "SELECT count() FROM system.data_skipping_indices WHERE database = 'sqm' AND table = 'binding_event' AND name = 'idx_imei'") == "1";

        Assert.True(refused.Count == 0,
            "Refused: " + string.Join("; ", refused)
            + (hasImeiIndex ? string.Empty : ". idx_imei is not on binding_event - apply analytics migration 021."));
    }
}
