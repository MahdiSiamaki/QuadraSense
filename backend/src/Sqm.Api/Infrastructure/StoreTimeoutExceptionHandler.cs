using Microsoft.AspNetCore.Diagnostics;

namespace Sqm.Api.Infrastructure;

/// <summary>
/// A store that did not answer in time is a 504 that says so, not an opaque 500.
/// </summary>
/// <remarks>
/// When the API's client to ClickHouse gives up (its <c>HttpClient.Timeout</c>), the driver throws
/// <see cref="TaskCanceledException"/> wrapping a <see cref="TimeoutException"/>. That reached the
/// browser as "500 Internal Server Error" on 2026-10-19 for a filtered dashboard KPI that ran 183 s,
/// and the frontend's retry sent it again. A 504 names the cause, and the frontend does not retry
/// one (lib/queryClient.ts). A request whose own caller left is not answered at all: nobody is
/// there to read it, and the query behind it has already been killed (AbandonedQueryCanceller).
/// </remarks>
internal sealed class StoreTimeoutExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (httpContext.RequestAborted.IsCancellationRequested || !IsStoreTimeout(exception))
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = StatusCodes.Status504GatewayTimeout,
                Title = "The data store did not answer in time",
                Detail = "The query took longer than the API waits for one, and was stopped. Try again "
                    + "later or narrow the view; retrying at once will not be faster.",
            },
        }).ConfigureAwait(false);
    }

    /// <summary>An HTTP client timeout, anywhere in the chain - not a cancellation by the caller.</summary>
    internal static bool IsStoreTimeout(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is TimeoutException)
            {
                return true;
            }
        }

        return false;
    }
}
