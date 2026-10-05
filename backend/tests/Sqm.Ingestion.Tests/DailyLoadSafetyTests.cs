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

    /// <summary>One import of one file against recording stubs.</summary>
    private sealed class Run(string file)
    {
        public List<string> Calls { get; } = [];

        public List<string> Loaded { get; } = [];

        /// <summary>What the store reports holding after the load; by default, what was sent.</summary>
        public long? StoredCount { get; init; }

        public bool LoadFails { get; init; }

        private long _stored;

        public async Task ProcessAsync()
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
                _ => Stub.Default(m),
            });

            var job = new ClaimedJob(
                1, "SQM", 1, "stored", "daily_subs_device_sim_info_2026-06-15.csv", "sha",
                file.Length, new DateOnly(2026, 6, 15), 1, 3, 0, null);

            await processor.ProcessAsync(job, context, CancellationToken.None);
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
                    return Task.FromResult(Loaded.Count == 0 ? 0L : StoredCount ?? _stored);
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

            if (LoadFails)
            {
                throw new IOException("connection reset mid-insert");
            }

            _stored = text.Split('\n').Count(l => l.Trim('\r').Length > 0) - 1;
            return 0;
        }
    }
}
