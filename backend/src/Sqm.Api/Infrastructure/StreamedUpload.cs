using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Sqm.Api.Infrastructure;

/// <summary>
/// Reads one file out of a multipart request without buffering it first.
/// </summary>
/// <remarks>
/// <para>
/// <c>IFormFile</c> is the obvious way to do this and the wrong one here. ASP.NET Core reads the
/// whole multipart body before the handler runs — to memory below 64 KB and to a temporary file
/// above it — so a 1 GB daily import would be written to disk twice: once by the framework, once
/// by the file store. It also trips the default 128 MB multipart limit, and raising that limit
/// only makes the double write bigger.
/// </para>
/// <para>
/// Reading the sections directly hands the file store a stream that is still arriving over the
/// socket. The bytes go from network to disk once, and the hash is computed on the way past.
/// </para>
/// <para>
/// The cost is that this has to handle the multipart framing itself, which is why it is one
/// small, well-tested helper rather than something each endpoint does.
/// </para>
/// </remarks>
internal static class StreamedUpload
{
    /// <summary>Largest multipart header block accepted, to bound what a client can make us buffer.</summary>
    private const int HeadersLengthLimit = 16 * 1024;

    /// <summary>
    /// Finds the first file section and hands its stream to <paramref name="handle"/>.
    /// </summary>
    /// <returns>
    /// The handler's result, or <see langword="null"/> when the request carried no file section.
    /// </returns>
    public static async Task<T?> ReadFileSectionAsync<T>(
        HttpRequest request,
        Func<string, Stream, CancellationToken, Task<T>> handle,
        CancellationToken ct)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(handle);

        var boundary = GetBoundary(request.ContentType);
        var reader = new MultipartReader(boundary, request.Body)
        {
            HeadersLengthLimit = HeadersLengthLimit,
        };

        while (await reader.ReadNextSectionAsync(ct).ConfigureAwait(false) is { } section)
        {
            if (!ContentDispositionHeaderValue.TryParse(
                    section.ContentDisposition, out var disposition))
            {
                continue;
            }

            if (!disposition.IsFileDisposition())
            {
                continue;
            }

            // The client-supplied name is recorded as metadata and never used to build a path.
            var fileName = HeaderUtilities
                .RemoveQuotes(disposition.FileNameStar.HasValue
                    ? disposition.FileNameStar
                    : disposition.FileName)
                .ToString();

            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = "upload.dat";
            }

            return await handle(fileName, section.Body, ct).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>Whether the request is a multipart form at all.</summary>
    public static bool IsMultipart(HttpRequest request) =>
        request?.ContentType is { } type
        && type.Contains("multipart/", StringComparison.OrdinalIgnoreCase);

    private static string GetBoundary(string? contentType)
    {
        var boundary = HeaderUtilities.RemoveQuotes(
            MediaTypeHeaderValue.Parse(contentType).Boundary).Value;

        if (string.IsNullOrWhiteSpace(boundary))
        {
            throw new InvalidOperationException("the multipart request has no boundary");
        }

        // A boundary is framing, not content. An unbounded one is a cheap way to make a server
        // allocate, so it is capped well above anything a real client sends.
        return boundary.Length > 128
            ? throw new InvalidOperationException("the multipart boundary is implausibly long")
            : boundary;
    }
}
