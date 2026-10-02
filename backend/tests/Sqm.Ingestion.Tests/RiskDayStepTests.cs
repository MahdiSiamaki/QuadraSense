using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Application.DataImport;
using Sqm.Application.Quality;
using Sqm.Ingestion.Processing;

namespace Sqm.Ingestion.Tests;

/// <summary>
/// Each import writes its day's SIM changes for the risk pages after feed quality has screened them,
/// says on the job what it wrote - and never fails the import over them.
/// </summary>
/// <remarks>What the rows hold is proven against ClickHouse in RiskDayTests.</remarks>
public sealed class RiskDayStepTests
{
    private const string File = "msisdn,imsi,imei,label\n9121234567,432110123456789,35085748000001,add\n";

    private static (IImportContext Context, List<(string Level, string Message)> Notes) Context()
    {
        var notes = new List<(string Level, string Message)>();
        var context = Stub.Create<IImportContext>((m, args) =>
        {
            if (m.Name == "NoteAsync")
            {
                notes.Add(((string)args[0]!, (string)args[1]!));
            }

            return m.Name switch
            {
                "OpenFileAsync" => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(File))),
                "IsCancellationRequestedAsync" => Task.FromResult(false),
                _ => Stub.Default(m),
            };
        });

        return (context, notes);
    }

    private static RiskDayStep Step(Func<object?> refresh) => new(
        Stub.Create<IAnalyticsIngestionStore>((m, _) => m.Name == "RefreshRiskDayAsync" ? refresh() : Stub.Default(m)),
        NullLogger<RiskDayStep>.Instance);

    [Fact]
    public async Task A_written_day_is_noted_with_its_changes_and_what_was_set_aside()
    {
        var (context, notes) = Context();

        await Step(() => Task.FromResult<RiskDayRefresh?>(new RiskDayRefresh(102_345, 14_007, true)))
            .AfterDayAsync(new DateOnly(2026, 9, 20), context, CancellationToken.None);

        var note = Assert.Single(notes);
        Assert.Equal("info", note.Level);
        Assert.Equal("Risk signals: 102,345 SIM changes recorded; 14,007 of them set aside because the feed listed "
            + "one of their SIMs under several numbers that day.", note.Message);
    }

    [Fact]
    public async Task An_unscreened_day_warns_that_nothing_will_be_listed()
    {
        var (context, notes) = Context();

        await Step(() => Task.FromResult<RiskDayRefresh?>(new RiskDayRefresh(9, 9, false)))
            .AfterDayAsync(new DateOnly(2026, 9, 20), context, CancellationToken.None);

        var note = Assert.Single(notes);
        Assert.Equal("warning", note.Level);
        Assert.Contains("not screened", note.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_worker_ahead_of_the_migration_warns_rather_than_fails()
    {
        var (context, notes) = Context();

        await Step(() => Task.FromResult<RiskDayRefresh?>(null))
            .AfterDayAsync(new DateOnly(2026, 9, 20), context, CancellationToken.None);

        var note = Assert.Single(notes);
        Assert.Equal("warning", note.Level);
        Assert.Contains("migration 023", note.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_is_a_note_with_the_command_that_repairs_it_and_the_import_goes_on()
    {
        var (context, notes) = Context();

        await Step(() => Task.FromException<RiskDayRefresh?>(new InvalidOperationException("memory limit exceeded")))
            .AfterDayAsync(new DateOnly(2026, 9, 20), context, CancellationToken.None);

        var note = Assert.Single(notes);
        Assert.Equal("warning", note.Level);
        Assert.Contains("(memory limit exceeded)", note.Message, StringComparison.Ordinal);
        Assert.Contains("--refresh-risk-days --from 2026-09-20 --to 2026-09-20", note.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        var (context, _) = Context();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Step(() => Task.FromException<RiskDayRefresh?>(new OperationCanceledException()))
                .AfterDayAsync(new DateOnly(2026, 9, 20), context, CancellationToken.None));
    }

    /// <summary>
    /// The screen reads the day's multi-number SIM list, which the feed-quality step writes: run
    /// first, every change on a defective day would pass as clean.
    /// </summary>
    [Fact]
    public async Task The_risk_step_runs_after_feed_quality_and_before_the_dashboard()
    {
        var calls = new List<string>();
        var analytics = Stub.Create<IAnalyticsIngestionStore>((m, _) =>
        {
            calls.Add(m.Name);
            return m.Name switch
            {
                "CountEventsForDateAsync" => Task.FromResult(calls.Contains("LoadDailyEventsAsync") ? 1L : 0L),
                "LoadDailyEventsAsync" => Task.FromResult(1L),
                "FoldDayAsync" => Task.FromResult(1L),
                "RefreshHistoryForDayAsync" => Task.FromResult(HistoryRefresh.NotDeployed),
                "RefreshFeedQualityForDayAsync" => Task.FromResult(false),
                "RefreshRiskDayAsync" => Task.FromException<RiskDayRefresh?>(new OperationCanceledException("stop after the risk step")),
                _ => Stub.Default(m),
            };
        });

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
            NullLogger<SqmDailyProcessor>.Instance);

        var (context, _) = Context();
        var job = new ClaimedJob(
            1, "SQM", 1, "stored", "daily_subs_device_sim_info_2026-06-15.csv", "sha",
            File.Length, new DateOnly(2026, 6, 15), 1, 3, 0, null);

        await Assert.ThrowsAsync<OperationCanceledException>(() => processor.ProcessAsync(job, context, CancellationToken.None));

        var order = calls.Where(c => c is "RefreshHistoryForDayAsync" or "RefreshFeedQualityForDayAsync" or "RefreshRiskDayAsync").ToList();
        Assert.Equal(["RefreshHistoryForDayAsync", "RefreshFeedQualityForDayAsync", "RefreshRiskDayAsync"], order);
    }
}
