using System.IO.Compression;
using System.Text;
using Sqm.Application.DataImport;

namespace Sqm.Ingestion.Tests;

/// <summary>The loaded dump is stored and recorded in the Import Center - and only the dump that was loaded.</summary>
/// <remarks>
/// On the department VM the Import Center showed no trace of the 126 million bindings --load-dump
/// had loaded (2026-10-24). These drive DumpHistory against stubs; the SQL that records the job is
/// proved against the real PostgreSQL in Sqm.Integration.Tests' ImportQueueTests.
/// </remarks>
public sealed class DumpHistoryTests : IDisposable
{
    private const string Csv = "imei,imsi,msisdn\n35004012000001,432110000000001,9120000001\n"
        + "35004012000002,432110000000002,9120000002\n35004012000003,432110000000003,9120000003\n";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dumphistory-" + Guid.NewGuid().ToString("N"));

    public DumpHistoryTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Nothing_is_stored_or_recorded_when_no_dump_is_loaded()
    {
        var fake = new Fakes(held: 0);

        var exit = await Run(fake, Write("dump.csv", Csv), verifyRows: true);

        Assert.Equal(1, exit);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task A_file_that_is_not_the_loaded_dump_is_refused_before_anything_is_stored()
    {
        var fake = new Fakes(held: 4);

        var exit = await Run(fake, Write("dump.csv", Csv), verifyRows: true);

        Assert.Equal(1, exit);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task A_compressed_dump_is_counted_through_its_compression_and_stored_as_it_is()
    {
        var fake = new Fakes(held: 3);
        var path = Path.Combine(_dir, "dump_subs_device_sim_info_20251227_20260125.csv.gz");
        await using (var gz = new GZipStream(File.Create(path), CompressionLevel.Fastest))
        {
            await gz.WriteAsync(Encoding.UTF8.GetBytes(Csv));
        }

        var exit = await Run(fake, path, verifyRows: true);

        Assert.Equal(0, exit);
        Assert.Equal(await File.ReadAllBytesAsync(path), fake.Saved);
        Assert.Equal(["SaveAsync", "RegisterFileAsync", "RecordCompletedLoadAsync"], fake.Calls);
        Assert.Equal(("SQM", "dump_subs_device_sim_info_20251227_20260125.csv.gz", DumpHistory.Actor), fake.Registered);
        Assert.Equal(3L, fake.RecordedRows);
        Assert.Null(fake.RecordedTimes.Started);
        Assert.Contains("Recorded afterwards", fake.RecordedMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fresh_load_is_recorded_with_its_rows_and_when_it_ran()
    {
        var fake = new Fakes(held: 3);
        var started = new DateTimeOffset(2026, 10, 24, 7, 48, 45, TimeSpan.Zero);
        var finished = started.AddMinutes(21);

        var exit = await DumpHistory.RecordAsync(
            fake.Analytics, fake.Repository, fake.Files, Write("dump.csv", Csv), started, finished,
            verifyRows: false, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(3L, fake.RecordedRows);
        Assert.Equal((started, finished), fake.RecordedTimes);
        Assert.Contains("Reconciled", fake.RecordedMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dump_registered_before_keeps_its_record_and_the_surplus_copy_is_removed()
    {
        var fake = new Fakes(held: 3) { AlreadyRegistered = true };

        var exit = await Run(fake, Write("dump.csv", Csv), verifyRows: true);

        Assert.Equal(0, exit);
        Assert.Equal(["SaveAsync", "RegisterFileAsync", "DeleteAsync", "RecordCompletedLoadAsync"], fake.Calls);
        Assert.Equal("SQM/2026/10/stored.csv", fake.Deleted);
    }

    private static Task<int> Run(Fakes fake, string path, bool verifyRows) =>
        DumpHistory.RecordAsync(
            fake.Analytics, fake.Repository, fake.Files, path, null, null, verifyRows, CancellationToken.None);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class Fakes
    {
        public Fakes(long held)
        {
            Analytics = Stub.Create<IAnalyticsIngestionStore>((method, _) =>
                method.Name == "ScalarAsync" ? Task.FromResult<long?>(held) : Stub.Default(method));

            Files = Stub.Create<IImportFileStore>((method, args) =>
            {
                Calls.Add(method.Name);
                switch (method.Name)
                {
                    case "SaveAsync":
                        return SaveAsync((Stream)args[2]!);
                    case "DeleteAsync":
                        Deleted = (string)args[0]!;
                        return Task.CompletedTask;
                    default:
                        return Stub.Default(method);
                }
            });

            Repository = Stub.Create<IImportJobRepository>((method, args) =>
            {
                Calls.Add(method.Name);
                switch (method.Name)
                {
                    case "RegisterFileAsync":
                        Registered = ((string)args[0]!, (string)args[1]!, (string)args[3]!);
                        return Task.FromResult(new RegisteredFile(
                            41, "abc", IsNew: !AlreadyRegistered, AlreadyRegistered ? 7 : null, null));
                    case "RecordCompletedLoadAsync":
                        RecordedRows = (long)args[2]!;
                        RecordedTimes = ((DateTimeOffset?)args[3], (DateTimeOffset?)args[4]);
                        RecordedMessage = (string)args[6]!;
                        return Task.FromResult(new RecordedLoad(AlreadyRegistered ? 7 : 8, !AlreadyRegistered));
                    default:
                        return Stub.Default(method);
                }
            });
        }

        public IAnalyticsIngestionStore Analytics { get; }

        public IImportFileStore Files { get; }

        public IImportJobRepository Repository { get; }

        public bool AlreadyRegistered { get; init; }

        public List<string> Calls { get; } = [];

        public byte[]? Saved { get; private set; }

        public string? Deleted { get; private set; }

        public (string Source, string Name, string By) Registered { get; private set; }

        public long RecordedRows { get; private set; }

        public (DateTimeOffset? Started, DateTimeOffset? Finished) RecordedTimes { get; private set; }

        public string RecordedMessage { get; private set; } = string.Empty;

        private async Task<StoredFile> SaveAsync(Stream content)
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy);
            Saved = copy.ToArray();
            return new StoredFile("SQM/2026/10/stored.csv", Saved.Length, "abc");
        }
    }
}
