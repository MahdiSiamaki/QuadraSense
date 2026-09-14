using Sqm.Application.DataImport;

namespace Sqm.Integration.Tests;

/// <summary>
/// The guarantees the import platform claims, checked against a real PostgreSQL.
/// </summary>
/// <remarks>
/// Each test here corresponds to a claim made in the design that would be expensive to be wrong
/// about: a duplicate file is recognised, two workers never get the same job, a business date
/// never has two effective imports, and a crashed worker's job comes back.
/// </remarks>
[Collection("import-queue")]
public sealed class ImportQueueTests(ImportQueueFixture fixture) : IClassFixture<ImportQueueFixture>
{
    private const string TestSource = "ITEST";

    private static StoredFile Stored(string sha, long bytes = 1024) =>
        new($"itest/{sha[..8]}.csv", bytes, sha);

    private static string Hash(string seed) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(seed)));

    private async Task<bool> ReadyAsync()
    {
        if (!fixture.IsAvailable)
        {
            return false;
        }

        await fixture.EnsureSourceAsync(TestSource);
        return true;
    }

    [Fact]
    public async Task The_same_content_uploaded_twice_is_recognised_not_imported_again()
    {
        if (!await ReadyAsync())
        {
            Assert.Skip(fixture.UnavailableReason ?? "no database");
            return;
        }

        try
        {
            var sha = Hash(nameof(The_same_content_uploaded_twice_is_recognised_not_imported_again));

            var first = await fixture.Repository.RegisterFileAsync(
                TestSource, "day-1.csv", Stored(sha), "tester", TestContext.Current.CancellationToken);

            Assert.True(first.IsNew);

            await fixture.Repository.EnqueueAsync(
                TestSource, first.FileId, new DateOnly(2026, 3, 1), "tester",
                ct: TestContext.Current.CancellationToken);

            // Same bytes, different name. Identity is the content hash, so this is the same file.
            var second = await fixture.Repository.RegisterFileAsync(
                TestSource, "day-1-copy.csv", Stored(sha), "tester",
                TestContext.Current.CancellationToken);

            Assert.False(second.IsNew);
            Assert.Equal(first.FileId, second.FileId);
            Assert.Equal("day-1.csv", second.ExistingOriginalName);
            Assert.NotNull(second.ExistingJobId);
        }
        finally
        {
            await fixture.CleanupAsync(TestSource);
        }
    }

    [Fact]
    public async Task Two_workers_asking_at_once_never_get_the_same_job()
    {
        if (!await ReadyAsync())
        {
            Assert.Skip(fixture.UnavailableReason ?? "no database");
            return;
        }

        try
        {
            var ct = TestContext.Current.CancellationToken;

            for (var i = 0; i < 4; i++)
            {
                var file = await fixture.Repository.RegisterFileAsync(
                    TestSource, $"day-{i}.csv", Stored(Hash($"skiplocked-{i}")), "tester", ct);

                await fixture.Repository.EnqueueAsync(
                    TestSource, file.FileId, new DateOnly(2026, 4, 1).AddDays(i), "tester", ct: ct);
            }

            // Four workers, four jobs, claimed concurrently. Without SKIP LOCKED these would
            // serialise behind one another; with it they step over each other's locked rows.
            var claims = await Task.WhenAll(
                Enumerable.Range(0, 4).Select(n =>
                    fixture.Repository.ClaimNextAsync($"worker-{n}", TimeSpan.FromMinutes(5), ct)));

            var claimed = claims.Where(c => c is not null).Select(c => c!.JobId).ToList();

            Assert.Equal(4, claimed.Count);
            Assert.Equal(claimed.Count, claimed.Distinct().Count());
        }
        finally
        {
            await fixture.CleanupAsync(TestSource);
        }
    }

    [Fact]
    public async Task A_business_date_never_has_two_effective_imports()
    {
        if (!await ReadyAsync())
        {
            Assert.Skip(fixture.UnavailableReason ?? "no database");
            return;
        }

        try
        {
            var ct = TestContext.Current.CancellationToken;
            var date = new DateOnly(2026, 5, 20);

            var originalFile = await fixture.Repository.RegisterFileAsync(
                TestSource, "day.csv", Stored(Hash("effective-1")), "tester", ct);
            var originalJob = await fixture.Repository.EnqueueAsync(
                TestSource, originalFile.FileId, date, "tester", ct: ct);

            await fixture.Repository.CompleteAsync(
                originalJob, ImportJobStatus.Completed, new ImportCounters(RowsInserted: 100),
                makeEffective: true, ct);

            // A corrected file for the same day.
            var correctedFile = await fixture.Repository.RegisterFileAsync(
                TestSource, "day-corrected.csv", Stored(Hash("effective-2")), "tester", ct);
            var correctedJob = await fixture.Repository.EnqueueAsync(
                TestSource, correctedFile.FileId, date, "tester", ct: ct);

            await fixture.Repository.CompleteAsync(
                correctedJob, ImportJobStatus.Completed, new ImportCounters(RowsInserted: 105),
                makeEffective: true, ct);

            var effective = await fixture.Repository.ListJobsAsync(
                new ImportHistoryFilter(TestSource, EffectiveOnly: true), 50, 0, ct);

            var forDate = effective.Where(j => j.BusinessDate == date).ToList();

            Assert.Single(forDate);
            Assert.Equal(correctedJob, forDate[0].JobId);
            Assert.Equal(2, forDate[0].Revision);

            // The superseded job is still in history - a day imported twice is a fact worth
            // being able to see, and the detail page links the two.
            var detail = await fixture.Repository.GetJobAsync(correctedJob, ct);
            Assert.Equal(originalJob, detail!.SupersedesJobId);
        }
        finally
        {
            await fixture.CleanupAsync(TestSource);
        }
    }

    [Fact]
    public async Task A_job_whose_worker_stopped_renewing_comes_back_to_the_queue()
    {
        if (!await ReadyAsync())
        {
            Assert.Skip(fixture.UnavailableReason ?? "no database");
            return;
        }

        try
        {
            var ct = TestContext.Current.CancellationToken;

            var file = await fixture.Repository.RegisterFileAsync(
                TestSource, "crash.csv", Stored(Hash("crash")), "tester", ct);
            var jobId = await fixture.Repository.EnqueueAsync(
                TestSource, file.FileId, new DateOnly(2026, 6, 1), "tester", ct: ct);

            // A lease that has already expired is what a dead worker leaves behind.
            var claimed = await fixture.Repository.ClaimNextAsync(
                "doomed-worker", TimeSpan.FromSeconds(-1), ct);

            Assert.NotNull(claimed);
            Assert.Equal(jobId, claimed.JobId);

            var recovered = await fixture.Repository.RecoverExpiredLeasesAsync(ct);
            Assert.True(recovered >= 1);

            // Another worker can now take it, and the attempt counter remembers the first try.
            var retaken = await fixture.Repository.ClaimNextAsync(
                "healthy-worker", TimeSpan.FromMinutes(5), ct);

            Assert.NotNull(retaken);
            Assert.Equal(jobId, retaken.JobId);
            Assert.Equal(2, retaken.Attempt);
        }
        finally
        {
            await fixture.CleanupAsync(TestSource);
        }
    }

    [Fact]
    public async Task A_retryable_failure_is_scheduled_again_and_a_fatal_one_is_not()
    {
        if (!await ReadyAsync())
        {
            Assert.Skip(fixture.UnavailableReason ?? "no database");
            return;
        }

        try
        {
            var ct = TestContext.Current.CancellationToken;

            var file = await fixture.Repository.RegisterFileAsync(
                TestSource, "retry.csv", Stored(Hash("retry")), "tester", ct);
            var jobId = await fixture.Repository.EnqueueAsync(
                TestSource, file.FileId, new DateOnly(2026, 6, 2), "tester", ct: ct);

            await fixture.Repository.ClaimNextAsync("worker", TimeSpan.FromMinutes(5), ct);

            var willRetry = await fixture.Repository.FailAsync(
                jobId, "ClickHouse was unreachable", isRetryable: true, TimeSpan.FromSeconds(1), ct);

            Assert.True(willRetry);

            // A file whose columns are wrong will have the same columns in five minutes, so the
            // worker classifies it as fatal and the queue does not try again.
            await fixture.Repository.ClaimNextAsync("worker", TimeSpan.FromMinutes(5), ct);

            var willRetryAgain = await fixture.Repository.FailAsync(
                jobId, "Unexpected columns", isRetryable: false, TimeSpan.Zero, ct);

            Assert.False(willRetryAgain);

            var detail = await fixture.Repository.GetJobAsync(jobId, ct);
            Assert.Equal(ImportJobStatus.Failed, detail!.Summary.Status);
            Assert.Equal("Unexpected columns", detail.Summary.ErrorSummary);
        }
        finally
        {
            await fixture.CleanupAsync(TestSource);
        }
    }

    [Fact]
    public async Task Quarantine_is_recorded_by_rule_with_a_capped_sample()
    {
        if (!await ReadyAsync())
        {
            Assert.Skip(fixture.UnavailableReason ?? "no database");
            return;
        }

        try
        {
            var ct = TestContext.Current.CancellationToken;

            var file = await fixture.Repository.RegisterFileAsync(
                TestSource, "dirty.csv", Stored(Hash("dirty")), "tester", ct);
            var jobId = await fixture.Repository.EnqueueAsync(
                TestSource, file.FileId, new DateOnly(2026, 6, 3), "tester", ct: ct);

            // Eight million broken rows, three examples. Storing every offending row would make
            // error handling the storage problem.
            await fixture.Repository.RecordQuarantineAsync(jobId,
            [
                new QuarantineWrite(
                    "TEST_RULE", "imei", "warning", 8_000_000, 42, "A test rule.",
                    [
                        new QuarantineSample(42, "1,2,bad,add", "bad"),
                        new QuarantineSample(99, "1,2,worse,add", "worse"),
                        new QuarantineSample(123, "1,2,worst,add", "worst"),
                    ]),
            ], ct);

            var detail = await fixture.Repository.GetJobAsync(jobId, ct);
            var group = Assert.Single(detail!.Quarantine);

            Assert.Equal(8_000_000, group.OccurrenceCount);
            Assert.Equal(42, group.FirstRowNumber);

            // The job's warning count is distinct rules, not rows: "8 million rows failed one
            // rule" and "8 million rows failed 400 rules" are very different situations.
            Assert.Equal(1, detail.Summary.WarningCount);

            var samples = await fixture.Repository.GetQuarantineSamplesAsync(group.SummaryId, 10, ct);
            Assert.Equal(3, samples.Count);
        }
        finally
        {
            await fixture.CleanupAsync(TestSource);
        }
    }
}
