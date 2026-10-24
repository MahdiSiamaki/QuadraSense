using System.IO.Compression;
using Sqm.Application.DataImport;

namespace Sqm.Ingestion;

/// <summary>
/// Loads the operator's initial dump into a store that holds nothing yet - the baseline every daily
/// file folds onto.
/// </summary>
/// <remarks>
/// <para>
/// <c>--load-dump &lt;file&gt;</c>, once per environment, before the first daily file. Found
/// missing on 2026-10-24, deploying to the department VM: the Import Center refuses the dump (its
/// name is a range, not a day) and on the first machine it had gone in by hand. See
/// <c>ClickHouseIngestionStore.Dump.cs</c>.
/// </para>
/// <para>
/// <b>Exact, or nothing.</b> The file's lines are counted as it streams; with merges paused, the
/// snapshot, current state and its two ordered copies must each hold exactly that many rows. Any
/// difference, or any failure on the way, empties all four again - a half-loaded baseline would
/// be folded onto by every day after it, and nothing downstream could tell.
/// </para>
/// <para>
/// A <c>.gz</c> file is read compressed, so the dump can be carried and kept at a fraction of its
/// size: 4.9 GB of CSV.
/// </para>
/// </remarks>
internal static class DumpLoad
{
    /// <summary>Loads the dump and returns a process exit code.</summary>
    /// <returns>0 loaded and reconciled; 1 refused or did not reconcile (and undone); 2 no such file.</returns>
    public static async Task<int> RunAsync(IAnalyticsIngestionStore analytics, string path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(analytics);
        ArgumentNullException.ThrowIfNull(path);

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"no such file: {path}");
            return 2;
        }

        // A dump on top of anything would double the baseline, or put day-0 state under days
        // already folded. Only an empty store takes one.
        foreach (var table in new[] { "binding_snapshot", "binding_current" })
        {
            var held = await analytics.ScalarAsync($"SELECT count() FROM {table}", ct).ConfigureAwait(false);
            if (held is not 0)
            {
                Console.Error.WriteLine(
                    $"refusing: {table} already holds {held:N0} rows. The dump is loaded once, into an empty store.");
                return 1;
            }
        }

        Console.WriteLine($"loading the initial dump from {Path.GetFileName(path)}");

        if (await MergeControl.PauseAsync(analytics, ct).ConfigureAwait(false) != 0)
        {
            await MergeControl.ResumeAsync(analytics, ct).ConfigureAwait(false);
            Console.Error.WriteLine("refusing: merges did not stop, so the counts could not be checked exactly");
            return 1;
        }

        try
        {
            await using var file = File.OpenRead(path);
            await using var csv = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                ? new GZipStream(file, CompressionMode.Decompress)
                : (Stream)file;
            var lines = new LineCountingStream(csv);

            var tenth = 0L;
            var written = await analytics.LoadDumpAsync(lines, _ =>
            {
                var now = file.Length == 0 ? 10 : file.Position * 10 / file.Length;
                if (now > tenth)
                {
                    tenth = now;
                    Console.WriteLine($"  {now * 10}% read");
                }
            }, ct).ConfigureAwait(false);

            var rows = lines.DataRows;
            Console.WriteLine($"  file: {rows:N0} rows; written to binding_snapshot: {written:N0}");

            var counts = new List<(string Table, long Rows)>
            {
                ("binding_snapshot", await CountAsync(analytics, "binding_snapshot", ct).ConfigureAwait(false)),
            };

            if (written == rows && counts[0].Rows == rows)
            {
                Console.WriteLine("  seeding current state from it");
                counts.Add(("binding_current", await analytics.SeedCurrentFromDumpAsync(ct).ConfigureAwait(false)));
                counts.Add(("binding_by_imsi", await CountAsync(analytics, "binding_by_imsi", ct).ConfigureAwait(false)));
                counts.Add(("binding_by_imei", await CountAsync(analytics, "binding_by_imei", ct).ConfigureAwait(false)));
            }

            foreach (var (table, held) in counts)
            {
                Console.WriteLine($"  {table,-17} {held,15:N0}{(held == rows ? string.Empty : "   <- expected " + rows.ToString("N0", System.Globalization.CultureInfo.InvariantCulture))}");
            }

            if (written != rows || counts.Count != 4 || counts.Any(c => c.Rows != rows))
            {
                Console.Error.WriteLine("the dump did not reconcile; undoing it");
                await analytics.ClearDumpAsync(CancellationToken.None).ConfigureAwait(false);
                return 1;
            }

            Console.WriteLine($"initial dump loaded: {rows:N0} bindings");
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"the dump failed: {ex.Message}");
            Console.Error.WriteLine("undoing it");
            await analytics.ClearDumpAsync(CancellationToken.None).ConfigureAwait(false);
            return 1;
        }
        finally
        {
            await MergeControl.ResumeAsync(analytics, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<long> CountAsync(IAnalyticsIngestionStore analytics, string table, CancellationToken ct) =>
        await analytics.ScalarAsync($"SELECT count() FROM {table}", ct).ConfigureAwait(false) ?? -1;

    /// <summary>Passes a CSV through, counting its lines.</summary>
    internal sealed class LineCountingStream(Stream inner) : Stream
    {
        private long _newlines;
        private long _bytes;
        private byte _last;

        /// <summary>Rows after the header: a last line without its newline still counts.</summary>
        public long DataRows
        {
            get
            {
                var lines = _newlines + (_bytes > 0 && _last != (byte)'\n' ? 1 : 0);
                return Math.Max(0, lines - 1);
            }
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _bytes;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Count(buffer.AsSpan(offset, inner.Read(buffer, offset, count)));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            return Count(buffer.Span[..read]);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Count(ReadOnlySpan<byte> read)
        {
            if (read.Length > 0)
            {
                _newlines += read.Count((byte)'\n');
                _bytes += read.Length;
                _last = read[^1];
            }

            return read.Length;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
