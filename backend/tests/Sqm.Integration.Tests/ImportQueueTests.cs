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

    /// <summary>
    /// A later day is not claimable while an earlier one is still unlanded.
    /// </summary>
    /// <remarks>
    /// This is the rule that stops a delivery snapshot describing a network that never existed.
    /// Every dashboard mart is "the state at delivery N", aggregated from current state as it
    /// stands when the mart runs - so running day N+1 first, or running N after N+1 has folded,
    /// writes the wrong state under the right label. Three real deliveries did exactly that and
    /// ended up holding the identical figure of 114,230,645 active bindings.
    ///
    /// ORDER BY alone could not prevent it: SKIP LOCKED steps over an earlier day held by another
    /// worker - or by one that died holding it - and takes the next.
    /// </remarks>
    [Fact]
    public async Task A_later_day_is_not_claimed_while_an_earlier_day_is_still_queued()
    {
        if (!await ReadyAsync())
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;

        try
        {
            // Enqueued newest first, so passing would be impossible by accident.
            var laterFile = await fixture.Repository.RegisterFileAsync(
                TestSource, "day-2.csv", Stored(Hash("order-later")), "tester", ct);
            var later = await fixture.Repository.EnqueueAsync(
                TestSource, laterFile.FileId, new DateOnly(2026, 4, 2), "tester", ct: ct);

            var earlierFile = await fixture.Repository.RegisterFileAsync(
                TestSource, "day-1.csv", Stored(Hash("order-earlier")), "tester", ct);
            var earlier = await fixture.Repository.EnqueueAsync(
                TestSource, earlierFile.FileId, new DateOnly(2026, 4, 1), "tester", ct: ct);

            var first = await fixture.Repository.ClaimNextAsync(
                "worker-a", TimeSpan.FromMinutes(5), ct);

            Assert.NotNull(first);
            Assert.Equal(earlier, first.JobId);

            // The earlier day is now RUNNING rather than queued, and that must block just as
            // firmly - this is the case SKIP LOCKED used to walk straight past.
            var second = await fixture.Repository.ClaimNextAsync(
                "worker-b", TimeSpan.FromMinutes(5), ct);

            Assert.Null(second);

            // Landed, and the later day becomes claimable.
            await fixture.Repository.CompleteAsync(
                earlier, ImportJobStatus.Completed, new ImportCounters(RowsInserted: 1),
                makeEffective: true, businessDate: new DateOnly(2026, 4, 1), ct);

            var third = await fixture.Repository.ClaimNextAsync(
                "worker-b", TimeSpan.FromMinutes(5), ct);

            Assert.NotNull(third);
            Assert.Equal(later, third.JobId);
        }
        finally
        {
            await fixture.CleanupAsync(TestSource);
        }
    }

    /// <summary>
    /// A day that failed blocks the days after it, rather than letting them run over the hole.
    /// </summary>
    /// <remarks>
    /// Deliberate, and the less obvious half of the rule. Skipping a failed day would let the
    /// next one build a snapshot of a network missing a day's changes, and nothing downstream
    /// would ever say so. Blocking is visible on the Import Center and has an exit: reprocess the
    /// failed day, or delete it.
    /// </remarks>
    [Fact]
    public async Task A_failed_day_blocks_the_days_after_it()
    {
        if (!await ReadyAsync())
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;

        try
        {
            var badFile = await fixture.Repository.RegisterFileAsync(
                TestSource, "bad-day.csv", Stored(Hash("order-failed")), "tester", ct);
            var bad = await fixture.Repository.EnqueueAsync(
                TestSource, badFile.FileId, new DateOnly(2026, 5, 1), "tester", ct: ct);

            var nextFile = await fixture.Repository.RegisterFileAsync(
                TestSource, "next-day.csv", Stored(Hash("order-after-failed")), "tester", ct);
            await fixture.Repository.EnqueueAsync(
                TestSource, nextFile.FileId, new DateOnly(2026, 5, 2), "tester", ct: ct);

            var claimed = await fixture.Repository.ClaimNextAsync(
                "worker-a", TimeSpan.FromMinutes(5), ct);
            Assert.NotNull(claimed);
            Assert.Equal(bad, claimed.JobId);

            await fixture.Repository.CompleteAsync(
                bad, ImportJobStatus.Failed, new ImportCounters(),
                makeEffective: false, businessDate: new DateOnly(2026, 5, 1), ct);

            Assert.Null(await fixture.Repository.ClaimNextAsync(
                "worker-b", TimeSpan.FromMinutes(5), ct));
        }
        finally
        {
            await fixture.CleanupAsync(TestSource);
        }
    }

    [Fact]
    public async Task A_failed_attempt_stops_blocking_once_that_day_lands_another_way()
    {
        if (!await ReadyAsync())
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;

        try
        {
            // The shape that jammed the real queue. 2026-07-11 failed as one job and was
            // re-imported successfully as another; the day had landed, the failed attempt had
            // not. Written as "no earlier JOB that did not succeed", the dead attempt blocked
            // four later days indefinitely - and a reprocess could not release them, because it
            // adds a new job and leaves the old one exactly as it was.
            var badFile = await fixture.Repository.RegisterFileAsync(
                TestSource, "day-one-attempt-one.csv", Stored(Hash("superseded-failure")),
                "tester", ct);
            var bad = await fixture.Repository.EnqueueAsync(
                TestSource, badFile.FileId, new DateOnly(2026, 5, 1), "tester", ct: ct);

            var laterFile = await fixture.Repository.RegisterFileAsync(
                TestSource, "day-two.csv", Stored(Hash("superseded-later-day")), "tester", ct);
            await fixture.Repository.EnqueueAsync(
                TestSource, laterFile.FileId, new DateOnly(2026, 5, 2), "tester", ct: ct);

            var first = await fixture.Repository.ClaimNextAsync(
                "worker-a", TimeSpan.FromMinutes(5), ct);
            Assert.NotNull(first);
            Assert.Equal(bad, first.JobId);

            await fixture.Repository.CompleteAsync(
                bad, ImportJobStatus.Failed, new ImportCounters(),
                makeEffective: false, businessDate: new DateOnly(2026, 5, 1), ct);

            // Still blocked: 1 May has not landed by any route yet. This is the guarantee that
            // must survive the fix, so it is asserted before the fix is exercised.
            Assert.Null(await fixture.Repository.ClaimNextAsync(
                "worker-b", TimeSpan.FromMinutes(5), ct));

            // Now 1 May is re-imported successfully as a second job, which is what a reprocess
            // does.
            var retryFile = await fixture.Repository.RegisterFileAsync(
                TestSource, "day-one-attempt-two.csv", Stored(Hash("superseded-retry")),
                "tester", ct);
            var retry = await fixture.Repository.EnqueueAsync(
                TestSource, retryFile.FileId, new DateOnly(2026, 5, 1), "tester", ct: ct);

            var second = await fixture.Repository.ClaimNextAsync(
                "worker-c", TimeSpan.FromMinutes(5), ct);
            Assert.NotNull(second);
            Assert.Equal(retry, second.JobId);

            await fixture.Repository.CompleteAsync(
                retry, ImportJobStatus.PartiallyCompleted, new ImportCounters(),
                makeEffective: true, businessDate: new DateOnly(2026, 5, 1), ct);

            // 1 May has landed, so 2 May is claimable - the dead attempt is history, not a wall.
            var third = await fixture.Repository.ClaimNextAsync(
                "worker-d", TimeSpan.FromMinutes(5), ct);
            Assert.NotNull(third);
            Assert.Equal(new DateOnly(2026, 5, 2), third!.BusinessDate);
        }
        finally
        {
            await fixture.CleanupAsync(TestSource);
        }
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

            // NO business dates, and that is the point of the test now.
            //
            // This used to enqueue four consecutive DAYS and assert that four workers claimed
            // four of them at once. That property is gone on purpose: days of one source are now
            // strictly serial, because a delivery's mart is "the state at delivery N" and running
            // N+1 alongside or before N writes the wrong state under the right label.
            //
            // What is still true, and is what this test was always really about, is the locking:
            // two workers running this statement at the same instant never receive the same row.
            // Jobs with no business date - the initial dump, the TAC snapshots - do not take part
            // in the ordering rule, so they are the honest place to exercise it.
            for (var i = 0; i < 4; i++)
            {
                var file = await fixture.Repository.RegisterFileAsync(
                    TestSource, $"nodate-{i}.csv", Stored(Hash($"skiplocked-{i}")), "tester", ct);

                await fixture.Repository.EnqueueAsync(
                    TestSource, file.FileId, businessDate: null, "tester", ct: ct);
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
                makeEffective: true, businessDate: date, ct);

            // A corrected file for the same day.
            var correctedFile = await fixture.Repository.RegisterFileAsync(
                TestSource, "day-corrected.csv", Stored(Hash("effective-2")), "tester", ct);
            var correctedJob = await fixture.Repository.EnqueueAsync(
                TestSource, correctedFile.FileId, date, "tester", ct: ct);

            await fixture.Repository.CompleteAsync(
                correctedJob, ImportJobStatus.Completed, new ImportCounters(RowsInserted: 105),
                makeEffective: true, businessDate: date, ct);

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

/// <summary>
/// The read paths the dashboard and Import Center call on every page load.
/// </summary>
/// <remarks>
/// These exist because the queue tests did not cover them, and both shipped broken for the same
/// reason the queue tests had already caught once: PostgreSQL counts in <c>bigint</c>, the models
/// hold small counts as <c>int</c>, and Dapper matches a record constructor on the reader's
/// types. The failure is an <c>InvalidOperationException</c> naming neither the column nor the
/// mismatch, surfacing as a 500 on a page that had been working.
///
/// A test that only asserts "the query runs" is enough to catch every bug of this shape, which
/// is the argument for having one per read method rather than per interesting behaviour.
/// </remarks>
[Collection("import-queue")]
public sealed class ImportReadTests(ImportQueueFixture fixture) : IClassFixture<ImportQueueFixture>
{
    [Fact]
    public async Task Freshness_materialises_for_every_configured_source()
    {
        if (!fixture.IsAvailable)
        {
            Assert.Skip(fixture.UnavailableReason ?? "no database");
            return;
        }

        var today = new DateOnly(2026, 9, 14);
        var freshness = await fixture.Repository
            .GetFreshnessAsync(today, TestContext.Current.CancellationToken).ConfigureAwait(true);

        // Both seeded sources must appear even when neither has imported anything: a source with
        // no data is exactly the case an operator needs to see.
        Assert.Contains(freshness, f => f.SourceCode == "SQM");
        Assert.Contains(freshness, f => f.SourceCode == "TAC");

        foreach (var row in freshness)
        {
            Assert.True(row.FailedLast7Days >= 0);
            Assert.NotNull(row.MissingBusinessDates);

            // Days behind is measured from the business date, not the import time. A file
            // imported an hour ago describing last month is stale data however recently it
            // arrived, and this is the number that has to say so.
            if (row.LatestBusinessDate is { } date)
            {
                Assert.Equal(today.DayNumber - date.DayNumber, row.DaysBehind);
            }
            else
            {
                Assert.Null(row.DaysBehind);
            }
        }
    }

    [Fact]
    public async Task Worker_health_materialises_on_an_idle_queue()
    {
        if (!fixture.IsAvailable)
        {
            Assert.Skip(fixture.UnavailableReason ?? "no database");
            return;
        }

        var health = await fixture.Repository
            .GetWorkerHealthAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(health.Queued >= 0);
        Assert.True(health.Running >= 0);
        Assert.True(health.StaleLeases >= 0);
        Assert.True(health.ActiveWorkers >= 0);
    }

    [Fact]
    public async Task Listing_and_counting_agree_with_each_other()
    {
        if (!fixture.IsAvailable)
        {
            Assert.Skip(fixture.UnavailableReason ?? "no database");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var filter = new ImportHistoryFilter();

        var total = await fixture.Repository.CountJobsAsync(filter, ct).ConfigureAwait(true);
        var page = await fixture.Repository.ListJobsAsync(filter, 500, 0, ct).ConfigureAwait(true);

        // The count drives the pager; the list fills the table. If they disagree the UI shows a
        // page number that cannot be reached.
        Assert.Equal(Math.Min(total, 500), page.Count);

        foreach (var job in page)
        {
            var detail = await fixture.Repository.GetJobAsync(job.JobId, ct).ConfigureAwait(true);

            Assert.NotNull(detail);
            Assert.Equal(job.Status, detail.Summary.Status);
            break; // one is enough to prove the detail projection materialises
        }
    }
}
