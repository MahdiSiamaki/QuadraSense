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
/// A data store that does not answer in time is a 504 that says so, not an opaque 500.
/// </summary>
/// <remarks>
/// On 2026-10-19 a filtered dashboard KPI ran 183 s, the API's ClickHouse client gave up at 180 s,
/// and the browser saw "500 Internal Server Error" and retried it. The store here throws what the
/// driver throws then: a TaskCanceledException around a TimeoutException.
/// </remarks>
public sealed class StoreTimeoutResponseTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private Exception _failure = new TaskCanceledException(
        "The request was canceled due to the configured HttpClient.Timeout of 180 seconds elapsing.",
        new TimeoutException("The operation was canceled."));

    public StoreTimeoutResponseTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
        {
            services.AddSingleton(TestStubs.Create<ISessionStore>((method, _) => method.Name switch
            {
                "ResolveAsync" => Task.FromResult<ResolvedSession?>(new ResolvedSession(
                    Guid.NewGuid(),
                    new AuthenticatedUser(4, "analyst", "Analyst", false,
                        new[] { Permissions.DashboardView }.ToFrozenSet(StringComparer.Ordinal)),
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(1))),
                "TouchAsync" => Task.CompletedTask,
                "DeleteExpiredAsync" => Task.FromResult(0),
                _ => throw new NotSupportedException(method.Name),
            }));
            services.AddSingleton(TestStubs.Create<IAuditLog>((_, _) => Task.CompletedTask));
            services.AddSingleton(TestStubs.Create<IDeviceAnalyticsStore>((method, _) => method.Name switch
            {
                "GetKpiSummaryAsync" => Task.FromException<KpiSummary>(_failure),
                _ => throw new NotSupportedException(method.Name),
            }));
        }));

    private async Task<(HttpStatusCode Status, string Body)> KpiAsync()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/dashboard/kpi?deviceType=Smartphone");
        request.Headers.Add("Cookie", "sqm_session=analyst");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_store_timeout_is_a_504_that_names_it()
    {
        var (status, body) = await KpiAsync();

        Assert.Equal(HttpStatusCode.GatewayTimeout, status);
        Assert.Contains("did not answer in time", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Any_other_failure_stays_a_500()
    {
        _failure = new InvalidOperationException("something else");

        var (status, _) = await KpiAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, status);
    }
}
