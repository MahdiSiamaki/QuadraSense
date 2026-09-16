using Microsoft.Extensions.Options;
using Sqm.Application.DataImport;
using Sqm.Ingestion.Processing;

namespace Sqm.Ingestion;

/// <summary>
/// Claims import jobs and runs them.
/// </summary>
/// <remarks>
/// <para>
/// The loop is deliberately dull: ask for a job, run it, record what happened, ask again. Every
/// piece of state that matters lives in the database, so a worker that dies is not a worker whose
/// in-flight knowledge is lost - it is a lease that stops being renewed.
/// </para>
/// <para>
/// The worker polls rather than listening. PostgreSQL's LISTEN/NOTIFY would remove the poll
/// interval, but at roughly one file a day per source a five-second delay is not a problem worth
/// a persistent notification connection and its reconnection handling.
/// </para>
/// </remarks>
public sealed partial class ImportWorker(
    IImportJobRepository repository,
    IImportFileStore fileStore,
    IEnumerable<IImportProcessor> processors,
    IOptions<ImportWorkerOptions> options,
    ILogger<ImportWorker> logger) : BackgroundService
{
    [LoggerMessage(EventId = 3400, Level = LogLevel.Information,
        Message = "Import worker {WorkerId} started; {ProcessorCount} processor(s), "
                  + "{Concurrency} concurrent job(s)")]
    private partial void LogStarted(string workerId, int processorCount, int concurrency);

    [LoggerMessage(EventId = 3401, Level = LogLevel.Information,
        Message = "Job {JobId} finished as {Status} in {ElapsedMs} ms")]
    private partial void LogFinished(long jobId, string status, long elapsedMs);

    [LoggerMessage(EventId = 3402, Level = LogLevel.Error,
        Message = "Job {JobId} threw")]
    private partial void LogJobFailed(long jobId, Exception exception);

    [LoggerMessage(EventId = 3403, Level = LogLevel.Warning,
        Message = "Job {JobId} lost its lease and is being abandoned; another worker will take it")]
    private partial void LogLeaseLost(long jobId);

    [LoggerMessage(EventId = 3404, Level = LogLevel.Information,
        Message = "Import worker stopping; waiting up to {GraceSeconds}s for {Running} running job(s)")]
    private partial void LogStopping(double graceSeconds, int running);

    [LoggerMessage(EventId = 3405, Level = LogLevel.Warning,
        Message = "Could not record the worker heartbeat; the Import Center may report this "
                  + "worker as absent while it is in fact running")]
    private partial void LogHeartbeatFailed(Exception exception);

    private readonly ImportWorkerOptions _options = options.Value;
    private readonly bool _hostedInApi = options.Value.HostedInApi;
    private readonly Dictionary<string, IImportProcessor> _processors =
        processors.ToDictionary(p => p.SourceCode, StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(_options.WorkerId, _processors.Count, _options.MaxConcurrentJobs);

        using var slots = new SemaphoreSlim(_options.MaxConcurrentJobs, _options.MaxConcurrentJobs);
        var running = new List<Task>();

        // The heartbeat runs on its OWN loop, and that is not an implementation preference.
        //
        // The claim loop below blocks on slots.WaitAsync whenever every slot is busy - which,
        // with the default of one concurrent job, is the entire duration of an import. A
        // heartbeat written from inside that loop would stop for the ten minutes a daily file
        // takes, and the UI would report the worker dead at exactly the moment it was working
        // hardest. Beating from a separate task is what makes "last seen" mean "alive" rather
        // than "idle".
        var heartbeat = HeartbeatAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            running.RemoveAll(t => t.IsCompleted);

            await slots.WaitAsync(stoppingToken).ConfigureAwait(false);

            ClaimedJob? job;
            try
            {
                job = await repository
                    .ClaimNextAsync(_options.WorkerId, _options.LeaseDuration, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The operational database is unreachable. Releasing the slot and waiting is the
                // right response: the jobs are still there, and hammering a database that is
                // down helps nobody.
                slots.Release();
                LogJobFailed(0, ex);
                await DelayAsync(_options.PollInterval, stoppingToken).ConfigureAwait(false);
                continue;
            }

            if (job is null)
            {
                slots.Release();
                await DelayAsync(_options.PollInterval, stoppingToken).ConfigureAwait(false);
                continue;
            }

            running.Add(RunAndReleaseAsync(job, slots, stoppingToken));
        }

        var stillRunning = running.Count(t => !t.IsCompleted);
        LogStopping(_options.ShutdownGrace.TotalSeconds, stillRunning);

        // Give running jobs a chance to finish cleanly. Anything still going when the grace
        // expires is simply left: its lease stops being renewed, the recovery sweep returns it to
        // the queue, and because every import is idempotent at day granularity, re-running it is
        // safe. That is why shutdown can be a timeout rather than a negotiation.
        await Task.WhenAny(
            Task.WhenAll(running),
            Task.Delay(_options.ShutdownGrace, CancellationToken.None)).ConfigureAwait(false);

        await heartbeat.ConfigureAwait(false);
    }

    /// <summary>
    /// Says "I am here" on a fixed beat, whatever the claim loop is doing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The absence of these rows is what lets the Import Center distinguish a queue that is busy
    /// from a queue nobody is serving. It exists because a 319.6 MB upload sat at "Queued" with
    /// every component reporting success and nothing saying that no worker was running.
    /// </para>
    /// <para>
    /// A failure here is logged and swallowed. A worker that cannot write its heartbeat can still
    /// import files perfectly well, and stopping real work because a status row could not be
    /// updated would turn a monitoring problem into an outage.
    /// </para>
    /// </remarks>
    private async Task HeartbeatAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await repository.RecordHeartbeatAsync(
                    _options.WorkerId,
                    Environment.MachineName,
                    _options.MaxConcurrentJobs,
                    _hostedInApi,
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogHeartbeatFailed(ex);
            }

            await DelayAsync(_options.PollInterval, ct).ConfigureAwait(false);
        }
    }

    private async Task RunAndReleaseAsync(
        ClaimedJob job, SemaphoreSlim slots, CancellationToken stoppingToken)
    {
        try
        {
            await RunJobAsync(job, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            slots.Release();
        }
    }

    private async Task RunJobAsync(ClaimedJob job, CancellationToken stoppingToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Two reasons to stop: the process is shutting down, or this worker lost the lease. The
        // linked token lets the processor observe either one through the same mechanism.
        using var leaseLost = new CancellationTokenSource();
        using var linked = CancellationTokenSource
            .CreateLinkedTokenSource(stoppingToken, leaseLost.Token);

        var renewal = RenewLeaseUntilDoneAsync(job, leaseLost, linked.Token);

        try
        {
            if (!_processors.TryGetValue(job.SourceCode, out var processor))
            {
                await repository.FailAsync(
                    job.JobId,
                    $"No processor is registered for data source '{job.SourceCode}'.",
                    isRetryable: false, TimeSpan.Zero, stoppingToken).ConfigureAwait(false);
                return;
            }

            var context = new ImportContext(repository, fileStore, job);
            var outcome = await processor.ProcessAsync(job, context, linked.Token).ConfigureAwait(false);

            await repository.AppendEventAsync(
                job.JobId, outcome.Status is ImportJobStatus.Completed ? "info" : "warning",
                null, outcome.Message, null, stoppingToken).ConfigureAwait(false);

            if (outcome.Status is ImportJobStatus.Cancelled)
            {
                await repository.MarkCancelledAsync(job.JobId, stoppingToken).ConfigureAwait(false);
            }
            else
            {
                await repository.CompleteAsync(
                    job.JobId, outcome.Status, outcome.Counters, outcome.MakeEffective,
                    outcome.BusinessDate, stoppingToken).ConfigureAwait(false);
            }

            stopwatch.Stop();
            var status = outcome.Status.ToDatabaseValue();
            LogFinished(job.JobId, status, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (leaseLost.IsCancellationRequested)
        {
            // Do not touch the job record. Another worker owns it now, and writing a status from
            // here would overwrite whatever that worker has done since.
            LogLeaseLost(job.JobId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown. The lease will expire and the recovery sweep requeues the job.
        }
        catch (Exception ex)
        {
            LogJobFailed(job.JobId, ex);
            await HandleFailureAsync(job, ex).ConfigureAwait(false);
        }
        finally
        {
            leaseLost.Cancel();
            try
            {
                await renewal.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: cancelling the renewal loop is how it is stopped.
            }
        }
    }

    private async Task HandleFailureAsync(ClaimedJob job, Exception exception)
    {
        var (retryable, summary) = ImportFailure.Classify(exception);
        var delay = BackoffFor(job.Attempt);

        // Recording the failure must not itself be cancellable by the shutdown token: a job that
        // failed and was never marked failed would sit in a working status until its lease
        // expired, which is a worse outcome than one extra database write during shutdown.
        await repository.FailAsync(job.JobId, summary, retryable, delay, CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>Exponential backoff, capped.</summary>
    /// <remarks>
    /// The cap matters more than the curve. Most retryable failures here are transient - the
    /// analytics server restarting, a lock held a moment too long - and doubling past a few
    /// minutes only delays recovery from something that fixed itself long ago.
    /// </remarks>
    private TimeSpan BackoffFor(int attempt)
    {
        var multiplier = Math.Pow(2, Math.Max(0, attempt - 1));
        var delay = _options.RetryBaseDelay * multiplier;
        var cap = TimeSpan.FromMinutes(10);
        return delay > cap ? cap : delay;
    }

    /// <summary>
    /// Extends the lease while the job runs, and signals if the claim is ever lost.
    /// </summary>
    private async Task RenewLeaseUntilDoneAsync(
        ClaimedJob job, CancellationTokenSource leaseLost, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_options.LeaseRenewInterval, ct).ConfigureAwait(false);

                var held = await repository
                    .RenewLeaseAsync(job.JobId, _options.WorkerId, _options.LeaseDuration, ct)
                    .ConfigureAwait(false);

                if (!held)
                {
                    await leaseLost.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The job finished and cancelled this loop. Nothing to do.
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
