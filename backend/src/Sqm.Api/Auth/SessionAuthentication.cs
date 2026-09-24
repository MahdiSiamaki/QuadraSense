using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Sqm.Application.Identity;

namespace Sqm.Api.Auth;

/// <summary>Resolves the session cookie into a principal on every request.</summary>
/// <remarks>
/// <para>
/// An <see cref="AuthenticationHandler{TOptions}"/> rather than middleware, so that
/// <c>[Authorize]</c>, the authorisation pipeline and <c>HttpContext.User</c> all behave the way
/// the rest of ASP.NET Core expects. Bespoke middleware here would work and would then surprise
/// the next person who adds an endpoint.
/// </para>
/// <para>
/// The resolved session is put in <c>HttpContext.Items</c> as well as being projected into
/// claims. The claims exist so that <c>User.Identity.Name</c> works for code that only needs the
/// username - the import endpoints already read it - and the item exists so that authorisation
/// can test a <see cref="System.Collections.Frozen.FrozenSet{T}"/> rather than scan a claims
/// collection on every check.
/// </para>
/// </remarks>
public sealed class SessionAuthenticationHandler
    : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The scheme name.</summary>
    public const string SchemeName = "SqmSession";

    /// <summary>Key under which the resolved session is stored in <c>HttpContext.Items</c>.</summary>
    public const string SessionItemKey = "sqm.session";

    private readonly ISessionStore _sessions;
    private readonly SessionSettings _options;

    /// <summary>Creates the handler.</summary>
    public SessionAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ISessionStore sessions,
        IOptions<AuthOptions> auth)
        : base(schemeOptions, logger, encoder)
    {
        ArgumentNullException.ThrowIfNull(auth);
        _sessions = sessions;
        _options = auth.Value.Session;
    }

    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = Request.Cookies[CookieNames.Session(_options)];

        if (string.IsNullOrEmpty(token))
        {
            // NoResult, not Fail. "No cookie" is an anonymous request, not a broken one, and
            // returning Fail turns every unauthenticated call into a logged error.
            return AuthenticateResult.NoResult();
        }

        var session = await _sessions.ResolveAsync(token, Context.RequestAborted)
            .ConfigureAwait(false);

        if (session is null)
        {
            // Unknown, expired, revoked, or the account was deactivated - the store does not say
            // which, and neither does this. Clearing the cookie stops the browser re-sending a
            // dead value on every subsequent request.
            CookieNames.ClearSession(Response, _options);
            return AuthenticateResult.NoResult();
        }

        Context.Items[SessionItemKey] = session;

        // Roll the idle window forward, but only when it has actually gone stale. Writing on
        // every request would turn every GET into a write transaction for no behavioural gain;
        // the window only needs to be accurate to within the touch interval.
        if (session.LastSeenAt < TimeProvider.System.GetUtcNow() - _options.TouchInterval)
        {
            await _sessions.TouchAsync(session.SessionId, Context.RequestAborted)
                .ConfigureAwait(false);
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier,
                session.User.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Name, session.User.Username),
            new Claim("display_name", session.User.DisplayName),
        ], SchemeName);

        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    /// <summary>
    /// Returns 401 with no <c>WWW-Authenticate</c> header and no redirect.
    /// </summary>
    /// <remarks>
    /// The default cookie handler redirects to a login page, which for an API means the SPA
    /// receives a 200 containing HTML and reports a JSON parse error. The client decides what to
    /// do with a 401; the server's job is to say 401.
    /// </remarks>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}

/// <summary>Cookie names, with the <c>__Host-</c> prefix dropped when it cannot be honoured.</summary>
/// <remarks>
/// A browser rejects a <c>__Host-</c> cookie outright unless it is Secure - silently, with no
/// error the developer can act on. On a plain-HTTP development origin the prefix is therefore
/// stripped rather than left to produce a login that appears to succeed and then does nothing.
/// </remarks>
public static class CookieNames
{
    private const string HostPrefix = "__Host-";

    /// <summary>Name of the session cookie for the current configuration.</summary>
    public static string Session(SessionSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Adjust(options.CookieName, options.RequireSecureCookies);
    }

    /// <summary>Tells the browser to drop the session cookie.</summary>
    /// <remarks>
    /// A deletion is a Set-Cookie like any other, and a browser applies the prefix rule to it too:
    /// without <c>Secure</c> it is ignored and the dead cookie stays. That once happened here. The
    /// cookie then outlived the CSRF cookie, whose lifetime is the session's 24-hour limit, and
    /// the CSRF check - which fires whenever a session cookie is present - refused every sign-in
    /// with 403 until the browser was restarted.
    /// </remarks>
    public static void ClearSession(HttpResponse response, SessionSettings options)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(options);

        response.Cookies.Delete(Session(options), new CookieOptions
        {
            Secure = options.RequireSecureCookies,
            SameSite = SameSiteMode.Strict,
            Path = "/",
        });
    }

    /// <summary>Name of the CSRF cookie for the current configuration.</summary>
    public static string Csrf(SessionSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Adjust(options.CsrfCookieName, options.RequireSecureCookies);
    }

    private static string Adjust(string name, bool secure) =>
        secure || !name.StartsWith(HostPrefix, StringComparison.Ordinal)
            ? name
            : name[HostPrefix.Length..];
}
