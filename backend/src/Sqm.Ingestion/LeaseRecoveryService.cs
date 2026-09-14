using Microsoft.Extensions.Options;
using Sqm.Application.DataImport;

namespace Sqm.Ingestion;

/// <summary>
/// Returns jobs to the queue when the worker holding them stopped renewing its lease.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of the crash-recovery story, and it is deliberately small. There is no
/// heartbeat table, no worker registry and no leader election: a worker's liveness is expressed
/// only as a timestamp on the job it holds, so the sweep is one UPDATE against an index.
/// </para>
/// <para>
/// It runs in every worker rather than in one designated process. The statement is idempotent and
/// races harmlessly - two workers running it at the same moment recover the same jobs once,
/// because the UPDATE's own predicate stops matching as soon as either commits - and that removes
/// the need for a special process whose own failure would need recovering.
/// </para>
/// </remarks>
public sealed partial class LeaseRecoveryService(
    IImportJobRepository repository,
    IOptions<ImportWorkerOptions> options,
    ILogger<LeaseRecoveryService> logger) : BackgroundService
{
    [LoggerMessage(EventId = 3500, Level = LogLevel.Error,
        Message = "Lease recovery sweep failed")]
    private partial void LogSweepFailed(Exception exception);

    private readonly ImportWorkerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.LeaseRecoveryInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await repository.RecoverExpiredLeasesAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed sweep is not a reason to stop sweeping. The next tick tries again.
                LogSweepFailed(ex);
            }
        }
    }
}
