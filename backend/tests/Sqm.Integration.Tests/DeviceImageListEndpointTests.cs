using System.Collections.Frozen;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sqm.Application.Abstractions;
using Sqm.Application.Identity;
using Sqm.Contracts.Devices;

namespace Sqm.Integration.Tests;

/// <summary>
/// The review queue's query string reaches the store as exactly the query it names: every filter
/// reduced to its closed set, the page size clamped, and no page number able to break the offset.
/// </summary>
/// <remarks>The real host with the store stubbed; the store's SQL is proven in DeviceImageCandidateTests.</remarks>
public sealed class DeviceImageListEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<DeviceImageCandidateQuery> _queries = [];

    public DeviceImageListEndpointTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
        {
            services.AddSingleton(TestStubs.Create<ISessionStore>((method, _) => method.Name switch
            {
                "ResolveAsync" => Task.FromResult<ResolvedSession?>(new ResolvedSession(
                    Guid.NewGuid(),
                    new AuthenticatedUser(1, "admin", "Administrator", false,
                        new[] { Permissions.DeviceImageManage }.ToFrozenSet(StringComparer.Ordinal)),
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(1))),
                "TouchAsync" => Task.CompletedTask,
                "DeleteExpiredAsync" => Task.FromResult(0),
                _ => throw new NotSupportedException(method.Name),
            }));
            services.AddSingleton(TestStubs.Create<IAuditLog>((_, _) => Task.CompletedTask));
            services.AddSingleton(TestStubs.Create<IDeviceImageCandidateStore>((method, args) =>
            {
                if (method.Name == "ListAsync")
                {
                    _queries.Add((DeviceImageCandidateQuery)args[0]!);
                    return Task.FromResult(new DeviceImageCandidatePage(0, []));
                }

                throw new NotSupportedException(method.Name);
            }));
        }));

    private async Task<HttpStatusCode> GetAsync(string query)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/devices/image-candidates" + query);
        request.Headers.Add("Cookie", "sqm_session=admin; sqm_csrf=t");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    [Fact]
    public async Task Every_filter_reaches_the_store_as_named()
    {
        Assert.Equal(HttpStatusCode.OK, await GetAsync("?status=all&brand=%20Samsung%20&sourceType=manufacturer&onNetwork=false&warnings=HIGH&sort=score&page=3&pageSize=500"));

        var q = Assert.Single(_queries);
        Assert.Equal(("all", "Samsung", "manufacturer", (bool?)false, "high", "score"), (q.Status, q.Brand, q.SourceType, q.OnNetwork, q.Warnings, q.Sort));
        Assert.Equal((96, 192), (q.Limit, q.Offset));
    }

    [Fact]
    public async Task Anything_outside_a_closed_set_is_the_default()
    {
        Assert.Equal(HttpStatusCode.OK, await GetAsync("?status=deleted&onNetwork=maybe&warnings=some&sort=name&pageSize=0"));

        var q = Assert.Single(_queries);
        Assert.Equal(("needs_review", (string?)null, (bool?)null, "any", "bindings", 1, 0), (q.Status, q.Brand, q.OnNetwork, q.Warnings, q.Sort, q.Limit, q.Offset));
        Assert.Equal(HttpStatusCode.OK, await GetAsync("?onNetwork=TRUE&warnings=without"));
        Assert.Equal(((bool?)true, "without"), (_queries[1].OnNetwork, _queries[1].Warnings));
    }

    [Fact]
    public async Task A_page_number_too_large_for_an_offset_is_an_empty_page_not_a_500()
    {
        Assert.Equal(HttpStatusCode.OK, await GetAsync("?page=44739245"));

        Assert.Equal(int.MaxValue, Assert.Single(_queries).Offset);
    }
}
