using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sqm.Application.DataImport;
using Sqm.Application.Quality;

namespace Sqm.Ingestion.Processing;

/// <summary>
/// Measures each imported day's file and says so on the job when it is unlike the ordinary days.
/// </summary>
/// <remarks>
/// <para>
/// The alarm the import did not have. On 2026-09-15 a quarter of the operator's rows began to
/// carry IMEIs shifted by one digit, and from 2026-07-27 SIMs began to carry several numbers a
/// day; every row was well-formed, so validation passed them all, and the dashboard's SIM and
/// handset changes rose 15-fold and 5-fold before a chart gave it away. Measured here, both would
/// have been on the jobs' timelines as they arrived: shifted IMEIs on the first day's job, multi-number
/// SIMs from the second (measured against the whole history in docs/architecture/15-feed-quality.md).
/// </para>
/// <para>
/// A finding is a note, not a failure. The data is what the operator sent, and refusing it would
/// leave a gap the dashboard counts as a missing day; what the operator must be told is that the
/// day looks wrong, and on what evidence.
/// </para>
/// </remarks>
internal sealed partial class FeedQualityMonitor(
    IAnalyticsIngestionStore analytics,
    IFeedQualityReader reader,
    IOptions<FeedQualityOptions> options,
    ILogger<FeedQualityMonitor> logger)
{
    [LoggerMessage(EventId = 3500, Level = LogLevel.Warning,
        Message = "Feed quality {Date}: {Check} at {Rate:P3}, threshold {Threshold:P3}")]
    private partial void LogFlagged(DateOnly date, string check, double rate, double? threshold);

    /// <summary>Measures the day, judges it, and notes what was found on the job.</summary>
    public async Task AfterDayAsync(DateOnly businessDate, IImportContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!await analytics.RefreshFeedQualityForDayAsync(businessDate, ct).ConfigureAwait(false))
        {
            // Said on every job until it is fixed, rather than failing them: the day's data is
            // sound either way, and a missing check is the operator's to notice, not the import's
            // to refuse over.
            await context.NoteAsync("warning",
                "Feed quality was not measured: its tables do not exist. Apply the analytics "
                + "migrations (020_feed_quality) and run Sqm.Ingestion --refresh-quality.",
                null, ct).ConfigureAwait(false);
            return;
        }

        var report = await FeedQualityReport.BuildAsync(
            reader, options.Value, businessDate, businessDate, ct).ConfigureAwait(false);

        if (report.Days is not [var day])
        {
            await context.NoteAsync("warning",
                $"Feed quality could not be read back for {businessDate:yyyy-MM-dd} after it was measured.",
                null, ct).ConfigureAwait(false);
            return;
        }

        var flagged = day.Findings.Where(f => f.Flagged).ToList();

        if (flagged.Count == 0)
        {
            await context.NoteAsync("info",
                report.Reference.Days == 0
                    ? "Feed quality measured. There are no reference days yet to judge it against."
                    : $"Feed quality: in line with the {report.Reference.Days} reference days on every check.",
                null, ct).ConfigureAwait(false);
            return;
        }

        foreach (var finding in flagged)
        {
            LogFlagged(businessDate, finding.Check.ToString(), finding.Rate, finding.Threshold);

            await context.NoteAsync("warning", "Feed quality: " + finding.Explanation,
                new { check = finding.Check.ToString(), rate = finding.Rate, threshold = finding.Threshold },
                ct).ConfigureAwait(false);
        }
    }
}
