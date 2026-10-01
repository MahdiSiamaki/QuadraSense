using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sqm.Application.Abstractions;
using Sqm.Application.Explorer;
using Sqm.Application.Identity;
using Sqm.Contracts.Explorer;

namespace Sqm.Integration.Tests;

/// <summary>
/// The Explorer's routes enforce both layers of permission, mask identifiers without reveal, and
/// audit every query and refusal without the values it looked for.
/// </summary>
/// <remarks>
/// The real host, with the engine stubbed: what is under test is the API's decisions, not the SQL -
/// <see cref="ExplorerEngineTests"/> covers that against ClickHouse.
/// </remarks>
public sealed class ExplorerEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Msisdn = "9121234567";
    private const string Imsi = "432110123456789";
    private const string Imei = "35085748123456";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<AuditEntry> _audited = [];
    private readonly List<CheckedExplorerQuery> _ran = [];
    private string[] _held = [Permissions.ExplorerQuery, Permissions.LookupSubscriber, Permissions.LookupImsi, Permissions.LookupImei];
    private Func<CheckedExplorerQuery, ExplorerRows>? _engine;

    private static readonly ExplorerPlanInfo Plan = new("Current state, read by number", "KeyRead", 400_000, "Light", 100_000_000, []);

    public ExplorerEndpointTests(WebApplicationFactory<Program> factory) =>
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
            services.AddSingleton(TestStubs.Create<IDeviceAnalyticsStore>((method, _) => method.Name switch
            {
                "GetModelIdentityAsync" => Task.FromResult<DeviceModelIdentity?>(new("Samsung", "Samsung Korea", "Galaxy A32")),
                _ => throw new NotSupportedException(method.Name),
            }));
            services.AddSingleton(TestStubs.Create<IExplorerEngine>((method, args) =>
            {
                var query = (CheckedExplorerQuery)args[0]!;
                _ran.Add(query);
                return method.Name switch
                {
                    "PlanAsync" => Task.FromResult(Plan),
                    "RunAsync" => Task.FromResult((_engine ?? Rows)(query)),
                    _ => throw new NotSupportedException(method.Name),
                };
            }));
        }));

    /// <summary>One row with a plausible value for every column.</summary>
    private static ExplorerRows Rows(CheckedExplorerQuery query) => new(Plan,
        [[.. query.Columns.Select(c => (object?)(c.Type switch
        {
            ExplorerFieldType.Msisdn => Msisdn,
            ExplorerFieldType.Imsi => Imsi,
            ExplorerFieldType.Imei => Imei,
            ExplorerFieldType.Boolean => true,
            ExplorerFieldType.Date => "2026-09-26",
            ExplorerFieldType.Number => 3L,
            _ => "Galaxy A32",
        }))]], 1, 12, 400_000);

    private static object Query(params string[] columns) => new
    {
        dataset = "Bindings",
        where = new { field = "tac", @operator = "Equals", values = new[] { "35085748" } },
        columns,
    };

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string path, object body)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Cookie", "sqm_session=analyst; sqm_csrf=t");
        request.Headers.Add("X-CSRF-Token", "t");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    [Fact]
    public async Task Without_explorer_query_the_builder_is_closed()
    {
        _held = [Permissions.LookupSubscriber, Permissions.LookupImsi, Permissions.LookupImei, Permissions.IdentifierReveal];

        var (status, _) = await PostAsync("/api/v1/explorer/query", Query("model"));

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Empty(_ran);
    }

    [Fact]
    public async Task A_query_for_identifiers_the_caller_may_not_see_is_refused_whole_and_says_why()
    {
        _held = [Permissions.ExplorerQuery, Permissions.LookupImei];

        var (status, body) = await PostAsync("/api/v1/explorer/query", Query("imei", "msisdn"));

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal([Permissions.LookupSubscriber], body.GetProperty("missing").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(_ran);

        var entry = Assert.Single(_audited);
        Assert.Equal((Permissions.ExplorerQuery, AuditOutcome.Denied), (entry.Action, entry.Outcome));
    }

    /// <summary>With explorer.query alone a person can query models and TACs - and nobody by name.</summary>
    [Fact]
    public async Task Models_and_TACs_need_no_lookup_permission()
    {
        _held = [Permissions.ExplorerQuery];

        var (status, body) = await PostAsync("/api/v1/explorer/query", Query("tac", "model", "active"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Galaxy A32", body.GetProperty("rows")[0][1].GetString());
    }

    [Fact]
    public async Task Without_reveal_every_identifier_comes_back_masked()
    {
        var (status, body) = await PostAsync("/api/v1/explorer/query", Query("msisdn", "imsi", "imei", "model"));
        var raw = body.GetRawText();

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain(Msisdn, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(Imsi, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(Imei, raw, StringComparison.Ordinal);
        Assert.Equal([true, true, true, false], body.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("masked").GetBoolean()));
    }

    [Fact]
    public async Task With_reveal_identifiers_are_complete()
    {
        _held = [.. _held, Permissions.IdentifierReveal];

        var (_, body) = await PostAsync("/api/v1/explorer/query", Query("msisdn", "imsi", "imei"));

        Assert.Equal([Msisdn, Imsi, Imei], body.GetProperty("rows")[0].EnumerateArray().Select(v => v.GetString()));
    }

    /// <summary>The audit says what the query was - dataset, fields, cost - never what it looked for.</summary>
    [Fact]
    public async Task Every_query_is_audited_without_its_values()
    {
        _held = [.. _held, Permissions.IdentifierReveal];

        await PostAsync("/api/v1/explorer/query", new
        {
            dataset = "Bindings",
            where = new { field = "msisdn", @operator = "Equals", values = new[] { Msisdn } },
            columns = new[] { "imsi" },
        });

        var entry = Assert.Single(_audited);
        var detail = JsonSerializer.Serialize(entry);

        Assert.Equal((Permissions.ExplorerQuery, AuditOutcome.Success, 7L), (entry.Action, entry.Outcome, entry.ActorUserId));
        Assert.Equal("msisdn", entry.Detail!["fields"]);
        Assert.Equal(1, entry.Detail["returned"]);
        Assert.DoesNotContain(Msisdn, detail, StringComparison.Ordinal);
        Assert.DoesNotContain(Imsi, detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_query_over_the_budget_is_answered_with_its_plan_and_audited()
    {
        _engine = _ => throw new ExplorerRefusedException(
            Plan with { Verdict = "Refused", EstimatedRows = 737_000_000, Notes = ["To bring it within the limit: ..."] },
            "This query would read about 737,000,000 rows; the limit is 100,000,000.");

        var (status, body) = await PostAsync("/api/v1/explorer/query", Query("model"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("Refused", body.GetProperty("plan").GetProperty("verdict").GetString());
        Assert.Equal(AuditOutcome.Failure, Assert.Single(_audited).Outcome);
    }

    [Fact]
    public async Task When_every_slot_is_taken_the_answer_is_busy()
    {
        _engine = _ => throw new ExplorerBusyException();

        var (status, _) = await PostAsync("/api/v1/explorer/query", Query("model"));

        Assert.Equal(HttpStatusCode.TooManyRequests, status);
    }

    [Fact]
    public async Task A_malformed_query_says_where_before_anything_runs()
    {
        var (status, body) = await PostAsync("/api/v1/explorer/query", new
        {
            dataset = "Bindings",
            where = new { field = "tac", @operator = "Equals", values = new[] { "123" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(body.GetProperty("errors").TryGetProperty("where.values[0]", out _));
        Assert.Empty(_ran);
    }

    [Fact]
    public async Task An_entity_summary_follows_its_kinds_lookup_permission()
    {
        _held = [Permissions.ExplorerQuery, Permissions.LookupSubscriber];
        _engine = q => new ExplorerRows(Plan, [[3L, 2L, 1L, 1L, 2L, 1L, 3L, 2L, "2026-09-26"]], 1, 5, 400_000);

        var (refused, _) = await PostAsync("/api/v1/explorer/entity", new { identifier = Imsi });
        var (status, body) = await PostAsync("/api/v1/explorer/entity", new { identifier = "0912 123 4567" });

        Assert.Equal(HttpStatusCode.Forbidden, refused);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(("msisdn", 2L, 3L, "2026-09-26"), (
            body.GetProperty("kind").GetString(),
            body.GetProperty("sims").GetInt64(),
            body.GetProperty("handsets").GetInt64(),
            body.GetProperty("lastChange").GetString()));
    }
}
