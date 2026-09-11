namespace Sqm.Api.Infrastructure;

/// <summary>
/// Assigns a correlation ID to every request and puts it on the log scope and the response.
/// </summary>
/// <remarks>
/// This is what makes a user-facing error actionable. The client is shown a short ID and never a stack
/// trace; support pastes that ID into the log search and gets the whole request, including which store
/// was queried and how long it took.
/// </remarks>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    /// <summary>Header carrying the correlation ID in and out.</summary>
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Accept a caller-supplied ID so a trace can span services, but never trust its shape.
        var incoming = context.Request.Headers[HeaderName].FirstOrDefault();
        var correlationId = IsSafe(incoming) ? incoming! : Guid.NewGuid().ToString("N");

        context.Items[HeaderName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["correlationId"] = correlationId,
            ["requestId"] = context.TraceIdentifier,
        });

        await next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Guards against header injection and unbounded log fields: a correlation ID is an opaque token,
    /// so anything that is not short and alphanumeric is replaced rather than sanitised.
    /// </summary>
    private static bool IsSafe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64) return false;

        foreach (var c in value)
        {
            var ok = c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '-';
            if (!ok) return false;
        }
        return true;
    }
}
