using System.IO.Compression;
using Sqm.Application.DataImport;

namespace Sqm.Ingestion;

/// <summary>
/// Hands a file already on the server to the Import Center, as an upload would: the same file
/// store, the same job, the same queue and processing.
/// </summary>
/// <remarks>
/// <para>
/// <c>--import-file &lt;file&gt; [--source SQM|TAC]</c>. Uploads through the department's VPN broke at
/// every drop: a 370 MB day needs ~45 minutes at the ~130 KB/s measured on 2026-10-25, a browser
/// upload cannot resume, and two of two attempts broke at 12.6 and 16.5 minutes, each in the minute
/// the tunnel dropped. A file carried to the server in pieces survives a drop; this queues it.
/// </para>
/// <para>
/// A <c>.gz</c> is decompressed as it is stored, under its name without <c>.gz</c>. So what is
/// stored, and its hash, are exactly what an upload of the same CSV gives: a file sent both ways is
/// recognised as one. The rules after storing are <see cref="ImportIntake"/>'s, shared with the
/// upload endpoint. Who may run it is whoever holds the server - the endpoint's permission check
/// has no user here, and the server's shell is already beyond it.
/// </para>
/// </remarks>
internal static class ImportFile
{
    /// <summary>Who history says sent it.</summary>
    internal const string Actor = "operator (--import-file)";

    /// <summary>Stores and queues the file; returns a process exit code.</summary>
    /// <returns>0 queued, or already imported; 2 no such file or an unknown source.</returns>
    public static async Task<int> RunAsync(
        IImportJobRepository repository, IImportFileStore files, string sourceCode, string path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(path);

        var source = sourceCode.ToUpperInvariant();
        if (source is not ("SQM" or "TAC"))
        {
            Console.Error.WriteLine($"unknown source '{sourceCode}': SQM or TAC");
            return 2;
        }

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"no such file: {path}");
            return 2;
        }

        var compressed = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
        var name = compressed ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path);

        Console.WriteLine($"storing {name} in the Import Center's file store{(compressed ? " (decompressing)" : string.Empty)}");
        StoredFile stored;
        await using (var file = File.OpenRead(path))
        await using (var content = compressed ? new GZipStream(file, CompressionMode.Decompress) : (Stream)file)
        {
            stored = await files.SaveAsync(source, name, content, ct).ConfigureAwait(false);
        }

        var intake = await ImportIntake
            .AcceptAsync(repository, files, source, name, stored, Actor, null, ct)
            .ConfigureAwait(false);

        Console.WriteLine(intake.QueuedJobId is { } jobId
            ? $"  queued as import {jobId} ({stored.SizeBytes:N0} bytes, sha256 {stored.Sha256})"
            : $"  already imported as import {intake.File.ExistingJobId}; nothing queued");
        return 0;
    }
}
