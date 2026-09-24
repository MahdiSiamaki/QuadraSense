using System.Collections.Frozen;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sqm.Application.Abstractions;
using Sqm.Application.Identity;
using Sqm.Contracts.Dashboard;

namespace Sqm.Integration.Tests;

/// <summary>
/// The dashboard's MSISDN filter takes an operator range, never a whole number.
/// </summary>
/// <remarks>
/// A ten-digit "prefix" filtered the dashboard to one subscriber, so anyone with dashboard.view
/// could learn which handset a named person uses - without lookup.subscriber, without an audit
/// entry, with the number in the URL. The product owner capped it at six digits. Malformed input
/// used to escape as an exception from the query builder, a 500; it is a 400 now.
/// </remarks>
public sealed class DashboardPrefixTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly List<DashboardFilter> _queried = [];
    private readonly WebApplicationFactory<Program> _factory;

    public DashboardPrefixTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
        {
            services.AddSingleton(TestStubs.Create<ISessionStore>((method, _) => method.Name switch
            {
                "ResolveAsync" => Task.FromResult<ResolvedSession?>(new ResolvedSession(
                    Guid.NewGuid(),
                    new AuthenticatedUser(1, "viewer", "Viewer", false,
                        new[] { Permissions.DashboardView }.ToFrozenSet(StringComparer.Ordinal)),
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(1))),
                "TouchAsync" => Task.CompletedTask,
                "DeleteExpiredAsync" => Task.FromResult(0),
                _ => throw new NotSupportedException(method.Name),
            }));
            services.AddSingleton(TestStubs.Create<IDeviceAnalyticsStore>((method, args) =>
            {
                if (method.Name != "GetTopDimensionAsync")
                {
                    throw new NotSupportedException(method.Name);
                }

                _queried.Add((DashboardFilter)args[1]!);
                return Task.FromResult<IReadOnlyList<DimensionCount>>([]);
            }));
        }));

    [Theory]
    [InlineData("9121234567")] // a whole number
    [InlineData("9121234")]    // one digit past the cap
    [InlineData("91a")]
    [InlineData("-912")]
    public async Task A_prefix_that_is_not_an_operator_range_is_refused_before_any_query(string prefix)
    {
        using var response = await GetAsync($"/api/v1/dashboard/top/tac?msisdnPrefix={prefix}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_queried);
    }

    [Theory]
    [InlineData("912")]
    [InlineData("912123")]
    public async Task An_operator_range_is_still_served(string prefix)
    {
        using var response = await GetAsync($"/api/v1/dashboard/top/tac?msisdnPrefix={prefix}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(prefix, Assert.Single(_queried).MsisdnPrefix);
    }

    private async Task<HttpResponseMessage> GetAsync(string path)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", "sqm_session=viewer");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
