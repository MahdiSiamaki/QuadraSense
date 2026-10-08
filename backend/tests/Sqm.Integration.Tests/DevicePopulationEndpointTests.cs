using System.Collections.Frozen;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sqm.Application.Abstractions;
using Sqm.Application.Identity;

namespace Sqm.Integration.Tests;

/// <summary>
/// A model that had no bindings at the first delivery has no percentage change, and so nothing to
/// compare with the network's.
/// </summary>
/// <remarks>
/// The endpoint answered 0 for the change, so the page showed "+0.00%" and, with the network down
/// 9.3% over the span, a green "+9.3 pts" - for exactly the models the New models page links to.
/// The vendor widget already answered null in the same case; the device page now does too.
/// </remarks>
public sealed class DevicePopulationEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Tac = "35123456";

    private readonly WebApplicationFactory<Program> _factory;
    private long _bindingsAtStart;

    public DevicePopulationEndpointTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
        {
            services.AddSingleton(TestStubs.Create<ISessionStore>((method, _) => method.Name switch
            {
                "ResolveAsync" => Task.FromResult<ResolvedSession?>(new ResolvedSession(
                    Guid.NewGuid(),
                    new AuthenticatedUser(4, "analyst", "Analyst", false,
                        new[] { Permissions.DeviceView }.ToFrozenSet(StringComparer.Ordinal)),
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(1))),
                "TouchAsync" => Task.CompletedTask,
                "DeleteExpiredAsync" => Task.FromResult(0),
                _ => throw new NotSupportedException(method.Name),
            }));
            services.AddSingleton(TestStubs.Create<IAuditLog>((_, _) => Task.CompletedTask));
            services.AddSingleton(TestStubs.Create<IDeviceAnalyticsStore>((method, _) => method.Name switch
            {
                "GetDeviceAsync" => Task.FromResult<DeviceDetailRow?>(Row(_bindingsAtStart)),
                _ => throw new NotSupportedException(method.Name),
            }));
            services.AddSingleton(TestStubs.Create<IDeviceImageStore>((method, _) => method.Name switch
            {
                "GetInfoAsync" => Task.FromResult<DeviceImageInfo?>(null),
                _ => throw new NotSupportedException(method.Name),
            }));
        }));

    /// <summary>1,200 bindings now; the network fell from 100M to 90.7M over the same span.</summary>
    private static DeviceDetailRow Row(long bindingsAtStart) => new(
        Summary: new DeviceRow(
            Tac, "Samsung Korea", "Samsung", "Samsung", "SM-A146B", "Galaxy A14 5G", "Smartphone", "Android",
            Bindings: 1_200, Handsets: 1_150, Sims: 1_190, Subscribers: 1_180,
            FirstSeen: new DateOnly(2026, 8, 20), LastSeen: new DateOnly(2026, 10, 3)),
        Oem: null, OrganisationId: null, AllocationDate: null, LastUpdatedDate: null,
        Bluetooth: null, Nfc: null, Wlan: null, SimSlots: null, ImeiQuantity: null, Bands: null,
        HasLte: true, Has5g: true, HasEsim: false, ImsEmergency: null,
        BindingsAtStart: bindingsAtStart,
        NetworkNow: 90_700_000,
        NetworkAtStart: 100_000_000,
        KnownToGsma: true,
        TacVersionId: 3);

    private async Task<JsonElement> PopulationAsync(long bindingsAtStart)
    {
        _bindingsAtStart = bindingsAtStart;
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/devices/{Tac}");
        request.Headers.Add("Cookie", "sqm_session=analyst");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("population").Clone();
    }

    [Fact]
    public async Task A_model_absent_at_the_first_delivery_has_no_change_and_no_comparison()
    {
        var population = await PopulationAsync(bindingsAtStart: 0);

        Assert.Equal(0, population.GetProperty("bindingsAtStart").GetInt64());
        Assert.Equal(JsonValueKind.Null, population.GetProperty("changePercent").ValueKind);
        Assert.Equal(JsonValueKind.Null, population.GetProperty("vsNetworkPoints").ValueKind);
    }

    [Fact]
    public async Task A_model_present_at_the_start_is_measured_against_the_network()
    {
        var population = await PopulationAsync(bindingsAtStart: 1_000);

        // +20% for the model against -9.3% for the network: 29.3 points ahead of it.
        Assert.Equal(20.0, population.GetProperty("changePercent").GetDouble(), 4);
        Assert.Equal(29.3, population.GetProperty("vsNetworkPoints").GetDouble(), 4);
    }
}
