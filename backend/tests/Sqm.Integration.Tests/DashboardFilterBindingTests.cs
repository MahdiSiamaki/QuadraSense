using System.Reflection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Sqm.Integration.Tests;

/// <summary>
/// Every dashboard widget sends the whole filter set, so every dashboard endpoint must accept it.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of a bug that produced no error anywhere. The dashboard holds one filter
/// object and spreads it into every request; <c>/distribution/{dimension}</c> declared only
/// <c>manufacturer</c> and <c>vendor</c>, and a minimal API does not bind a query parameter it has
/// not declared. So drilling into an operating system left the Device types card answering the
/// unfiltered question while the page above it displayed a chip saying "Filtered by OS".
/// </para>
/// <para>
/// Nothing failed. The card returned a perfectly good answer to a question nobody had asked, which
/// is the failure mode this whole product is built to avoid, and it is invisible in review because
/// the defect is an <em>absence</em> - a parameter that is not there.
/// </para>
/// <para>
/// Asserted against the router's own endpoint list and the handlers' real signatures, so adding a
/// dashboard endpoint that quietly ignores a filter fails the build.
/// </para>
/// </remarks>
public sealed class DashboardFilterBindingTests : IClassFixture<WebApplicationFactory<Program>>
{
    /// <summary>
    /// The filter the dashboard sends. These are the names on the wire, from
    /// <c>DashboardFilters</c> in <c>frontend/src/api/dashboard.ts</c>.
    /// </summary>
    private static readonly string[] DashboardFilterParameters =
    [
        "manufacturer", "vendor", "deviceType", "operatingSystem", "tac", "msisdnPrefix",
        "includeUnknownDevice",
    ];

    /// <summary>
    /// The endpoints the dashboard spreads its filter object into.
    /// </summary>
    /// <remarks>
    /// Not every route under /dashboard takes filters - the daily series and the vendor widget
    /// have their own shapes - so the list is explicit rather than "everything in the group".
    /// </remarks>
    private static readonly string[] FilteredRoutes =
    [
        "/api/v1/dashboard/kpi",
        "/api/v1/dashboard/top/{dimension}",
        "/api/v1/dashboard/distribution/{dimension}",
    ];

    private readonly WebApplicationFactory<Program> _factory;

    public DashboardFilterBindingTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public void Every_filtered_dashboard_endpoint_binds_every_filter_the_dashboard_sends()
    {
        var endpoints = _factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .ToList();

        Assert.NotEmpty(endpoints);

        var failures = new List<string>();

        foreach (var route in FilteredRoutes)
        {
            var endpoint = endpoints.SingleOrDefault(
                e => string.Equals(e.RoutePattern.RawText, route, StringComparison.Ordinal));

            Assert.True(endpoint is not null, $"No endpoint is mapped at '{route}'.");

            var handler = endpoint!.Metadata.GetMetadata<MethodInfo>();
            Assert.True(handler is not null, $"'{route}' exposes no handler MethodInfo.");

            var declared = handler!.GetParameters()
                .Select(p => p.Name)
                .Where(n => n is not null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var filter in DashboardFilterParameters)
            {
                if (!declared.Contains(filter))
                {
                    failures.Add($"{route} does not bind '{filter}'");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "A dashboard endpoint silently ignores a filter the dashboard sends it. The request "
            + "succeeds and the answer describes a different population than the page says it "
            + "does:\n  " + string.Join("\n  ", failures));
    }
}
