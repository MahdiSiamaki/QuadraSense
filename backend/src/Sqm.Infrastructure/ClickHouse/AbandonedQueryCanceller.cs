using System.Text;
using System.Text.RegularExpressions;
using System.Web;

namespace Sqm.Infrastructure.ClickHouse;

/// <summary>
/// Stops a ClickHouse query whose caller stopped waiting for it.
/// </summary>
/// <remarks>
/// <para>
/// ClickHouse finishes an HTTP query whose client has gone. It notices a closed connection only
/// when it writes to it, and an aggregate writes nothing until it is done - so a dashboard read the
/// API abandoned at its 180 s timeout ran on for 199 s on 2026-10-19 while the browser's retry
/// started a second copy beside it, each abandoned read slowing the next. Measured the same day:
/// <c>cancel_http_readonly_queries_on_client_close</c> alone did not stop a query whose client was
/// killed; it ran to its 120 s limit. Progress headers do make ClickHouse notice, but they commit
/// the 200 status before the query has finished, and a later error then arrives inside a 200 body
/// the driver reads as data.
/// </para>
/// <para>
/// So the API names every query it sends (<c>query_id</c>, unless the caller already did) and,
/// when the request is cancelled - the browser left, the lens changed, the client timeout fired -
/// asks the server to kill that query by name. Fire and forget, bounded to a few seconds: a kill
/// that fails leaves things as they were before this existed, never worse.
/// </para>
/// </remarks>
public sealed partial class AbandonedQueryCanceller : DelegatingHandler
{
    /// <summary>How long the kill request may take before it is given up.</summary>
    public static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The query ids this handler kills: generated GUIDs, or a caller's of the same shape.</summary>
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SafeQueryId();

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queryId = NameQuery(request);

        try
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (queryId is not null)
        {
            _ = KillAsync(request, queryId);
            throw;
        }
    }

    /// <summary>
    /// The request's query id, adding one when it has none. Null for anything that is not a query
    /// sent to the root endpoint (a ping, a caller's id this handler would not put into SQL).
    /// </summary>
    internal static string? NameQuery(HttpRequestMessage request)
    {
        var uri = request.RequestUri;
        if (uri is null || uri.AbsolutePath != "/")
        {
            return null;
        }

        var query = HttpUtility.ParseQueryString(uri.Query);
        var existing = query["query_id"];
        if (existing is not null)
        {
            return SafeQueryId().IsMatch(existing) ? existing : null;
        }

        var id = Guid.NewGuid().ToString("N");
        query["query_id"] = id;
        request.RequestUri = new UriBuilder(uri) { Query = query.ToString() }.Uri;
        return id;
    }

    /// <summary>The kill request: the same server and user, the query's own parameters left out.</summary>
    internal static HttpRequestMessage KillRequest(HttpRequestMessage original, string queryId)
    {
        var source = HttpUtility.ParseQueryString(original.RequestUri!.Query);
        var query = HttpUtility.ParseQueryString(string.Empty);

        // Who and where, nothing about the query being killed: its id would collide with the one
        // still running, and its parameters and settings mean nothing to KILL.
        foreach (var key in new[] { "user", "password", "database" })
        {
            if (source[key] is { } value)
            {
                query[key] = value;
            }
        }

        var kill = new HttpRequestMessage(HttpMethod.Post, new UriBuilder(original.RequestUri!) { Query = query.ToString() }.Uri)
        {
            Content = new StringContent(
                $"KILL QUERY WHERE query_id = '{queryId}' ASYNC", Encoding.UTF8, "text/plain"),
        };

        kill.Headers.Authorization = original.Headers.Authorization;
        foreach (var header in new[] { "X-ClickHouse-User", "X-ClickHouse-Key" })
        {
            if (original.Headers.TryGetValues(header, out var values))
            {
                kill.Headers.TryAddWithoutValidation(header, values);
            }
        }

        return kill;
    }

    private async Task KillAsync(HttpRequestMessage original, string queryId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(KillTimeout);
            using var kill = KillRequest(original, queryId);
            using var response = await base.SendAsync(kill, timeout.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best effort by design: a failed kill leaves the query as it was.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Nothing to do: the query runs to its own limit, as every query did before this.
        }
    }
}
