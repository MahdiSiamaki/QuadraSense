namespace Sqm.Application.DataImport;

/// <summary>Where uploaded files live.</summary>
/// <remarks>
/// <para>
/// The chosen implementation writes to a directory on the server (decision D2): one worker reads
/// files on the machine they landed on, and object storage earns its keep when compute and
/// storage are separate or many services need the same blobs, neither of which is true here.
/// </para>
/// <para>
/// It stays behind this interface anyway, because that assumption has a shelf life. When a second
/// worker on a second machine appears, moving to object storage should be an implementation swap,
/// not a rewrite - and the interface is small enough (four members) that the abstraction costs
/// almost nothing to carry until then.
/// </para>
/// </remarks>
public interface IImportFileStore
{
    /// <summary>
    /// Writes a file and returns where it went, together with what it actually contained.
    /// </summary>
    /// <param name="sourceCode">Data source, used to lay out storage.</param>
    /// <param name="originalFileName">The client-supplied name. Recorded, never used as a path.</param>
    /// <param name="content">The bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// The hash is computed here, while streaming, rather than by the caller. Hashing at the point
    /// of writing means the recorded hash describes the bytes that were actually stored, not the
    /// bytes someone intended to store.
    /// </remarks>
    Task<StoredFile> SaveAsync(
        string sourceCode, string originalFileName, Stream content, CancellationToken ct);

    /// <summary>Opens a stored file for reading.</summary>
    Task<Stream> OpenReadAsync(string storedPath, CancellationToken ct);

    /// <summary>Whether the blob is still there.</summary>
    Task<bool> ExistsAsync(string storedPath, CancellationToken ct);

    /// <summary>
    /// Removes a blob. The metadata row survives, so history stays explainable after the bytes
    /// are gone.
    /// </summary>
    Task DeleteAsync(string storedPath, CancellationToken ct);

    /// <summary>
    /// A path the ClickHouse server can read the same file by, or <see langword="null"/> when the
    /// store cannot offer one.
    /// </summary>
    /// <remarks>
    /// Bulk loading is an order of magnitude faster when ClickHouse reads the file off its own
    /// disk than when the same bytes are streamed through a client process. That only works when
    /// the server can see the storage, which is exactly the coupling this method exposes rather
    /// than hides: a caller that gets <see langword="null"/> has to fall back to streaming, and
    /// the difference in throughput is visible rather than mysterious.
    /// </remarks>
    string? ResolveServerReadablePath(string storedPath);
}

/// <summary>The result of storing a file.</summary>
/// <param name="StoredPath">Where it went, relative to the storage root.</param>
/// <param name="SizeBytes">How many bytes were written.</param>
/// <param name="Sha256">Hex-encoded SHA-256 of the content.</param>
public sealed record StoredFile(string StoredPath, long SizeBytes, string Sha256);
