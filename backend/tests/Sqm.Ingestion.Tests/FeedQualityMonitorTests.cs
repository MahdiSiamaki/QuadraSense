using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Application.DataImport;
using Sqm.Application.Quality;
using Sqm.Domain.Quality;
using Sqm.Ingestion.Processing;

namespace Sqm.Ingestion.Tests;

/// <summary>What an import says about its file, on the job, after measuring it.</summary>
public sealed class FeedQualityMonitorTests
{
    private static readonly DateOnly Sep20 = new(2026, 9, 20);

    private static FeedQualityDay Ordinary(DateOnly date) =>
        // As 20 July measured: 6.5M rows, 3,282 unknown devices, 3,502 malformed, 6,497 shifted, 926 multi-number SIMs.
        new(date, 6_503_281, 5_849_230, 3_282, 3_502, 19_202, 6_497, 926, 3_204, 1);

    /// <summary>One monitor run against stubs, recording the notes it leaves.</summary>
    private sealed class Run
    {
        public bool TablesExist { get; init; } = true;

        public FeedQualityDay? Today { get; init; }

        public List<(string Severity, string Message)> Notes { get; } = [];

        public int Reads { get; private set; }

        public async Task ExecuteAsync()
        {
            var options = new FeedQualityOptions();

            var analytics = Stub.Create<IAnalyticsIngestionStore>((m, _) => m.Name == "RefreshFeedQualityForDayAsync"
                ? Task.FromResult(TablesExist)
                : Stub.Default(m));

            // Thirty ordinary reference days for the reference window; the day itself otherwise.
            var reader = Stub.Create<IFeedQualityReader>((_, args) =>
            {
                Reads++;
                IReadOnlyList<FeedQualityDay> days = (DateOnly?)args[0] == options.ReferenceFrom
                    ? [.. Enumerable.Range(0, 30).Select(i => Ordinary(options.ReferenceFrom.AddDays(i)))]
                    : Today is null ? [] : [Today];
                return Task.FromResult(days);
            });

            var context = Stub.Create<IImportContext>((m, args) =>
            {
                if (m.Name == "NoteAsync")
                {
                    Notes.Add(((string)args[0]!, (string)args[1]!));
                }

                return Stub.Default(m);
            });

            await new FeedQualityMonitor(analytics, reader, Options.Create(options), NullLogger<FeedQualityMonitor>.Instance)
                .AfterDayAsync(Sep20, context, CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_day_with_both_defects_leaves_one_warning_per_defect_with_its_evidence()
    {
        var run = new Run
        {
            Today = new FeedQualityDay(Sep20, 11_953_857, 8_550_042, 404, 5_437, 2_957_257, 2_940_845, 360_910, 1_472_199, 1),
        };

        await run.ExecuteAsync();

        Assert.Equal(2, run.Notes.Count);
        Assert.All(run.Notes, n => Assert.Equal("warning", n.Severity));
        Assert.Contains(run.Notes, n => n.Message.StartsWith("Feed quality: 24.60% of rows (2,940,845)", StringComparison.Ordinal));
        Assert.Contains(run.Notes, n => n.Message.StartsWith("Feed quality: 4.22% of SIMs (360,910)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_ordinary_day_says_so_once()
    {
        var run = new Run { Today = Ordinary(Sep20) };

        await run.ExecuteAsync();

        var note = Assert.Single(run.Notes);
        Assert.Equal("info", note.Severity);
        Assert.Contains("in line with the 30 reference days", note.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_its_tables_it_says_what_to_apply_and_reads_nothing()
    {
        var run = new Run { TablesExist = false, Today = Ordinary(Sep20) };

        await run.ExecuteAsync();

        var note = Assert.Single(run.Notes);
        Assert.Equal("warning", note.Severity);
        Assert.Contains("020_feed_quality", note.Message, StringComparison.Ordinal);
        Assert.Equal(0, run.Reads);
    }
}
