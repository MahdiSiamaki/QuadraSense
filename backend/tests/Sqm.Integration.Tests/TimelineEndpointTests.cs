using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sqm.Application.Identity;
using Sqm.Application.Timeline;
using Sqm.Domain.Bindings;
using Sqm.Domain.Timeline;

namespace Sqm.Integration.Tests;

/// <summary>
/// A timeline follows the identifier rules: the centre's own lookup permission, related kinds
/// withheld from whoever may not look them up, masking without reveal, and an audit without the
/// identifier - and is not served from an incomplete history.
/// </summary>
/// <remarks>The real host with the store stubbed; the store itself is proven in BindingHistoryTests.</remarks>
public sealed class TimelineEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Imei = "35085748000001";
    private const ulong Number = 9121234567, Sim = 432110123456789;

    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<AuditEntry> _audited = [];
    private readonly List<string> _read = [];
    private string[] _held = [Permissions.LookupSubscriber, Permissions.LookupImsi, Permissions.LookupImei];
    private HistoryReadiness _readiness = HistoryReadiness.Ready;

    public TimelineEndpointTests(WebApplicationFactory<Program> factory) =>
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
            services.AddSingleton(TestStubs.Create<ITimelineStore>((method, args) =>
            {
                _read.Add(method.Name);
                return method.Name switch
                {
                    "GetReadinessAsync" => Task.FromResult(_readiness),
                    "DataThroughAsync" => Task.FromResult<DateOnly?>(new DateOnly(2026, 9, 26)),
                    "GetAsync" => Task.FromResult(new StoredTimeline(
                    [
                        new StoredBinding(Number, Sim, Imei, "35085748", "Samsung", "Galaxy A32", true,
                            [new BindingEvent(new DateOnly(2026, 3, 1), 30, ChangeLabel.Remove)], false),
                    ], 1, 4, 81_920)),
                    _ => throw new NotSupportedException(method.Name),
                };
            }));
        }));

    private async Task<(HttpStatusCode Status, JsonElement Body, string Raw)> PostAsync(object body)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/timeline") { Content = JsonContent.Create(body) };
        request.Headers.Add("Cookie", "sqm_session=analyst; sqm_csrf=t");
        request.Headers.Add("X-CSRF-Token", "t");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone(), text);
    }

    [Fact]
    public async Task A_handsets_timeline_has_its_bindings_and_its_groups()
    {
        _held = [.. _held, Permissions.IdentifierReveal];

        var (status, body, _) = await PostAsync(new { identifier = Imei });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(("imei", "Galaxy A32"), (body.GetProperty("kind").GetString(), body.GetProperty("model").GetString()));
        var binding = body.GetProperty("bindings")[0];
        Assert.Equal((Number.ToString(), "2025-12-27", true), (
            binding.GetProperty("msisdn").GetString(),
            binding.GetProperty("periods")[0].GetProperty("start").GetString(),
            binding.GetProperty("periods")[0].GetProperty("startsInDump").GetBoolean()));
        Assert.Equal(["msisdn", "imsi"], body.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("kind").GetString()));
        Assert.Equal("2026-09-26", body.GetProperty("dataThrough").GetString());
    }

    [Fact]
    public async Task Without_reveal_the_related_identifiers_are_masked_and_cannot_be_opened()
    {
        var (_, body, raw) = await PostAsync(new { identifier = Imei });

        Assert.True(body.GetProperty("masked").GetBoolean());
        Assert.DoesNotContain(Number.ToString(), raw, StringComparison.Ordinal);
        Assert.DoesNotContain(Sim.ToString(), raw, StringComparison.Ordinal);
        Assert.All(body.GetProperty("groups").EnumerateArray(), g => Assert.False(g.GetProperty("drillable").GetBoolean()));
    }

    [Fact]
    public async Task A_kind_the_caller_may_not_look_up_is_withheld()
    {
        _held = [Permissions.LookupImei, Permissions.IdentifierReveal];

        var (status, body, raw) = await PostAsync(new { identifier = Imei });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["msisdn", "imsi"], body.GetProperty("withheld").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(body.GetProperty("groups").EnumerateArray());
        Assert.DoesNotContain(Sim.ToString(), raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_centre_needs_its_own_kinds_permission_and_a_refusal_is_audited()
    {
        _held = [Permissions.LookupSubscriber];

        var (status, _, _) = await PostAsync(new { identifier = Imei });

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.DoesNotContain("GetAsync", _read);
        var entry = Assert.Single(_audited);
        Assert.Equal((Permissions.LookupImei, AuditOutcome.Denied), (entry.Action, entry.Outcome));
    }

    [Theory]
    [InlineData(HistoryReadiness.NotDeployed)]
    [InlineData(HistoryReadiness.Building)]
    public async Task An_incomplete_history_is_not_served(HistoryReadiness readiness)
    {
        _readiness = readiness;

        var (status, _, _) = await PostAsync(new { identifier = Imei });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.DoesNotContain("GetAsync", _read);
    }

    [Fact]
    public async Task A_TAC_is_not_a_timeline()
    {
        var (status, body, _) = await PostAsync(new { identifier = "35085748" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(body.GetProperty("errors").TryGetProperty("identifier", out _));
    }

    [Fact]
    public async Task A_timeline_is_audited_by_kind_and_size_never_by_identifier()
    {
        _held = [.. _held, Permissions.IdentifierReveal];

        await PostAsync(new { identifier = Imei });

        var entry = Assert.Single(_audited);
        Assert.Equal((Permissions.LookupImei, AuditOutcome.Success, "timeline"), (entry.Action, entry.Outcome, entry.TargetType));
        Assert.Equal(("imei", 1), (entry.Detail!["kind"], entry.Detail["bindings"]));
        var serialized = JsonSerializer.Serialize(entry);
        Assert.DoesNotContain(Imei, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(Number.ToString(), serialized, StringComparison.Ordinal);
    }
}
