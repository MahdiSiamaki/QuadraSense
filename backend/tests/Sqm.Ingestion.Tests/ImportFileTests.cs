using System.IO.Compression;
using System.Text;
using Sqm.Application.DataImport;

namespace Sqm.Ingestion.Tests;

/// <summary>A file already on the server enters the Import Center exactly as an upload of it would.</summary>
/// <remarks>
/// Uploads through the department's VPN broke at every drop (2026-10-25), so days are carried to the
/// server in pieces and queued with --import-file. These drive it against stubs; the rules after
/// storing are ImportIntake's, which the upload endpoint shares and Sqm.Integration.Tests'
/// OrphanedUploadTests exercises through HTTP.
/// </remarks>
public sealed class ImportFileTests : IDisposable
{
    private const string Day = "msisdn,imsi,imei,action\n9120000001,432110000000001,35004012000001,add\n";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "importfile-" + Guid.NewGuid().ToString("N"));

    public ImportFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task A_compressed_day_is_stored_as_its_csv_and_queued_for_the_day_in_its_name()
    {
        var fake = new Fakes();
        var path = Path.Combine(_dir, "daily_subs_device_sim_info_2026-01-26.csv.gz");
        await using (var gz = new GZipStream(File.Create(path), CompressionLevel.Fastest))
        {
            await gz.WriteAsync(Encoding.UTF8.GetBytes(Day));
        }

        var exit = await Run(fake, "SQM", path);

        Assert.Equal(0, exit);
        Assert.Equal(Day, fake.Saved);
        Assert.Equal(("SQM", "daily_subs_device_sim_info_2026-01-26.csv"), fake.SavedAs);
        Assert.Equal(("SQM", new DateOnly(2026, 1, 26), ImportFile.Actor), fake.Enqueued);
        Assert.Equal(["SaveAsync", "RegisterFileAsync", "EnqueueAsync", "WriteAuditAsync:import.upload"], fake.Calls);
    }

    [Fact]
    public async Task Content_already_imported_is_not_queued_again_and_its_second_copy_is_removed()
    {
        var fake = new Fakes { Known = true, ExistingJobId = 7 };

        var exit = await Run(fake, "SQM", Write("daily_subs_device_sim_info_2026-01-26.csv", Day));

        Assert.Equal(0, exit);
        Assert.Equal(
            ["SaveAsync", "RegisterFileAsync", "DeleteAsync", "WriteAuditAsync:import.upload.duplicate"], fake.Calls);
        Assert.Null(fake.Enqueued);
    }

    [Fact]
    public async Task A_file_registered_without_a_job_is_queued_from_the_copy_already_stored()
    {
        // An upload that ended between registering the file and queueing it.
        var fake = new Fakes { Known = true, ExistingJobId = null };

        var exit = await Run(fake, "SQM", Write("daily_subs_device_sim_info_2026-01-26.csv", Day));

        Assert.Equal(0, exit);
        Assert.Equal(
            ["SaveAsync", "RegisterFileAsync", "DeleteAsync", "EnqueueAsync", "WriteAuditAsync:import.upload"], fake.Calls);
    }

    [Fact]
    public async Task An_unknown_source_is_refused_before_anything_is_stored()
    {
        var fake = new Fakes();

        var exit = await Run(fake, "XYZ", Write("daily_subs_device_sim_info_2026-01-26.csv", Day));

        Assert.Equal(2, exit);
        Assert.Empty(fake.Calls);
    }

    private static Task<int> Run(Fakes fake, string source, string path) =>
        ImportFile.RunAsync(fake.Repository, fake.Files, source, path, CancellationToken.None);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class Fakes
    {
        public Fakes()
        {
            Files = Stub.Create<IImportFileStore>((method, args) =>
            {
                Calls.Add(method.Name);
                return method.Name == "SaveAsync"
                    ? SaveAsync((string)args[0]!, (string)args[1]!, (Stream)args[2]!)
                    : Stub.Default(method);
            });

            Repository = Stub.Create<IImportJobRepository>((method, args) =>
            {
                switch (method.Name)
                {
                    case "RegisterFileAsync":
                        Calls.Add(method.Name);
                        return Task.FromResult(new RegisteredFile(41, "abc", IsNew: !Known, Known ? ExistingJobId : null, null));
                    case "EnqueueAsync":
                        Calls.Add(method.Name);
                        Enqueued = ((string)args[0]!, (DateOnly?)args[2], (string)args[3]!);
                        return Task.FromResult(8L);
                    case "WriteAuditAsync":
                        Calls.Add($"{method.Name}:{args[1]}");
                        return Task.CompletedTask;
                    default:
                        Calls.Add(method.Name);
                        return Stub.Default(method);
                }
            });
        }

        public IImportFileStore Files { get; }

        public IImportJobRepository Repository { get; }

        public bool Known { get; init; }

        public long? ExistingJobId { get; init; }

        public List<string> Calls { get; } = [];

        public string? Saved { get; private set; }

        public (string Source, string Name)? SavedAs { get; private set; }

        public (string Source, DateOnly? Date, string By)? Enqueued { get; private set; }

        private async Task<StoredFile> SaveAsync(string source, string name, Stream content)
        {
            using var reader = new StreamReader(content);
            Saved = await reader.ReadToEndAsync();
            SavedAs = (source, name);
            return new StoredFile("SQM/2026/10/stored.csv", Saved.Length, "abc");
        }
    }
}
