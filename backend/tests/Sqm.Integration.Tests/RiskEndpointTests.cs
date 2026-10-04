using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sqm.Application.Identity;
using Sqm.Application.Quality;
using Sqm.Application.Risk;
using Sqm.Domain.Quality;
using Sqm.Domain.Risk;

namespace Sqm.Integration.Tests;

/// <summary>
/// Risk lists follow the identifier rules - risk.view, the unit's own lookup permission, masking
/// without reveal, an audit without identifiers - judge every row with the rule set in force, and
/// refuse to judge at all before a threshold is calibrated.
/// </summary>
/// <remarks>The real host with the reader stubbed; the reader's SQL is proven against ClickHouse elsewhere.</remarks>
public sealed class RiskEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Sim = "432110123456789";
    private static readonly DateOnly AsOf = new(2026, 9, 26);

    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<AuditEntry> _audited = [];
    private readonly List<RiskListQuery> _lists = [];
    private string[] _held = [Permissions.RiskView, Permissions.LookupImsi, Permissions.LookupImei, Permissions.LookupSubscriber];
    private long? _threshold = 20;
    private long? _imeiThreshold;
    private List<RiskEntityMeasures> _linked = [];
    private DateOnly? _flagged;

    public RiskEndpointTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
        {
            services.AddSingleton(TestStubs.Create<ISessionStore>((method, _) => method.Name switch
            {
                "ResolveAsync" => Task.FromResult<ResolvedSession?>(new ResolvedSession(
                    Guid.NewGuid(),
                    new AuthenticatedUser(7, "analyst", "Analyst", false, _held.ToFrozenSet(StringComparer.Ordinal)),
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(1))),
                "TouchAsync" => Task.CompletedTask,
                "DeleteExpiredAsync" => Task.FromResult(0),
                _ => throw new NotSupportedException(method.Name),
            }));
            services.AddSingleton(TestStubs.Create<IAuditLog>((_, args) =>
            {
                _audited.Add((AuditEntry)args[0]!);
                return Task.CompletedTask;
            }));
            services.AddSingleton(TestStubs.Create<IOptionsMonitor<RiskOptions>>((method, _) => method.Name switch
            {
                "get_CurrentValue" => Options(),
                _ => throw new NotSupportedException(method.Name),
            }));
            services.AddSingleton(TestStubs.Create<IFeedQualityReader>((_, args) =>
                Task.FromResult<IReadOnlyList<FeedQualityDay>>(Days((DateOnly?)args[0], (DateOnly?)args[1]))));
            services.AddSingleton(TestStubs.Create<IRiskReader>((method, args) => method.Name switch
            {
                "GetStateAsync" => Task.FromResult(new RiskReadState(
                    true, new RiskPublishedRun(1, AsOf, 1, DateTimeOffset.UtcNow), null, AsOf,
                    Enumerable.Range(0, 60).Select(i => AsOf.AddDays(-i)).ToHashSet())),
                "ListAsync" => List((RiskListQuery)args[1]!),
                "CountAsync" => Task.FromResult<IReadOnlyList<RiskRuleCount>>([new RiskRuleCount(RiskRule.HighDeviceCount30, 12, 340)]),
                "GetEntityAsync" => Task.FromResult<RiskEntityMeasures?>(Measures()),
                "GetLinkedAsync" => Task.FromResult<IReadOnlyList<RiskEntityMeasures>>(_linked),
                "DeviceTypesAsync" => Task.FromResult<IReadOnlyList<string>>(["Modem", "Smartphone"]),
                _ => throw new NotSupportedException(method.Name),
            }));
        }));

    private RiskOptions Options()
    {
        var options = new RiskOptions();
        options.Thresholds.HighDeviceCount30 = _threshold;
        options.Thresholds.SharedImeiSims30 = _imeiThreshold;
        return options;
    }

    /// <summary>A million rows a day; on the flagged day, one in twenty is the shifted shape.</summary>
    private List<FeedQualityDay> Days(DateOnly? from, DateOnly? to)
    {
        var days = new List<FeedQualityDay>();
        for (var d = from ?? new DateOnly(2026, 1, 26); d <= (to ?? AsOf); d = d.AddDays(1))
        {
            days.Add(new FeedQualityDay(d, 1_000_000, 500_000, 10, 10, 100, d == _flagged ? 50_000 : 10, 10, 20, 1));
        }

        return days;
    }

    private static RiskEntityMeasures Measures() => new(
        RiskFamily.Sim, Sim,
        Sim: new RiskSimMeasures(40, 50, 30, 25, 12, 14, 60, 10, 20, 5, new DateOnly(2026, 9, 20), ["35000001", "35000002"]));

    private Task<RiskListPage> List(RiskListQuery query)
    {
        _lists.Add(query);
        return Task.FromResult(new RiskListPage([Measures()], 1, 3, 120));
    }

    private async Task<(HttpStatusCode Status, JsonElement Body, string Raw)> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Add("Cookie", "sqm_session=analyst; sqm_csrf=t");
        request.Headers.Add("X-CSRF-Token", "t");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json = text.StartsWith('{') ? JsonDocument.Parse(text).RootElement.Clone() : default;
        return (response.StatusCode, json, text);
    }

    private Task<(HttpStatusCode Status, JsonElement Body, string Raw)> ListAsync(object body) =>
        SendAsync(HttpMethod.Post, "/api/v1/risk/list", body);

    [Fact]
    public async Task A_row_over_its_threshold_is_a_potential_risk_signal_with_the_servers_reason()
    {
        _held = [.. _held, Permissions.IdentifierReveal];

        var (status, body, _) = await ListAsync(new { rule = "HighDeviceCount30" });

        Assert.Equal(HttpStatusCode.OK, status);
        var row = body.GetProperty("rows")[0];
        Assert.Equal((Sim, "RiskSignal", true), (row.GetProperty("key").GetString(), row.GetProperty("level").GetString(), row.GetProperty("drillable").GetBoolean()));

        var reason = row.GetProperty("reasons")[0];
        Assert.Equal("HighDeviceCount30", reason.GetProperty("rule").GetString());
        Assert.StartsWith(
            "Potential risk signal: this SIM (IMSI) was added to 40 different IMEIs in the 30 days to 2026-09-26 (30 of 30 days have data) (threshold: more than 20; rules ",
            reason.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal(40, row.GetProperty("values").GetProperty("imeis30").GetInt64());
        Assert.Equal(new RiskThreshold(20), Assert.Single(_lists).Threshold);
    }

    [Fact]
    public async Task Flagged_feed_days_in_the_window_hold_the_row_at_anomaly()
    {
        _flagged = new DateOnly(2026, 9, 20);

        var (_, body, _) = await ListAsync(new { rule = "HighDeviceCount30" });

        var reason = body.GetProperty("rows")[0].GetProperty("reasons")[0];
        Assert.Equal(("Anomaly", true), (reason.GetProperty("level").GetString(), reason.GetProperty("capped").GetBoolean()));
        Assert.Contains("shifted IMEIs; 2026-09-20 to 2026-09-20", reason.GetProperty("text").GetString(), StringComparison.Ordinal);

        var (_, state, _) = await SendAsync(HttpMethod.Get, "/api/v1/risk/status");
        var rule = state.GetProperty("rules").EnumerateArray().Single(r => r.GetProperty("rule").GetString() == "HighDeviceCount30");
        Assert.Equal(["2026-09-20"], rule.GetProperty("flaggedDays").EnumerateArray().Select(d => d.GetString()));
    }

    [Fact]
    public async Task Without_reveal_keys_are_masked_and_the_audit_names_nobody()
    {
        var (status, body, raw) = await ListAsync(new { rule = "HighDeviceCount30" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("masked").GetBoolean());
        Assert.DoesNotContain(Sim, raw, StringComparison.Ordinal);
        Assert.False(body.GetProperty("rows")[0].GetProperty("drillable").GetBoolean());

        var entry = Assert.Single(_audited);
        Assert.Equal(("risk.list", AuditOutcome.Success, "risk"), (entry.Action, entry.Outcome, entry.TargetType));
        Assert.Equal(("HighDeviceCount30", 20L, true), (entry.Detail!["rule"], entry.Detail["threshold"], entry.Detail["masked"]));
        Assert.DoesNotContain(Sim, JsonSerializer.Serialize(entry), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_list_needs_the_lookup_permission_of_its_unit_and_a_refusal_is_audited()
    {
        _held = [Permissions.RiskView, Permissions.LookupImei];

        var (status, _, _) = await ListAsync(new { rule = "HighDeviceCount30" });

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Empty(_lists);
        var entry = Assert.Single(_audited);
        Assert.Equal(("risk.list", AuditOutcome.Denied), (entry.Action, entry.Outcome));
    }

    [Fact]
    public async Task Without_risk_view_nothing_is_served()
    {
        _held = [Permissions.LookupImsi, Permissions.IdentifierReveal];

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/api/v1/risk/status")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await ListAsync(new { rule = "HighDeviceCount30" })).Status);
        Assert.Empty(_lists);
    }

    [Fact]
    public async Task Nothing_is_judged_before_a_threshold_is_calibrated()
    {
        _threshold = null;

        var (status, body, _) = await ListAsync(new { rule = "HighDeviceCount30", threshold = 30 });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Contains("not calibrated", body.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await SendAsync(HttpMethod.Get, "/api/v1/risk/overview")).Status);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await SendAsync(HttpMethod.Post, "/api/v1/risk/entity", new { identifier = Sim })).Status);
        Assert.Empty(_lists);

        var (ok, state, _) = await SendAsync(HttpMethod.Get, "/api/v1/risk/status");
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal((false, false), (state.GetProperty("ready").GetBoolean(), state.GetProperty("calibrated").GetBoolean()));
    }

    [Theory]
    [InlineData(4, HttpStatusCode.UnprocessableEntity)]   // lists 5 and up; 5 is below the floor of 6
    [InlineData(5, HttpStatusCode.OK)]
    public async Task An_override_is_applied_only_from_the_storage_floor_up(long threshold, HttpStatusCode expected)
    {
        var (status, body, _) = await ListAsync(new { rule = "HighDeviceCount30", threshold });

        Assert.Equal(expected, status);
        if (expected == HttpStatusCode.OK)
        {
            Assert.True(body.GetProperty("overridden").GetBoolean());
            Assert.Equal(threshold, Assert.Single(_lists).Threshold.Value);
            Assert.Equal((threshold, true), (_audited[^1].Detail!["threshold"], _audited[^1].Detail!["overridden"]));
        }
        else
        {
            Assert.Empty(_lists);
        }
    }

    [Fact]
    public async Task An_all_time_count_has_no_data_quality_view()
    {
        var (status, body, _) = await ListAsync(new { rule = "SharedImeiSimsEver", view = "dataQuality", threshold = 30 });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(body.GetProperty("errors").TryGetProperty("view", out _));
    }

    [Fact]
    public async Task An_entity_is_read_by_POST_only_and_judged_like_a_row()
    {
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await SendAsync(HttpMethod.Get, "/api/v1/risk/entity?identifier=" + Sim)).Status);

        var (status, body, _) = await SendAsync(HttpMethod.Post, "/api/v1/risk/entity", new { identifier = Sim });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(("imsi", "RiskSignal", true), (body.GetProperty("kind").GetString(), body.GetProperty("level").GetString(), body.GetProperty("stored").GetBoolean()));
        var entry = Assert.Single(_audited);
        Assert.Equal(("risk.entity", "imsi"), (entry.Action, entry.Detail!["kind"]));
        Assert.DoesNotContain(Sim, JsonSerializer.Serialize(entry), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Risk_signals_in_two_families_among_bound_entities_are_a_suspected_pattern_counted_not_named()
    {
        const string Imei = "35000001000001";
        _held = [.. _held, Permissions.IdentifierReveal];
        _linked =
        [
            new RiskEntityMeasures(RiskFamily.Imei, Imei, ImeiWindow: new RiskImeiWindowMeasures(40, 40, 3, 40, 50, 0, 10, 4, AsOf)),
            new RiskEntityMeasures(RiskFamily.Imei, "35000001000002", ImeiWindow: new RiskImeiWindowMeasures(8, 8, 1, 8, 8, 0, 3, 2, AsOf)),
        ];

        // Without a threshold for IMEIs, the handset is not judged: one family, no pattern.
        var (_, alone, _) = await SendAsync(HttpMethod.Post, "/api/v1/risk/entity", new { identifier = Sim });
        Assert.Equal("RiskSignal", alone.GetProperty("pattern").GetString());

        _imeiThreshold = 20;
        var (status, body, raw) = await SendAsync(HttpMethod.Post, "/api/v1/risk/entity", new { identifier = Sim });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(("RiskSignal", "SuspiciousPattern"), (body.GetProperty("level").GetString(), body.GetProperty("pattern").GetString()));
        var linked = Assert.Single(body.GetProperty("linked").EnumerateArray());
        Assert.Equal(("Imei", 2, 1), (linked.GetProperty("family").GetString(), linked.GetProperty("stored").GetInt32(), linked.GetProperty("riskSignals").GetInt32()));
        Assert.DoesNotContain(Imei, raw, StringComparison.Ordinal);
        Assert.Equal("SuspiciousPattern", _audited[^1].Detail!["pattern"]);
    }

    [Fact]
    public async Task A_list_is_narrowed_to_known_device_types_only_and_the_audit_says_which()
    {
        var (status, _, _) = await ListAsync(new { rule = "HighDeviceCount30", deviceTypes = new[] { "Modem" } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["Modem"], Assert.Single(_lists).DeviceTypes!);
        Assert.Equal("Modem", _audited[^1].Detail!["deviceTypes"]);

        var (unknown, body, _) = await ListAsync(new { rule = "HighDeviceCount30", deviceTypes = new[] { "Modem'; DROP TABLE x" } });
        Assert.Equal(HttpStatusCode.BadRequest, unknown);
        Assert.True(body.GetProperty("errors").TryGetProperty("deviceTypes", out _));
        Assert.Single(_lists);

        var (_, state, _) = await SendAsync(HttpMethod.Get, "/api/v1/risk/status");
        Assert.Equal(["Modem", "Smartphone"], state.GetProperty("deviceTypes").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task A_list_of_numbers_has_no_device_type()
    {
        var (status, body, _) = await ListAsync(new { rule = "RepeatedSimChange7", threshold = 2, deviceTypes = new[] { "Modem" } });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(body.GetProperty("errors").TryGetProperty("deviceTypes", out _));
        Assert.Empty(_lists);
    }

    [Fact]
    public async Task Export_needs_data_export()
    {
        var (status, _, _) = await SendAsync(HttpMethod.Post, "/api/v1/risk/export", new { rule = "HighDeviceCount30" });
        Assert.Equal(HttpStatusCode.Forbidden, status);

        _held = [.. _held, Permissions.DataExport, Permissions.IdentifierReveal];
        var (ok, _, csv) = await SendAsync(HttpMethod.Post, "/api/v1/risk/export", new { rule = "HighDeviceCount30" });
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Contains(Sim + ",RiskSignal,true,40,50", csv, StringComparison.Ordinal);
        Assert.Equal(RiskEndpointsMaxReachable, _lists[^1].PageSize);
    }

    private const int RiskEndpointsMaxReachable = 10_000;
}
