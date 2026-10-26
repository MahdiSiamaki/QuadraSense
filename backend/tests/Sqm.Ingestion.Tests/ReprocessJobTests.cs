using Sqm.Application.DataImport;

namespace Sqm.Ingestion.Tests;

/// <summary>"Import again" from the shell queues the same file as its day's next revision - and only a file it can read.</summary>
/// <remarks>
/// Added to repair the first machine, whose bulk load had written only part of four days
/// (2026-10-26) - and whose bulk-loaded files are recorded at a drive they have since left, so
/// "present" in the record is not enough. The rules are ImportIntake's, shared with the endpoint.
/// </remarks>
public sealed class ReprocessJobTests
{
    [Fact]
    public async Task A_finished_import_is_queued_again_for_its_day_ahead_of_ordinary_work()
    {
        var fake = new Fakes(readable: true);

        var exit = await ReprocessJob.RunAsync(fake.Repository, fake.Files, 41, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(("SQM", 7L, new DateOnly(2026, 2, 3), ReprocessJob.Actor, (short)1, 41L), fake.Enqueued);
        Assert.Equal(["GetJobAsync", "ExistsAsync", "EnqueueAsync", "WriteAuditAsync:import.reprocess"], fake.Calls);
    }

    [Fact]
    public async Task A_file_recorded_as_present_but_not_readable_is_refused_and_nothing_is_queued()
    {
        var fake = new Fakes(readable: false);

        var exit = await ReprocessJob.RunAsync(fake.Repository, fake.Files, 41, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(fake.Enqueued);
        Assert.DoesNotContain("EnqueueAsync", fake.Calls);
    }

    [Fact]
    public async Task An_unknown_import_is_reported()
    {
        var fake = new Fakes(readable: true, exists: false);

        var exit = await ReprocessJob.RunAsync(fake.Repository, fake.Files, 999, CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.Null(fake.Enqueued);
    }

    private sealed class Fakes
    {
        public Fakes(bool readable, bool exists = true)
        {
            Files = Stub.Create<IImportFileStore>((method, _) =>
            {
                Calls.Add(method.Name);
                return method.Name == "ExistsAsync" ? Task.FromResult(readable) : Stub.Default(method);
            });

            Repository = Stub.Create<IImportJobRepository>((method, args) =>
            {
                switch (method.Name)
                {
                    case "GetJobAsync":
                        Calls.Add(method.Name);
                        return Task.FromResult(exists ? Detail() : null);
                    case "EnqueueAsync":
                        Calls.Add(method.Name);
                        Enqueued = ((string)args[0]!, (long)args[1]!, (DateOnly?)args[2], (string)args[3]!,
                            Convert.ToInt16(args[4], System.Globalization.CultureInfo.InvariantCulture), (long?)args[5]);
                        return Task.FromResult(300L);
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

        public List<string> Calls { get; } = [];

        public (string Source, long FileId, DateOnly? Date, string By, short Priority, long? Of)? Enqueued { get; private set; }

        private static ImportJobDetail Detail() => new(
            new ImportJobSummary(
                41, "SQM", "daily_subs_device_sim_info_2026-02-03.csv", 448_915_891, ImportJobStatus.Completed,
                new DateOnly(2026, 2, 3), 1, true, 1, 5_564_191, 5_564_191, 0, 0, 0,
                DateTimeOffset.UnixEpoch, null, null, null, "bulk-load (pre-platform)", null,
                false, false, null, null),
            7, "828d34", "D:/SQM/New CDR/New CDR/daily_subs_device_sim_info_2026-02-03.csv", true, null, null,
            [], [], null, null, null);
    }
}
