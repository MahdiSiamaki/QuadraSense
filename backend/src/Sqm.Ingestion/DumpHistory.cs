using System.Globalization;
using System.IO.Compression;
using Sqm.Application.DataImport;

namespace Sqm.Ingestion;

/// <summary>
/// The initial dump in the Import Center: its original in the file store and a completed import in
/// the history, as every uploaded file has.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DumpLoad"/> writes the dump into the analytics store and nothing else, so on the
/// department VM (2026-10-24) the Import Center showed an empty history over 126 million bindings,
/// and the original was kept only where the operator had put it - retention (originals kept for
/// ever, CLAUDE.md) then depended on someone remembering. The first machine shows its dump because
/// <c>db/operational/jobs/backfill_bulk_load_history.py</c> wrote that record after the fact.
/// </para>
/// <para>
/// <c>--load-dump</c> ends with this. <c>--record-dump &lt;file&gt;</c> runs it alone, for a store
/// loaded before it existed - and first checks that the file is the one in the store: its rows must
/// number exactly what <c>binding_snapshot</c> holds.
/// </para>
/// </remarks>
internal static class DumpHistory
{
    /// <summary>Who history says ran it: the worker has no user of its own; an operator ran it.</summary>
    internal const string Actor = "operator (--load-dump)";

    private const string Source = "SQM";

    /// <summary>Stores and records the dump; returns a process exit code.</summary>
    /// <param name="analytics">The store the dump was loaded into.</param>
    /// <param name="repository">The Import Center's history.</param>
    /// <param name="files">The Import Center's file store.</param>
    /// <param name="path">The dump, as it was loaded (a <c>.gz</c> is stored as it is).</param>
    /// <param name="startedAt">When the load began; <see langword="null"/> when nobody kept it.</param>
    /// <param name="finishedAt">When it finished; <see langword="null"/> when nobody kept it.</param>
    /// <param name="verifyRows">Count the file's rows against the store before recording it.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>0 recorded, or already recorded; 1 refused; 2 no such file.</returns>
    public static async Task<int> RecordAsync(
        IAnalyticsIngestionStore analytics,
        IImportJobRepository repository,
        IImportFileStore files,
        string path,
        DateTimeOffset? startedAt,
        DateTimeOffset? finishedAt,
        bool verifyRows,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(analytics);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(path);

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"no such file: {path}");
            return 2;
        }

        var name = Path.GetFileName(path);
        var held = await analytics.ScalarAsync("SELECT count() FROM binding_snapshot", ct).ConfigureAwait(false) ?? 0;

        if (held == 0)
        {
            Console.Error.WriteLine(
                "refusing: binding_snapshot is empty, so no dump is loaded to record. --load-dump loads one and records it.");
            return 1;
        }

        if (verifyRows)
        {
            Console.WriteLine($"counting the rows of {name}");
            var rows = await CountRowsAsync(path, ct).ConfigureAwait(false);

            if (rows != held)
            {
                Console.Error.WriteLine(
                    $"refusing: {name} has {Format(rows)} rows and binding_snapshot holds {Format(held)}, "
                    + "so it is not the dump that was loaded");
                return 1;
            }

            Console.WriteLine($"  {Format(rows)} rows, exactly what binding_snapshot holds");
        }

        Console.WriteLine($"storing {name} in the Import Center's file store");
        StoredFile stored;
        await using (var file = File.OpenRead(path))
        {
            stored = await files.SaveAsync(Source, name, file, ct).ConfigureAwait(false);
        }

        var registered = await repository.RegisterFileAsync(Source, name, stored, Actor, ct).ConfigureAwait(false);

        if (!registered.IsNew)
        {
            // These bytes are registered already, with their own stored copy; this one is surplus.
            // The upload endpoint does the same with a file sent twice.
            await files.DeleteAsync(stored.StoredPath, ct).ConfigureAwait(false);
        }

        var how = startedAt is null
            ? $"Recorded afterwards, with --record-dump on {DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}: "
              + "when the load ran was not kept."
            : "Reconciled in binding_snapshot, binding_current and its IMSI and IMEI copies.";

        var message =
            $"Initial dump: {Format(held)} bindings, loaded with --load-dump straight into the analytics store "
            + $"- the Import Center takes daily files only. {how} It has no business date: it is the state "
            + "before the first daily file.";

        var recorded = await repository.RecordCompletedLoadAsync(
            Source, registered.FileId, held, startedAt, finishedAt, Actor, message, ct).ConfigureAwait(false);

        Console.WriteLine(recorded.Created
            ? $"  recorded in the Import Center as import {recorded.JobId}"
            : $"  already in the Import Center as import {recorded.JobId}; left as it is");
        return 0;
    }

    private static async Task<long> CountRowsAsync(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        await using var csv = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(file, CompressionMode.Decompress)
            : (Stream)file;
        var lines = new DumpLoad.LineCountingStream(csv);

        await lines.CopyToAsync(Stream.Null, ct).ConfigureAwait(false);
        return lines.DataRows;
    }

    private static string Format(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
