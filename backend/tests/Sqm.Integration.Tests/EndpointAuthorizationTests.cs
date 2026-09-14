using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Sqm.Api.Auth;
using Sqm.Application.Identity;

namespace Sqm.Integration.Tests;

/// <summary>
/// Every route the API exposes is either authorised or deliberately anonymous.
/// </summary>
/// <remarks>
/// <para>
/// This is the test that answers the requirement directly: do not merely hide permissions in the
/// frontend. It enumerates the application's own <see cref="EndpointDataSource"/> - the same list
/// the router serves from, not a list maintained by hand - and fails on anything unprotected that
/// is not in a short, named allow-list.
/// </para>
/// <para>
/// So adding an endpoint and forgetting to protect it fails the build. There is no version of
/// this that a code review reliably catches: the mistake is an absence, and absences are what
/// reviews miss.
/// </para>
/// </remarks>
public sealed class EndpointAuthorizationTests : IClassFixture<WebApplicationFactory<Program>>
{
    /// <summary>
    /// Routes that are anonymous on purpose, each with the reason it has to be.
    /// </summary>
    /// <remarks>
    /// Kept deliberately small and written out in full. Every addition here is a decision to
    /// expose something without a session, and it should be as awkward to make as it is easy to
    /// review.
    /// </remarks>
    private static readonly Dictionary<string, string> IntentionallyAnonymous = new(StringComparer.Ordinal)
    {
        ["/health/live"] = "liveness must answer while the database is down, or an orchestrator "
                           + "kills a process whose only problem is a dependency",
        ["/health/ready"] = "readiness is polled by the orchestrator, which has no account",
        ["/api/v1/auth/login"] = "the endpoint that issues the session cannot require one",
        ["/openapi/{documentName}.json"] = "development only; not mapped outside Development",
    };

    private readonly WebApplicationFactory<Program> _factory;

    public EndpointAuthorizationTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public void Every_endpoint_requires_authorisation_or_is_on_the_anonymous_list()
    {
        var endpoints = _factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .ToList();

        Assert.NotEmpty(endpoints);

        var unprotected = new List<string>();

        foreach (var endpoint in endpoints)
        {
            var pattern = endpoint.RoutePattern.RawText ?? "(no pattern)";

            var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            var authorised = endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null;

            if (anonymous)
            {
                // An AllowAnonymous route must be one we meant to expose. A new one appearing
                // here is the same mistake as a missing RequireAuthorization, just spelled out.
                Assert.True(
                    IntentionallyAnonymous.ContainsKey(pattern),
                    $"{pattern} allows anonymous access but is not on the reviewed list. "
                    + "If that is intended, add it to IntentionallyAnonymous with the reason.");
                continue;
            }

            if (!authorised)
            {
                // Not necessarily a hole - the fallback policy still applies - but it means the
                // route requires only a session, which for this API is almost never right.
                unprotected.Add(pattern);
            }
        }

        Assert.True(
            unprotected.Count == 0,
            "These endpoints declare no authorisation. The fallback policy still requires a "
            + "session, but a data endpoint should name the permission it needs:\n  "
            + string.Join("\n  ", unprotected));
    }

    [Fact]
    public async Task The_fallback_policy_is_not_null_so_an_unmarked_endpoint_is_still_closed()
    {
        var provider = _factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        var fallback = await provider.GetFallbackPolicyAsync();

        // This is what turns "forgot to protect it" from an open door into a 401. Without it,
        // ASP.NET applies no policy at all to an endpoint with no authorisation metadata.
        Assert.NotNull(fallback);
        Assert.Contains(fallback.Requirements, r => r is PermissionRequirement);
    }

    [Fact]
    public void Every_permission_policy_an_endpoint_names_is_a_real_permission()
    {
        var endpoints = _factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints;

        var named = endpoints
            .SelectMany(e => e.Metadata.OfType<IAuthorizeData>())
            .Select(a => a.Policy)
            .Where(p => p is not null && p.StartsWith(PermissionPolicyProvider.Prefix, StringComparison.Ordinal))
            .Select(p => p![PermissionPolicyProvider.Prefix.Length..])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(named);

        // A typo in a permission name produces a policy nobody can satisfy, which on screen is
        // indistinguishable from a correctly denied request.
        var unknown = named.Where(p => !Permissions.All.Contains(p)).ToList();
        Assert.Empty(unknown);
    }

    [Fact]
    public void The_endpoints_that_touch_subscriber_data_name_the_permission_that_guards_it()
    {
        var endpoints = _factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .ToList();

        // Named explicitly rather than discovered, because this is the boundary the whole role
        // split exists to draw: the route that resolves an individual must never be reachable
        // with dashboard access alone.
        AssertRequires(endpoints, "/api/v1/lookup/msisdn", Permissions.LookupSubscriber);
        AssertRequires(endpoints, "/api/v1/users/", Permissions.UserView, Permissions.UserManage);
        AssertRequires(endpoints, "/api/v1/audit/", Permissions.AuditView);
        AssertRequires(endpoints, "/api/v1/tac-versions/{id:long}/activate", Permissions.TacActivate);
    }

    private static void AssertRequires(
        IReadOnlyList<RouteEndpoint> endpoints, string pattern, params string[] anyOf)
    {
        var matching = endpoints
            .Where(e => string.Equals(e.RoutePattern.RawText, pattern, StringComparison.Ordinal))
            .ToList();

        Assert.True(matching.Count > 0, $"no endpoint matched {pattern}");

        foreach (var endpoint in matching)
        {
            var policies = endpoint.Metadata.OfType<IAuthorizeData>()
                .Select(a => a.Policy)
                .Where(p => p is not null)
                .ToList();

            Assert.True(
                anyOf.Any(code => policies.Contains(
                    PermissionPolicyProvider.Prefix + code, StringComparer.Ordinal)),
                string.Create(CultureInfo.InvariantCulture,
                    $"{pattern} does not require any of [{string.Join(", ", anyOf)}]; "
                    + $"it has [{string.Join(", ", policies)}]"));
        }
    }
}
