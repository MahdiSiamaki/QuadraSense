using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Application.DataImport;
using Sqm.Application.Quality;
using Sqm.Ingestion.Processing;

namespace Sqm.Ingestion.Tests;

/// <summary>
/// Every daily import brings the binding history up to date after the day is folded, and says on
/// the job which path it took - and a worker ahead of migration 022 warns rather than fails.
/// </summary>
/// <remarks>
/// The real processor with recording stubs, stopped at the feed-quality step that follows. What
/// the history does with the day is proven against ClickHouse in BindingHistoryTests.
/// </remarks>
public sealed class HistoryStepTests
{
    private const string File = "msisdn,imsi,imei,label\n9121234567,432110123456789,35085748000001,add\n";

    [Theory]
    [InlineData(HistoryRefreshKind.Added, "info", "1 events added to the binding history.")]
    [InlineData(HistoryRefreshKind.RebuiltMonth, "info", "The binding history for 2026-06 was rebuilt from the event log (a corrected file): 1 events.")]
    [InlineData(HistoryRefreshKind.NotDeployed, "warning", "The binding history is not deployed, so timelines will not include this day.")]
    public async Task The_history_step_runs_after_the_fold_and_marts_and_says_what_it_did(
        HistoryRefreshKind kind, string level, string message)
    {
        var calls = new List<string>();
        var notes = new List<(string Level, string Message)>();

        var analytics = Stub.Create<IAnalyticsIngestionStore>((m, args) =>
        {
            calls.Add(m.Name);
            return m.Name switch
            {
                "CountEventsForDateAsync" => Task.FromResult(calls.Contains("LoadDailyEventsAsync") ? 1L : 0L),
                "LoadDailyEventsAsync" => Task.FromResult(1L),
                "FoldDayAsync" => Task.FromResult(1L),
                "RefreshHistoryForDayAsync" => Task.FromResult(new HistoryRefresh(
                    kind, kind == HistoryRefreshKind.NotDeployed ? 0 : 1,
                    kind == HistoryRefreshKind.RebuiltMonth ? "a corrected file" : null)),
                "RefreshFeedQualityForDayAsync" => throw new OperationCanceledException("stop after the history"),
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
            NullLogger<SqmDailyProcessor>.Instance);

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

        var job = new ClaimedJob(
            1, "SQM", 1, "stored", "daily_subs_device_sim_info_2026-06-15.csv", "sha",
            File.Length, new DateOnly(2026, 6, 15), 1, 3, 0, null);

        await Assert.ThrowsAsync<OperationCanceledException>(() => processor.ProcessAsync(job, context, CancellationToken.None));

        var order = calls.Where(c => c is "FoldDayAsync" or "RefreshChangeMartsForDayAsync" or "RefreshHistoryForDayAsync").ToList();
        Assert.Equal(["FoldDayAsync", "RefreshChangeMartsForDayAsync", "RefreshHistoryForDayAsync"], order);

        var note = Assert.Single(notes, n => n.Message.Contains("binding history", StringComparison.Ordinal));
        Assert.Equal(level, note.Level);
        Assert.StartsWith(message, note.Message, StringComparison.Ordinal);
    }
}
