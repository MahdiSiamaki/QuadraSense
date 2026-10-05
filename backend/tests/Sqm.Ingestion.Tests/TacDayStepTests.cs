using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Application.DataImport;
using Sqm.Application.Quality;
using Sqm.Ingestion.Processing;

namespace Sqm.Ingestion.Tests;

/// <summary>
/// Each import writes its day's per-model counts after the risk day step, says on the job what it
/// wrote - and never fails the import over them.
/// </summary>
/// <remarks>What the rows hold is proven against ClickHouse in ModelArrivalTests.</remarks>
public sealed class TacDayStepTests
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

    private static TacDayStep Step(Func<object?> refresh) => new(
        Stub.Create<IAnalyticsIngestionStore>((m, _) => m.Name == "RefreshTacDayAsync" ? refresh() : Stub.Default(m)),
        NullLogger<TacDayStep>.Instance);

    [Fact]
    public async Task A_written_day_is_noted_with_its_models_and_handsets()
    {
        var (context, notes) = Context();

        await Step(() => Task.FromResult<TacDayRefresh?>(new TacDayRefresh(41_177, 6_583_644)))
            .AfterDayAsync(new DateOnly(2026, 6, 15), context, CancellationToken.None);

        var note = Assert.Single(notes);
        Assert.Equal(("info", "New models: 41,177 models seen, 6,583,644 handsets (IMEIs)."), note);
    }

    [Fact]
    public async Task A_failure_is_a_note_with_the_command_that_repairs_it_and_the_import_goes_on()
    {
        var (context, notes) = Context();

        await Step(() => Task.FromException<TacDayRefresh?>(new InvalidOperationException("memory limit exceeded")))
            .AfterDayAsync(new DateOnly(2026, 9, 20), context, CancellationToken.None);

        var note = Assert.Single(notes);
        Assert.Equal("warning", note.Level);
        Assert.Contains("--refresh-tac-days --from 2026-09-20 --to 2026-09-20", note.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_worker_ahead_of_the_migration_warns_rather_than_fails()
    {
        var (context, notes) = Context();

        await Step(() => Task.FromResult<TacDayRefresh?>(null)).AfterDayAsync(new DateOnly(2026, 9, 20), context, CancellationToken.None);

        Assert.Contains("migration 025", Assert.Single(notes).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Last of the day-level steps: after history, feed quality and the risk day, so a failure of the
    /// model counts can never hold back the steps the risk pages depend on.
    /// </summary>
    [Fact]
    public async Task The_model_step_runs_after_the_risk_step()
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
                "RefreshTacDayAsync" => Task.FromException<TacDayRefresh?>(new OperationCanceledException("stop after the model step")),
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
            new TacDayStep(analytics, NullLogger<TacDayStep>.Instance),
            NullLogger<SqmDailyProcessor>.Instance);

        var (context, _) = Context();
        var job = new ClaimedJob(
            1, "SQM", 1, "stored", "daily_subs_device_sim_info_2026-06-15.csv", "sha",
            File.Length, new DateOnly(2026, 6, 15), 1, 3, 0, null);

        await Assert.ThrowsAsync<OperationCanceledException>(() => processor.ProcessAsync(job, context, CancellationToken.None));

        var order = calls.Where(c => c is "RefreshHistoryForDayAsync" or "RefreshFeedQualityForDayAsync" or "RefreshRiskDayAsync" or "RefreshTacDayAsync").ToList();
        Assert.Equal(["RefreshHistoryForDayAsync", "RefreshFeedQualityForDayAsync", "RefreshRiskDayAsync", "RefreshTacDayAsync"], order);
    }
}
