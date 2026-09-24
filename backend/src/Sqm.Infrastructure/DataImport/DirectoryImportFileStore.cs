using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sqm.Application.DataImport;

namespace Sqm.Infrastructure.DataImport;

/// <summary>Where original files are kept and how the server sees them.</summary>
public sealed class ImportStorageOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "ImportStorage";

    /// <summary>Absolute path to the storage root. Must be outside the web root.</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>
    /// The same directory as the analytics server sees it, when it can see it at all.
    /// </summary>
    /// <remarks>
    /// ClickHouse reading a file off its own disk is an order of magnitude faster than the same
    /// bytes streamed through a client process. In development the server is a container and the
    /// storage root is bind-mounted, so the two paths differ; leaving this empty simply means the
    /// worker streams instead.
    /// </remarks>
    public string? ServerPathPrefix { get; set; }

    /// <summary>Largest file the store will accept, in bytes.</summary>
    /// <remarks>
    /// Enforced while writing rather than from a declared content length, which a client controls
    /// and can lie about. The default is 10 GB: the largest observed daily file is ~1.1 GB and the
    /// TAC snapshot ~90 MB, so this is generous without being unbounded.
    /// </remarks>
    public long MaxFileBytes { get; set; } = 10L * 1024 * 1024 * 1024;
}

/// <summary>
/// Stores uploaded files in a directory tree on the server (decision D2).
/// </summary>
/// <remarks>
/// <para>
/// The client-supplied file name is recorded as metadata and never used to build a path. The
/// stored name is generated here from the source code, a UTC timestamp and a random suffix, which
/// removes path traversal as a category of bug rather than defending against it with validation.
/// </para>
/// <para>
/// Files land in a temporary name and are moved into place only after the whole stream is written
/// and hashed. A crash mid-upload therefore leaves a stray temp file, not a truncated file that
/// looks complete and imports as a short day.
/// </para>
/// </remarks>
public sealed partial class DirectoryImportFileStore : IImportFileStore
{
    [LoggerMessage(EventId = 3100, Level = LogLevel.Information,
        Message = "Stored {Bytes} bytes at {StoredPath} (sha256 {Sha256})")]
    private partial void LogStored(long bytes, string storedPath, string sha256);

    private readonly ImportStorageOptions _options;
    private readonly string _root;
    private readonly ILogger<DirectoryImportFileStore> _logger;

    public DirectoryImportFileStore(
        IOptions<ImportStorageOptions> options, ILogger<DirectoryImportFileStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_options.RootPath))
        {
            throw new InvalidOperationException(
                $"{ImportStorageOptions.SectionName}:RootPath is not configured.");
        }

        _root = Path.GetFullPath(_options.RootPath);
        Directory.CreateDirectory(_root);
    }

    public async Task<StoredFile> SaveAsync(
        string sourceCode, string originalFileName, Stream content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);

        var relativeDirectory = Path.Combine(
            sourceCode, DateTime.UtcNow.ToString("yyyy/MM", CultureInfo.InvariantCulture));
        var absoluteDirectory = Path.Combine(_root, relativeDirectory);
        Directory.CreateDirectory(absoluteDirectory);

        var extension = SafeExtension(originalFileName);
        var storedName = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}-{Guid.NewGuid():N}{extension}");

        var temporaryPath = Path.Combine(absoluteDirectory, storedName + ".partial");
        var finalPath = Path.Combine(absoluteDirectory, storedName);

        long written;
        byte[] hash;

        try
        {
            await using var destination = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 1 << 20, useAsync: true);

            // Hashing while writing, in one pass. Reading the file back to hash it would double
            // the I/O on a gigabyte file and, worse, would hash whatever is on disk afterwards
            // rather than what was actually received.
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 20];
            written = 0;

            while (true)
            {
                var read = await content.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                written += read;

                if (written > _options.MaxFileBytes)
                {
                    throw new InvalidOperationException(
                        $"file exceeds the {_options.MaxFileBytes:N0}-byte limit");
                }

                hasher.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            hash = hasher.GetHashAndReset();
            await destination.FlushAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }

        if (written == 0)
        {
            TryDelete(temporaryPath);
            throw new InvalidOperationException("the uploaded file was empty");
        }

        File.Move(temporaryPath, finalPath, overwrite: false);

        // Read-only once written. The platform treats an imported file as evidence of what was
        // delivered, and evidence that can be edited in place is not evidence.
        File.SetAttributes(finalPath, FileAttributes.ReadOnly);

        var relativePath = Path.Combine(relativeDirectory, storedName).Replace('\\', '/');
        var sha256 = Convert.ToHexStringLower(hash);

        LogStored(written, relativePath, sha256);
        return new StoredFile(relativePath, written, sha256);
    }

    public Task<Stream> OpenReadAsync(string storedPath, CancellationToken ct)
    {
        Stream stream = new FileStream(
            Resolve(storedPath), FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, useAsync: true);

        return Task.FromResult(stream);
    }

    public Task<bool> ExistsAsync(string storedPath, CancellationToken ct) =>
        Task.FromResult(File.Exists(Resolve(storedPath)));

    public Task DeleteAsync(string storedPath, CancellationToken ct)
    {
        var path = Resolve(storedPath);
        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public string? ResolveServerReadablePath(string storedPath)
    {
        if (string.IsNullOrWhiteSpace(_options.ServerPathPrefix))
        {
            return null;
        }

        return _options.ServerPathPrefix.TrimEnd('/') + "/" + storedPath.Replace('\\', '/');
    }

    /// <summary>
    /// Turns a stored path into an absolute one, refusing anything that escapes the root.
    /// </summary>
    /// <remarks>
    /// Stored paths are generated by this class and come from the database, so a traversal
    /// attempt would mean the database itself had been tampered with. The check stays anyway: it
    /// is one comparison, and it turns "an attacker with write access to one table can read any
    /// file on the host" into "an attacker with write access to one table cannot".
    /// </remarks>
    private string Resolve(string storedPath)
    {
        var full = Path.GetFullPath(Path.Combine(_root, storedPath));

        // With the separator: a bare prefix test let "/data/imports-evil" pass as inside
        // "/data/imports".
        var inside = Path.EndsInDirectorySeparator(_root) ? _root : _root + Path.DirectorySeparatorChar;

        if (!full.StartsWith(inside, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"stored path escapes the storage root: {storedPath}");
        }

        return full;
    }

    /// <summary>
    /// Keeps a recognisable extension without letting the client name any part of the path.
    /// </summary>
    private static string SafeExtension(string originalFileName)
    {
        var extension = Path.GetExtension(originalFileName);

        if (extension.Length is 0 or > 12)
        {
            return ".dat";
        }

        foreach (var c in extension.AsSpan(1))
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return ".dat";
            }
        }

        return extension.ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A temp file we could not remove is litter, not a failure worth masking the real
            // exception with.
        }
    }
}
