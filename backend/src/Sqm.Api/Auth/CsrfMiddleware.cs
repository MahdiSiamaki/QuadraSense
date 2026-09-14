using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Sqm.Application.Identity;

namespace Sqm.Api.Auth;

/// <summary>Double-submit CSRF protection for cookie-authenticated requests.</summary>
/// <remarks>
/// <para>
/// <c>SameSite=Strict</c> on the session cookie already stops a cross-site form post from
/// carrying it, and CORS is restricted to the known frontend origin. This is the second layer,
/// and it is here because the first has edges: a browser that does not enforce SameSite, and a
/// same-site subdomain an attacker controls - which is the realistic case inside a corporate
/// domain where anyone can be given a hostname.
/// </para>
/// <para>
/// How it works: on sign-in the server sets a random value in a cookie the SPA CAN read, and the
/// SPA echoes it in a header on every state-changing request. A cross-site attacker can cause the
/// browser to SEND the cookie but cannot READ it, so cannot produce the header.
/// </para>
/// <para>
/// Only requests carrying a session cookie are checked. An anonymous request has nothing to
/// forge, and the login endpoint cannot require a token it has not issued yet.
/// </para>
/// </remarks>
public sealed class CsrfMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SessionSettings _options;

    /// <summary>Creates the middleware.</summary>
    public CsrfMiddleware(RequestDelegate next, IOptions<AuthOptions> auth)
    {
        ArgumentNullException.ThrowIfNull(auth);
        _next = next;
        _options = auth.Value.Session;
    }

    /// <summary>Runs the check.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (RequiresToken(context))
        {
            var cookie = context.Request.Cookies[CookieNames.Csrf(_options)];
            var header = context.Request.Headers[_options.CsrfHeaderName].ToString();

            if (string.IsNullOrEmpty(cookie) || !FixedTimeEquals(cookie, header))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    title = "CSRF token missing or invalid",
                    status = 403,
                    detail = $"State-changing requests must echo the {CookieNames.Csrf(_options)} "
                        + $"cookie in the {_options.CsrfHeaderName} header.",
                }, context.RequestAborted).ConfigureAwait(false);

                return;
            }
        }

        await _next(context).ConfigureAwait(false);
    }

    /// <summary>Issues a fresh CSRF cookie. Called when a session is created.</summary>
    /// <remarks>
    /// Not <c>HttpOnly</c> - the SPA has to read it, and that is not a weakness: an attacker who
    /// can run script in this origin already has the session cookie's privileges through the
    /// browser and does not need to steal anything.
    /// </remarks>
    public static string IssueToken(HttpContext context, SessionSettings options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        context.Response.Cookies.Append(CookieNames.Csrf(options), token, new CookieOptions
        {
            HttpOnly = false,
            Secure = options.RequireSecureCookies,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            MaxAge = options.AbsoluteTimeout,
        });

        return token;
    }

    /// <summary>Removes the CSRF cookie. Called on sign-out.</summary>
    public static void ClearToken(HttpContext context, SessionSettings options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        context.Response.Cookies.Delete(CookieNames.Csrf(options), new CookieOptions
        {
            Secure = options.RequireSecureCookies,
            SameSite = SameSiteMode.Strict,
            Path = "/",
        });
    }

    private bool RequiresToken(HttpContext context)
    {
        // Safe methods do not change state, and CORS already prevents a cross-site page from
        // reading their responses.
        if (HttpMethods.IsGet(context.Request.Method)
            || HttpMethods.IsHead(context.Request.Method)
            || HttpMethods.IsOptions(context.Request.Method)
            || HttpMethods.IsTrace(context.Request.Method))
        {
            return false;
        }

        // No session cookie, nothing to forge.
        return context.Request.Cookies.ContainsKey(CookieNames.Session(_options));
    }

    /// <summary>Compares two tokens without leaking their contents through timing.</summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        var left = Encoding.UTF8.GetBytes(a);
        var right = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(left, right);
    }
}
