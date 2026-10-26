using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Application.DataImport;
using Sqm.Application.Quality;
using Sqm.Ingestion.Processing;

namespace Sqm.Ingestion.Tests;

/// <summary>
/// A daily file lands whole or not at all, and ClickHouse is never handed a shape it misreads.
/// </summary>
/// <remarks>
/// <para>
/// Three holes, each measured on ClickHouse 26.7 before it was closed. A blank line anywhere in a
/// CSV stream fails the insert after the blocks before it are written. Lines ending in a bare CR
/// import zero rows without an error. And the stored-count check ran after the fold and both mart
/// rebuilds, so a file ClickHouse read differently from the validator was published first and
/// failed second - and the failure removed nothing.
/// </para>
/// <para>
/// Drives the real processor with recording stubs; no ClickHouse is needed. Every path here
/// stops before the marts run.
/// </para>
/// </remarks>
public sealed class DailyLoadSafetyTests
{
    private const string Header = "msisdn,imsi,imei,label";
    private const string Row1 = "9121234567,432110123456789,35085748000001,add";
    private const string Row2 = "9121234568,432110123456780,35085748000002,remove";

    [Theory]
    [InlineData("blank line in the middle", Header + "\n" + Row1 + "\n\n" + Row2 + "\n")]
    [InlineData("doubled final newline", Header + "\n" + Row1 + "\n" + Row2 + "\n\n")]
    [InlineData("bare CR endings", Header + "\r" + Row1 + "\r" + Row2 + "\r")]
    public async Task A_shape_ClickHouse_misreads_is_rewritten_before_it_is_sent(string _, string file)
    {
        var run = new Run(file);

        await Assert.ThrowsAsync<OperationCanceledException>(run.ProcessAsync); // the stop at the fold

        var sent = Assert.Single(run.Loaded);
        Assert.Equal($"{Header}\n{Row1}\n{Row2}\n", sent.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_ordinary_file_is_streamed_as_it_is()
    {
        var file = $"{Header}\r\n{Row1}\r\n{Row2}\r\n";
        var run = new Run(file);

        await Assert.ThrowsAsync<OperationCanceledException>(run.ProcessAsync); // the stop at the fold

        Assert.Equal(file, Assert.Single(run.Loaded));
    }

    [Fact]
    public async Task A_file_whose_header_appends_a_column_is_loaded_not_quarantined()
    {
        var file = $"{Header},region\n{Row1},north\n{Row2},south\n";
        var run = new Run(file);

        await Assert.ThrowsAsync<OperationCanceledException>(run.ProcessAsync); // the stop at the fold

        Assert.Equal(file, Assert.Single(run.Loaded));
    }

    [Fact]
    public async Task A_count_mismatch_removes_the_day_before_anything_is_derived_from_it()
    {
        var run = new Run($"{Header}\n{Row1}\n{Row2}\n") { StoredCount = 1 };

        await Assert.ThrowsAsync<InvalidOperationException>(run.ProcessAsync);

        Assert.Contains("RemoveDayAsync", run.Calls);
        Assert.DoesNotContain("FoldDayAsync", run.Calls);
        Assert.DoesNotContain("RefreshChangeMartsForDayAsync", run.Calls);
    }

    [Fact]
    public async Task A_load_that_fails_partway_removes_what_it_wrote()
    {
        var run = new Run($"{Header}\n{Row1}\n{Row2}\n") { LoadFails = true };

        await Assert.ThrowsAsync<IOException>(run.ProcessAsync);

        Assert.Equal("RemoveDayAsync", run.Calls[^1]);
        Assert.DoesNotContain("FoldDayAsync", run.Calls);
    }

    [Fact]
    public async Task An_operators_cancel_during_the_load_removes_what_it_wrote_and_ends_cancelled()
    {
        var run = new Run($"{Header}\n{Row1}\n{Row2}\n")
        {
            StopDuringLoad = true,
            CancelledByOperator = true,
        };

        var outcome = await run.ProcessAsync();

        Assert.Equal(ImportJobStatus.Cancelled, outcome!.Status);
        Assert.Equal("RemoveDayAsync", run.Calls[^1]);
        Assert.DoesNotContain("FoldDayAsync", run.Calls);
        Assert.Equal("Cancelled while loading; what it had written was removed.", outcome.Message);
    }

    [Fact]
    public async Task A_load_cut_off_by_the_cancel_with_an_IO_error_is_still_the_cancel()
    {
        // An insert aborted mid-request surfaces as an I/O error as often as a cancellation.
        var run = new Run($"{Header}\n{Row1}\n{Row2}\n")
        {
            StopDuringLoad = true,
            LoadThrows = new IOException("request aborted"),
            CancelledByOperator = true,
        };

        var outcome = await run.ProcessAsync();

        Assert.Equal(ImportJobStatus.Cancelled, outcome!.Status);
        Assert.Equal("RemoveDayAsync", run.Calls[^1]);
    }

    [Fact]
    public async Task A_shutdown_during_the_load_leaves_the_day_for_the_next_attempt_to_clear()
    {
        // Not the operator: the job runs again, and its prepare stage removes the partial day.
        var run = new Run($"{Header}\n{Row1}\n{Row2}\n")
        {
            StopDuringLoad = true,
        };

        await Assert.ThrowsAsync<OperationCanceledException>(run.ProcessAsync);

        Assert.Equal("LoadDailyEventsAsync", run.Calls[^1]);
    }

    [Fact]
    public async Task A_cancel_that_came_before_the_fold_stops_at_the_point_of_no_return()
    {
        var run = new Run($"{Header}\n{Row1}\n{Row2}\n") { GateOpen = false };

        var outcome = await run.ProcessAsync();

        Assert.Equal(ImportJobStatus.Cancelled, outcome!.Status);
        Assert.Equal("RemoveDayAsync", run.Calls[^1]);
        Assert.DoesNotContain("FoldDayAsync", run.Calls);
    }

    [Fact]
    public async Task A_cancel_while_replacing_a_day_says_the_day_is_now_empty()
    {
        var run = new Run($"{Header}\n{Row1}\n{Row2}\n")
        {
            ExistingBefore = 7_543_114,
            StopDuringLoad = true,
            CancelledByOperator = true,
        };

        var outcome = await run.ProcessAsync();

        Assert.Contains("7,543,114 events from an earlier import", outcome!.Message, StringComparison.Ordinal);
        Assert.Contains("the day is empty until a file for it is imported again", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>One import of one file against recording stubs.</summary>
    private sealed class Run(string file)
    {
        public List<string> Calls { get; } = [];

        public List<string> Loaded { get; } = [];

        /// <summary>What the store reports holding after the load; by default, what was sent.</summary>
        public long? StoredCount { get; init; }

        public bool LoadFails { get; init; }

        /// <summary>What the load throws, when it should fail some other way than LoadFails.</summary>
        public Exception? LoadThrows { get; init; }

        /// <summary>
        /// Cancels the processor's token mid-load and throws, as a shutdown or an operator's cancel
        /// does (the worker links both into the one token); LoadThrows, if set, is thrown instead.
        /// </summary>
        public bool StopDuringLoad { get; init; }

        /// <summary>What the context says about who stopped the job.</summary>
        public bool CancelledByOperator { get; init; }

        /// <summary>Whether the point of no return lets the job through - false when a cancel came first.</summary>
        public bool GateOpen { get; init; } = true;

        /// <summary>Events the day held before this file, from an earlier import.</summary>
        public long ExistingBefore { get; init; }

        private long _stored;

        /// <summary>Cancels the token the processor was given; set while ProcessAsync runs.</summary>
        private Func<Task> _stop = () => Task.CompletedTask;

        public async Task<ImportOutcome?> ProcessAsync()
        {
            var analytics = Stub.Create<IAnalyticsIngestionStore>(Analytics);
            var repository = Stub.Create<IImportJobRepository>((m, _) => m.Name == "ResolveSchemaAsync"
                ? Task.FromResult(new SchemaResolution(SchemaVerdict.Known, 1, "v1", "known"))
                : Stub.Default(m));
            var processor = new SqmDailyProcessor(
                analytics,
                repository,
                new DashboardSnapshot(
                    analytics, repository,
                    new MartRefresh(analytics, NullLogger<MartRefresh>.Instance),
                    NullLogger<DashboardSnapshot>.Instance),
                new FeedQualityMonitor(
                    analytics, Stub.Create<IFeedQualityReader>((m, _) => Stub.Default(m)),
                    Options.Create(new FeedQualityOptions()), NullLogger<FeedQualityMonitor>.Instance),
                new RiskDayStep(analytics, NullLogger<RiskDayStep>.Instance),
                new TacDayStep(analytics, NullLogger<TacDayStep>.Instance),
                NullLogger<SqmDailyProcessor>.Instance);

            var context = Stub.Create<IImportContext>((m, _) => m.Name switch
            {
                "OpenFileAsync" => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(file))),
                "IsCancellationRequestedAsync" => Task.FromResult(false),
                "EnterStageUnlessCancelledAsync" => Task.FromResult(GateOpen),
                "get_CancelledByOperator" => CancelledByOperator,
                _ => Stub.Default(m),
            });

            var job = new ClaimedJob(
                1, "SQM", 1, "stored", "daily_subs_device_sim_info_2026-06-15.csv", "sha",
                file.Length, new DateOnly(2026, 6, 15), 1, 3, 0, null);

            using var stop = new CancellationTokenSource();
            _stop = stop.CancelAsync;
            return await processor.ProcessAsync(job, context, stop.Token);
        }

        private object? Analytics(MethodInfo method, object?[] args)
        {
            Calls.Add(method.Name);

            switch (method.Name)
            {
                case "LoadDailyEventsAsync":
                    return LoadAsync((Stream)args[2]!);
                case "CountEventsForDateAsync":
                    // Before the load: nothing there yet, so nothing to replace.
                    return Task.FromResult(Loaded.Count == 0 ? ExistingBefore : StoredCount ?? _stored);
                case "FoldDayAsync":
                    // Stop here: past this point the path under test has been decided.
                    throw new OperationCanceledException("stop after the fold");
                default:
                    return Stub.Default(method);
            }
        }

        private async Task<long> LoadAsync(Stream csv)
        {
            using var reader = new StreamReader(csv, Encoding.UTF8);
            var text = await reader.ReadToEndAsync();
            Loaded.Add(text);

            if (StopDuringLoad)
            {
                await _stop();
                throw LoadThrows ?? new OperationCanceledException("stopped mid-load");
            }

            if (LoadFails)
            {
                throw new IOException("connection reset mid-insert");
            }

            _stored = text.Split('\n').Count(l => l.Trim('\r').Length > 0) - 1;
            return 0;
        }
    }
}
