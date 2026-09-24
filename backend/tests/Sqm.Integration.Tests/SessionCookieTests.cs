using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sqm.Application.Identity;

namespace Sqm.Integration.Tests;

/// <summary>
/// A dead session cookie is removed in a way the browser actually honours.
/// </summary>
/// <remarks>
/// <para>
/// In production the cookie is <c>__Host-sqm_session</c>, and a browser ignores any Set-Cookie
/// for a <c>__Host-</c> name that lacks <c>Secure</c> - a deletion included. The handler used to
/// delete it with default options, so the dead cookie stayed. After the 24-hour absolute limit it
/// outlived the CSRF cookie, and the CSRF check, which fires whenever a session cookie is present,
/// refused the sign-in POST itself: nobody could sign back in until the browser was restarted.
/// </para>
/// <para>
/// Needs no database - the session store is replaced by one that knows no sessions - so this runs
/// in a cloud session and in CI as well as on the laptop.
/// </para>
/// </remarks>
public sealed class SessionCookieTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SessionCookieTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(host =>
        {
            // Production's setting. Development turns it off, which also drops the prefix and
            // with it the whole problem - so the default test host would pass either way.
            host.UseSetting("Auth:Session:RequireSecureCookies", "true");
            host.ConfigureTestServices(services =>
                services.AddSingleton<ISessionStore, NoSessions>());
        });

    [Fact]
    public async Task A_dead_session_cookie_is_deleted_with_the_attributes_its_prefix_requires()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        request.Headers.Add("Cookie", "__Host-sqm_session=expired-token");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);

        var deletion = Assert.Single(
            response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [],
            v => v.StartsWith("__Host-sqm_session=", StringComparison.Ordinal));

        var attributes = deletion.Split(';', StringSplitOptions.TrimEntries)
            .Select(a => a.ToLowerInvariant())
            .ToList();

        Assert.Contains("secure", attributes);
        Assert.Contains("path=/", attributes);
        Assert.Contains(attributes, a => a.StartsWith("expires=thu, 01 jan 1970", StringComparison.Ordinal));
    }

    /// <summary>A store in which every session has ended.</summary>
    private sealed class NoSessions : ISessionStore
    {
        public Task<ResolvedSession?> ResolveAsync(string token, CancellationToken ct) =>
            Task.FromResult<ResolvedSession?>(null);

        public Task<int> DeleteExpiredAsync(TimeSpan retainEndedFor, CancellationToken ct) =>
            Task.FromResult(0);

        public Task<SessionCreation> CreateAsync(
            long userId, string? ip, string? userAgent, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task TouchAsync(Guid sessionId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> RevokeAsync(
            Guid sessionId, string revokedBy, string reason, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<int> RevokeAllForUserAsync(
            long userId, Guid? exceptSessionId, string revokedBy, string reason,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SessionInfo>> ListForUserAsync(
            long userId, Guid? currentSessionId, int limit, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
