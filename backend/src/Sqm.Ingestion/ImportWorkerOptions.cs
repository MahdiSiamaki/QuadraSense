namespace Sqm.Ingestion;

/// <summary>Tuning for the import worker.</summary>
public sealed class ImportWorkerOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "ImportWorker";

    /// <summary>
    /// Identifies this worker in job records and logs. Defaults to the machine name plus the
    /// process id, so two workers on one host are still distinguishable.
    /// </summary>
    public string WorkerId { get; set; } =
        $"{Environment.MachineName}-{Environment.ProcessId}";

    /// <summary>How many jobs this worker runs at once.</summary>
    /// <remarks>
    /// <para>
    /// Defaults to 1, and that is a measurement rather than caution. A daily import is bound by
    /// ClickHouse insert throughput, not by anything this process does; running two at once
    /// divides the same disk and memory between them and finishes both later than running them
    /// in sequence would. The development machine made this vivid - a second concurrent load
    /// took the container into an out-of-memory kill.
    /// </para>
    /// <para>
    /// It is configurable because the constraint is the analytics server's, not the worker's. A
    /// production ClickHouse with more memory and independent disks can absorb more, and the
    /// number should be raised there against measurement, not by default here.
    /// </para>
    /// </remarks>
    public int MaxConcurrentJobs { get; set; } = 1;

    /// <summary>How long to wait before asking for work again when the queue was empty.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a claim is valid before another worker may take the job.
    /// </summary>
    /// <remarks>
    /// Must comfortably exceed <see cref="LeaseRenewInterval"/>. If the worker dies, the job stays
    /// unavailable for at most this long - so a short value recovers faster, and a value that is
    /// too short lets a busy worker lose a job it is still working on.
    /// </remarks>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How often to extend the lease on a running job.</summary>
    public TimeSpan LeaseRenewInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How often to look for jobs whose worker died.</summary>
    public TimeSpan LeaseRecoveryInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Base delay for retry backoff. Attempt <c>n</c> waits <c>base * 2^(n-1)</c>.
    /// </summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long to let a running job finish when the process is asked to stop.
    /// </summary>
    /// <remarks>
    /// A job that does not finish in time is not killed mid-write: the process stops, the lease
    /// stops being renewed, and the recovery sweep returns the job to the queue. Because every
    /// import is idempotent at day granularity, re-running it is safe - which is what lets
    /// shutdown be a plain timeout rather than a negotiation.
    /// </remarks>
    public TimeSpan ShutdownGrace { get; set; } = TimeSpan.FromSeconds(30);
}
