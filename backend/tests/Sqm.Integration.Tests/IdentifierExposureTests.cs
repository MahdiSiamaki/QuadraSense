using System.Collections.Frozen;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sqm.Application.Abstractions;
using Sqm.Application.Identity;
using Sqm.Contracts.Dashboard;
using Sqm.Contracts.Lookup;

namespace Sqm.Integration.Tests;

/// <summary>
/// identifier.reveal decides complete identifiers on every page, and every lookup is audited.
/// </summary>
/// <remarks>
/// <para>
/// IMSI search and a device's identifier list honoured the permission; the MSISDN lookup and the
/// relationship explorer did not, so an analyst whose reveal was denied saw every SIM and handset
/// in full there. The product owner chose to enforce it everywhere. Masking stays off for anyone
/// who holds the permission, which the default roles do.
/// </para>
/// <para>
/// IMSI history - every number and handset one SIM was ever bound to - was the only lookup that
/// wrote no audit entry.
/// </para>
/// </remarks>
public sealed class IdentifierExposureTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Imsi = "432110123456789";
    private const string Imei = "35085748123456";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<AuditEntry> _audited = [];
    private bool _reveal;

    public IdentifierExposureTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
        {
            services.AddSingleton(TestStubs.Create<ISessionStore>((method, _) => method.Name switch
            {
                "ResolveAsync" => Task.FromResult<ResolvedSession?>(new ResolvedSession(
                    Guid.NewGuid(),
                    new AuthenticatedUser(3, "analyst", "Analyst", false, Held()),
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(1))),
                "TouchAsync" => Task.CompletedTask,
                "DeleteExpiredAsync" => Task.FromResult(0),
                _ => throw new NotSupportedException(method.Name),
            }));
            services.AddSingleton(TestStubs.Create<IAuditLog>((method, args) =>
            {
                _audited.Add((AuditEntry)args[0]!);
                return Task.CompletedTask;
            }));
            services.AddSingleton(TestStubs.Create<IDeviceAnalyticsStore>((method, _) => method.Name switch
            {
                "GetBindingsForMsisdnAsync" => Task.FromResult<IReadOnlyList<BindingRow>>(
                [
                    new BindingRow("9121234567", Imsi, Imei, "35085748", "Samsung", "Galaxy A32",
                        "Smartphone", true, new DateOnly(2026, 8, 31)),
                ]),
                "GetRelationshipsAsync" => Task.FromResult(new RelationshipGraph(
                    "9121234567", RelatedKind.Msisdn, true, [],
                    [new RelatedNode(Imsi, RelatedKind.Imsi, 1, 1, null)],
                    [new RelatedNode(Imei, RelatedKind.Imei, 1, 1, null)],
                    [new PairedHandset("35085748123457", "Galaxy A32", 1)],
                    [], false, 1, 1)),
                "GetImsiHistoryAsync" => Task.FromResult(new ImsiHistoryOutcome(
                    [new ImsiHistoryRow(new DateOnly(2026, 8, 31), 5, 9121234567, Imei, null, null, true)],
                    false, 1, 1)),
                _ => throw new NotSupportedException(method.Name),
            }));
        }));

    [Fact]
    public async Task Msisdn_lookup_masks_sims_and_handsets_without_reveal()
    {
        var body = await PostAsync("/api/v1/lookup/msisdn", new { msisdn = "9121234567" });
        var row = body.GetProperty("bindings")[0];

        Assert.Equal("Masked", body.GetProperty("identifiers").GetString());
        Assert.Equal("9121234567", row.GetProperty("msisdn").GetString()); // what was typed
        Assert.DoesNotContain(Imsi, body.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(Imei, body.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Msisdn_lookup_is_complete_with_reveal()
    {
        _reveal = true;
        var body = await PostAsync("/api/v1/lookup/msisdn", new { msisdn = "9121234567" });

        Assert.Equal("Full", body.GetProperty("identifiers").GetString());
        Assert.Equal(Imsi, body.GetProperty("bindings")[0].GetProperty("imsi").GetString());
    }

    [Fact]
    public async Task Relationship_neighbours_are_masked_without_reveal()
    {
        var body = await PostAsync("/api/v1/relationships/explore", new { identifier = "9121234567" });

        Assert.Equal("Masked", body.GetProperty("identifiers").GetString());
        Assert.Equal("9121234567", body.GetProperty("centre").GetString());
        Assert.DoesNotContain(Imsi, body.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(Imei, body.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("35085748123457", body.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Relationship_neighbours_are_complete_with_reveal()
    {
        _reveal = true;
        var body = await PostAsync("/api/v1/relationships/explore", new { identifier = "9121234567" });

        Assert.Equal("Full", body.GetProperty("identifiers").GetString());
        Assert.Contains(Imsi, body.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Imsi_history_is_audited()
    {
        await PostAsync("/api/v1/lookup/imsi/history", new { imsi = Imsi });

        var entry = Assert.Single(_audited);
        Assert.Equal(Permissions.LookupImsi, entry.Action);
        Assert.Equal("imsi.history", entry.TargetType);
        Assert.Equal(3, entry.ActorUserId);
        Assert.DoesNotContain(Imsi, JsonSerializer.Serialize(entry), StringComparison.Ordinal);
    }

    private FrozenSet<string> Held() =>
        new[] { Permissions.LookupSubscriber, Permissions.LookupImsi, Permissions.LookupImei }
            .Concat(_reveal ? [Permissions.IdentifierReveal] : [])
            .ToFrozenSet(StringComparer.Ordinal);

    private async Task<JsonElement> PostAsync(string path, object body)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Cookie", "sqm_session=analyst; sqm_csrf=t");
        request.Headers.Add("X-CSRF-Token", "t");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }
}
