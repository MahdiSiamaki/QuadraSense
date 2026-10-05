using System.Collections.Frozen;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sqm.Application.Devices;
using Sqm.Application.Identity;

namespace Sqm.Integration.Tests;

/// <summary>
/// New models and network age need device.view, wait for a complete table rather than show part of
/// one, and count network age as days in this data - a lower bound for the dump's models.
/// </summary>
/// <remarks>The real host with the reader stubbed; the reader is proven against ClickHouse in ModelArrivalTests.</remarks>
public sealed class ModelArrivalEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly DateOnly Through = new(2026, 9, 26);

    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<NewModelQuery> _queries = [];
    private string[] _held = [Permissions.DeviceView];
    private bool _complete = true;

    public ModelArrivalEndpointTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
        {
            services.AddSingleton(TestStubs.Create<ISessionStore>((method, _) => method.Name switch
            {
                "ResolveAsync" => Task.FromResult<ResolvedSession?>(new ResolvedSession(
                    Guid.NewGuid(),
                    new AuthenticatedUser(7, "viewer", "Viewer", false, _held.ToFrozenSet(StringComparer.Ordinal)),
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(1))),
                "TouchAsync" => Task.CompletedTask,
                "DeleteExpiredAsync" => Task.FromResult(0),
                _ => throw new NotSupportedException(method.Name),
            }));
            services.AddSingleton(TestStubs.Create<IAuditLog>((_, _) => Task.CompletedTask));
            services.AddSingleton(TestStubs.Create<IModelArrivalReader>((method, args) => method.Name switch
            {
                "GetStateAsync" => Task.FromResult(new ModelArrivalState(true, _complete, _complete ? 233 : 120, 233, Through)),
                "ListAsync" => List((NewModelQuery)args[0]!),
                "ArrivalsAsync" => Task.FromResult<IReadOnlyList<ModelArrivalPeriod>>([new ModelArrivalPeriod(new DateOnly(2026, 9, 1), 85, 20)]),
                "FirstSeenAsync" => Task.FromResult<ModelFirstSeen?>((string)args[0]! == "35000001"
                    ? new ModelFirstSeen("35000001", true, new DateOnly(2026, 3, 1), 200)
                    : new ModelFirstSeen((string)args[0]!, false, new DateOnly(2026, 9, 20), 7)),
                _ => throw new NotSupportedException(method.Name),
            }));
        }));

    private Task<NewModelPage> List(NewModelQuery query)
    {
        _queries.Add(query);
        return Task.FromResult(new NewModelPage(
            [new NewModelRow("35000003", "Apple", "iPhone 17", "Smartphone", new DateOnly(2026, 9, 20), 7, 120, 900, 950)], 1, 12));
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string path)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", "sqm_session=viewer; sqm_csrf=t");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, text.StartsWith('{') ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    [Fact]
    public async Task New_models_default_to_the_last_30_days_with_their_network_age()
    {
        var (status, body) = await GetAsync("/api/v1/devices/new-models?deviceType=Smartphone&brand=%20apple%20");

        Assert.Equal(HttpStatusCode.OK, status);
        var query = Assert.Single(_queries);
        Assert.Equal((new DateOnly(2026, 8, 28), Through, "apple", "Smartphone", true), (query.From, query.To, query.Brand, query.DeviceType, query.KnownOnly));

        var row = body.GetProperty("rows")[0];
        Assert.Equal(("2026-09-20", 6), (row.GetProperty("firstSeen").GetString(), row.GetProperty("networkAgeDays").GetInt32()));
    }

    [Fact]
    public async Task A_model_from_the_dump_has_an_age_that_is_a_lower_bound()
    {
        var (_, dump) = await GetAsync("/api/v1/devices/35000001/network-age");
        Assert.Equal((true, true, 244), (dump.GetProperty("inDump").GetBoolean(), dump.GetProperty("atLeast").GetBoolean(), dump.GetProperty("networkAgeDays").GetInt32()));

        var (_, recent) = await GetAsync("/api/v1/devices/35000003/network-age");
        Assert.Equal((false, 6), (recent.GetProperty("atLeast").GetBoolean(), recent.GetProperty("networkAgeDays").GetInt32()));
    }

    [Fact]
    public async Task Nothing_is_listed_from_a_half_written_table()
    {
        _complete = false;

        var (status, body) = await GetAsync("/api/v1/devices/new-models");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Contains("120 of 233 days", body.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Empty(_queries);
    }

    [Fact]
    public async Task Without_device_view_nothing_is_served()
    {
        _held = [Permissions.DashboardView];

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync("/api/v1/devices/new-models")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync("/api/v1/devices/arrivals")).Status);
    }
}
