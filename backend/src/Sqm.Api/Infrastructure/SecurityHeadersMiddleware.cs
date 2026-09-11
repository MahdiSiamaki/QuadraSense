namespace Sqm.Api.Infrastructure;

/// <summary>Applies the security headers agreed in <c>docs/architecture/04-security-model.md</c>.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var headers = context.Response.Headers;

        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";

        // The API returns JSON only. A CSP this strict costs nothing here and means a reflected
        // payload cannot execute even if one were ever echoed back.
        headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";

        // Identifiers are never placed in URLs (lookups use POST bodies), but no-referrer removes
        // the whole class of leak rather than relying on that convention holding forever.
        headers["Referrer-Policy"] = "no-referrer";

        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";

        if (context.Request.IsHttps)
        {
            headers["Strict-Transport-Security"] = "max-age=63072000; includeSubDomains";
        }

        await next(context).ConfigureAwait(false);
    }
}
