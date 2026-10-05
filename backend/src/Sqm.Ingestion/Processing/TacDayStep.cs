using Microsoft.Extensions.Logging;
using Sqm.Application.DataImport;

namespace Sqm.Ingestion.Processing;

/// <summary>
/// Writes each imported day's per-model counts (analytics migration 025), from which a model's first
/// appearance and network age are read.
/// </summary>
/// <remarks>
/// Never a failure, like the risk day step: the day's data is sound whether or not its model counts
/// were written. A failure is noted on the job with the command that writes them.
/// </remarks>
internal sealed partial class TacDayStep(
    IAnalyticsIngestionStore analytics,
    ILogger<TacDayStep> logger)
{
    [LoggerMessage(EventId = 3530, Level = LogLevel.Warning,
        Message = "Model day step failed for {Date}")]
    private partial void LogFailed(Exception exception, DateOnly date);

    /// <summary>Writes the day's model counts and says on the job what was written.</summary>
    public async Task AfterDayAsync(DateOnly businessDate, IImportContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        TacDayRefresh? written;
        try
        {
            written = await analytics.RefreshTacDayAsync(businessDate, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(ex, businessDate);
            await context.NoteAsync("warning",
                $"New models: this day's model counts were not written ({ex.Message}). The import is complete; "
                + $"write them with Sqm.Ingestion --refresh-tac-days --from {businessDate:yyyy-MM-dd} --to {businessDate:yyyy-MM-dd}.",
                null, ct).ConfigureAwait(false);
            return;
        }

        if (written is null)
        {
            await context.NoteAsync("warning",
                "New models: not deployed, so this day's model counts were not recorded. Apply analytics "
                + "migration 025 and run Sqm.Ingestion --refresh-tac-days.",
                null, ct).ConfigureAwait(false);
            return;
        }

        await context.NoteAsync("info",
            $"New models: {written.Models:N0} models seen, {written.Imeis:N0} handsets (IMEIs).",
            new { tacDayModels = written.Models, tacDayImeis = written.Imeis }, ct).ConfigureAwait(false);
    }
}
