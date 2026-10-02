using Microsoft.Extensions.Logging;
using Sqm.Application.DataImport;

namespace Sqm.Ingestion.Processing;

/// <summary>
/// Writes each imported day's SIM changes for the risk pages, after the feed-quality step that
/// screens them.
/// </summary>
/// <remarks>
/// <para>
/// Order matters: the screen reads the day's list of SIMs carrying several numbers, which the
/// feed-quality step has just written. Run before it, every change on a defective day would pass as
/// clean - exactly the inflation (about 15-fold in September) the screen exists to keep out.
/// </para>
/// <para>
/// Never a failure. The day's data is sound whether or not its risk rows were written; a failure
/// here is noted on the job with the command that writes them, and the import goes on.
/// </para>
/// </remarks>
internal sealed partial class RiskDayStep(
    IAnalyticsIngestionStore analytics,
    ILogger<RiskDayStep> logger)
{
    [LoggerMessage(EventId = 3510, Level = LogLevel.Warning,
        Message = "Risk day step failed for {Date}")]
    private partial void LogFailed(Exception exception, DateOnly date);

    /// <summary>Writes the day's rows and says on the job what was written.</summary>
    public async Task AfterDayAsync(DateOnly businessDate, IImportContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        RiskDayRefresh? written;
        try
        {
            written = await analytics.RefreshRiskDayAsync(businessDate, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(ex, businessDate);
            await context.NoteAsync("warning",
                $"Risk signals: this day's SIM changes were not written ({ex.Message}). The import is complete; "
                + $"write them with Sqm.Ingestion --refresh-risk-days --from {businessDate:yyyy-MM-dd} --to {businessDate:yyyy-MM-dd}.",
                null, ct).ConfigureAwait(false);
            return;
        }

        if (written is null)
        {
            await context.NoteAsync("warning",
                "Risk signals: not deployed, so this day's SIM changes were not recorded. Apply analytics "
                + "migration 023 and run Sqm.Ingestion --refresh-risk-days.",
                null, ct).ConfigureAwait(false);
            return;
        }

        var message = written.QualityMeasured
            ? $"Risk signals: {written.Changes:N0} SIM changes recorded; {written.SetAside:N0} of them set aside "
              + "because the feed listed one of their SIMs under several numbers that day."
            : $"Risk signals: {written.Changes:N0} SIM changes recorded but not screened - this day's feed quality "
              + "was not measured, so none of them will be listed until the day is refreshed.";

        await context.NoteAsync(written.QualityMeasured ? "info" : "warning", message,
            new { riskSimChanges = written.Changes, riskSetAside = written.SetAside }, ct).ConfigureAwait(false);
    }
}
