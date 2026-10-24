using System.IO.Compression;
using System.Text;
using Sqm.Application.DataImport;

namespace Sqm.Ingestion.Tests;

/// <summary>The initial dump goes in exactly, into an empty store, or not at all.</summary>
/// <remarks>
/// No code loaded the dump until 2026-10-24: the first deployment away from the development machine
/// had no way to start (DumpLoad). These drive the command against a stub store; the SQL it runs is
/// proved against the real ClickHouse in Sqm.Integration.Tests' DumpLoadStoreTests.
/// </remarks>
public sealed class DumpLoadTests : IDisposable
{
    private const string Csv = "imei,imsi,msisdn\n35004012000001,432110000000001,9120000001\n"
        + "35004012000002,432110000000002,9120000002\n35004012000003,432110000000003,9120000003\n";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dumpload-" + Guid.NewGuid().ToString("N"));

    public DumpLoadTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task A_store_that_already_holds_bindings_is_refused_untouched()
    {
        var store = new FakeStore(held: 5);

        var exit = await DumpLoad.RunAsync(store.Proxy, Write("dump.csv", Csv), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.DoesNotContain("LoadDumpAsync", store.Calls);
        Assert.DoesNotContain("ClearDumpAsync", store.Calls);
        Assert.DoesNotContain("SetMergesEnabledAsync", store.Calls);
    }

    [Fact]
    public async Task Every_row_is_counted_and_every_table_must_hold_exactly_that_many()
    {
        var store = new FakeStore(held: 0) { Rows = 3 };

        var exit = await DumpLoad.RunAsync(store.Proxy, Write("dump.csv", Csv), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(Csv, store.Received);
        Assert.Contains("SeedCurrentFromDumpAsync", store.Calls);
        Assert.DoesNotContain("ClearDumpAsync", store.Calls);
        Assert.Equal([false, true], store.Merges);
    }

    [Fact]
    public async Task A_copy_that_does_not_reconcile_undoes_the_whole_load()
    {
        var store = new FakeStore(held: 0) { Rows = 3, ImeiCopyRows = 2 };

        var exit = await DumpLoad.RunAsync(store.Proxy, Write("dump.csv", Csv), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("ClearDumpAsync", store.Calls);
        Assert.Equal([false, true], store.Merges);
    }

    [Fact]
    public async Task Fewer_rows_written_than_the_file_holds_undoes_it_without_seeding()
    {
        var store = new FakeStore(held: 0) { Rows = 3, Written = 2 };

        var exit = await DumpLoad.RunAsync(store.Proxy, Write("dump.csv", Csv), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.DoesNotContain("SeedCurrentFromDumpAsync", store.Calls);
        Assert.Contains("ClearDumpAsync", store.Calls);
    }

    [Fact]
    public async Task A_failed_insert_undoes_whatever_it_left_and_resumes_merges()
    {
        var store = new FakeStore(held: 0) { Rows = 3, Fail = true };

        var exit = await DumpLoad.RunAsync(store.Proxy, Write("dump.csv", Csv), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("ClearDumpAsync", store.Calls);
        Assert.Equal([false, true], store.Merges);
    }

    [Fact]
    public async Task A_gzipped_dump_is_read_as_the_csv_inside_it()
    {
        var path = Path.Combine(_dir, "dump.csv.gz");
        await using (var gz = new GZipStream(File.Create(path), CompressionLevel.Fastest))
        {
            await gz.WriteAsync(Encoding.UTF8.GetBytes(Csv));
        }

        var store = new FakeStore(held: 0) { Rows = 3 };

        var exit = await DumpLoad.RunAsync(store.Proxy, path, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(Csv, store.Received);
    }

    [Theory]
    [InlineData("h\na\nb\n", 2)]
    [InlineData("h\na\nb", 2)]
    [InlineData("h\r\na\r\nb\r\n", 2)]
    [InlineData("h\n", 0)]
    [InlineData("", 0)]
    public void Rows_are_the_lines_after_the_header_with_or_without_a_final_newline(string text, long rows)
    {
        using var counting = new DumpLoad.LineCountingStream(new MemoryStream(Encoding.UTF8.GetBytes(text)));
        counting.CopyTo(Stream.Null);

        Assert.Equal(rows, counting.DataRows);
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>A store whose counts follow what the test says each step leaves behind.</summary>
    private sealed class FakeStore
    {
        public FakeStore(long held)
        {
            Proxy = Stub.Create<IAnalyticsIngestionStore>((method, args) =>
            {
                Calls.Add(method.Name);
                switch (method.Name)
                {
                    case "ScalarAsync":
                        var sql = (string)args[0]!;
                        return Task.FromResult<long?>(
                            sql.Contains("system.merges", StringComparison.Ordinal) ? 0
                            : !_loaded ? held
                            : sql.Contains("binding_by_imei", StringComparison.Ordinal) ? ImeiCopyRows ?? Rows
                            : Rows);
                    case "SetMergesEnabledAsync":
                        Merges.Add((bool)args[0]!);
                        return Task.CompletedTask;
                    case "LoadDumpAsync":
                        return LoadAsync((Stream)args[0]!);
                    case "SeedCurrentFromDumpAsync":
                        return Task.FromResult(Rows);
                    default:
                        return Stub.Default(method);
                }
            });
        }

        public IAnalyticsIngestionStore Proxy { get; }

        public List<string> Calls { get; } = [];

        public List<bool> Merges { get; } = [];

        public long Rows { get; init; }

        public long? Written { get; init; }

        public long? ImeiCopyRows { get; init; }

        public bool Fail { get; init; }

        public string? Received { get; private set; }

        private bool _loaded;

        private async Task<long> LoadAsync(Stream csv)
        {
            using var reader = new StreamReader(csv);
            Received = await reader.ReadToEndAsync();
            _loaded = true;
            return Fail ? throw new InvalidOperationException("ClickHouse rejected the insert") : Written ?? Rows;
        }
    }
}
