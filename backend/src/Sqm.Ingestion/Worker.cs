namespace Sqm.Ingestion;

/// <summary>
/// Placeholder for the ingestion pipeline worker (Phase 3).
/// </summary>
/// <remarks>
/// The real implementation watches for batch folders, gates on the <c>1.done</c> sentinel, validates
/// against the <c>1.config</c> column contract, hashes each file for idempotency, folds events into
/// current state, and refreshes the marts. See <c>docs/adr/ADR-004-ingestion-strategy.md</c>.
/// </remarks>
public sealed partial class Worker(ILogger<Worker> logger) : BackgroundService
{
    [LoggerMessage(EventId = 2000, Level = LogLevel.Information,
        Message = "Ingestion worker started; batch watching is not yet implemented")]
    private partial void LogStarted();

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted();
        return Task.CompletedTask;
    }
}
