using Sqm.Application.DataImport;
using Sqm.Infrastructure.ClickHouse;
using Sqm.Infrastructure.DataImport;
using Sqm.Ingestion;
using Sqm.Ingestion.Processing;

var builder = Host.CreateApplicationBuilder(args);

// JSON to stdout, like the API, so one log pipeline reads both.
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.UseUtcTimestamp = true;
});

// The pipeline, and the two hosted services that drive it. Defined once in
// ImportWorkerRegistration because the API hosts the same worker in development - see
// Import:RunWorkerInProcess there. Two copies of this list would drift on the first change.
//
// A batch command must NOT also start a worker that claims jobs underneath it, so the switches
// are inspected before deciding which of the two registrations to use.
var batchMode = args.Any(a => a.StartsWith("--", StringComparison.Ordinal)
    && a is not "--force" and not "--truncate" and not "--pause-merges");

if (batchMode)
{
    builder.Services.AddImportPipeline(builder.Configuration);
}
else
{
    builder.Services.AddImportWorker(builder.Configuration);
}

var host = builder.Build();

// A batch entry point into the same code the daily import uses.
//
//   dotnet run --project backend/src/Sqm.Ingestion -- --refresh-marts [--from d] [--to d]
//
// Rebuilding the historical marts with a separate SQL script would be a second implementation
// of the same aggregates, and the two would drift the first time one was corrected.
// Rebuild the dashboard marts for one delivery, without importing anything.
//
//   dotnet run --project backend/src/Sqm.Ingestion -- --refresh-dashboard [--seq N]
//
// Needed after a bulk load, and after activating a TAC version: both change what the marts
// would compute without any file arriving. Defaults to the highest sequence in the event log,
// which is the delivery the dashboard reads.
// Check that a delivery's marts are complete, not merely present.
//
//   dotnet run --project backend/src/Sqm.Ingestion -- --verify-marts [--seq N]
//
// "The partition exists" is a much weaker statement than it sounds: the refresh writes 15
// INSERTs across 6 marts, so a run where a third of them failed still leaves every mart holding
// rows for the delivery. This checks every slice and reconciles the totals against the KPI.
if (args.Contains("--verify-marts"))
{
    var analytics = host.Services.GetRequiredService<IAnalyticsIngestionStore>();

    var index = Array.IndexOf(args, "--seq");
    var sequence = index >= 0 && index + 1 < args.Length
        && int.TryParse(args[index + 1], out var wanted)
        ? wanted
        : await MartRefresh.LatestDeliveryAsync(analytics, CancellationToken.None).ConfigureAwait(false);

    var problems = await MartVerification
        .RunAsync(analytics, sequence, CancellationToken.None).ConfigureAwait(false);

    return problems == 0 ? 0 : 1;
}

if (args.Contains("--refresh-dashboard"))
{
    var analytics = host.Services.GetRequiredService<IAnalyticsIngestionStore>();
    var refresh = host.Services.GetRequiredService<MartRefresh>();

    var index = Array.IndexOf(args, "--seq");
    var sequence = index >= 0 && index + 1 < args.Length
        && int.TryParse(args[index + 1], out var parsed)
        ? parsed
        : await MartRefresh.LatestDeliveryAsync(analytics, CancellationToken.None).ConfigureAwait(false);

    Console.WriteLine($"rebuilding dashboard marts for delivery {sequence}");

    // --pause-merges is for an undersized node. The mart statements use tens of megabytes each
    // and still collide with a single merge of the 25 GiB event log, which can hold 4 GiB. It is
    // always turned back on, including when the job fails, because parts accumulate while it is
    // off.
    var pauseMerges = args.Contains("--pause-merges");

    if (pauseMerges)
    {
        Console.WriteLine("  pausing background merges for the duration");
        await MergeControl.PauseAsync(analytics, CancellationToken.None).ConfigureAwait(false);
    }

    int failed;
    try
    {
        failed = await refresh.RunAsync(
            sequence,
            message => { Console.WriteLine("  " + message); return Task.CompletedTask; },
            CancellationToken.None).ConfigureAwait(false);
    }
    finally
    {
        if (pauseMerges)
        {
            Console.WriteLine("  resuming background merges");
            await MergeControl.ResumeAsync(analytics, CancellationToken.None).ConfigureAwait(false);
        }
    }

    Console.WriteLine(failed == 0
        ? "dashboard marts rebuilt"
        : $"{failed} statement(s) failed; re-run to finish them");

    return failed == 0 ? 0 : 1;
}

if (args.Contains("--refresh-marts"))
{
    var analytics = host.Services.GetRequiredService<IAnalyticsIngestionStore>();
    var (from, to) = MartBackfill.ParseRange(args);
    return await MartBackfill
        .RunAsync(analytics, from, to, args.Contains("--force"), CancellationToken.None)
        .ConfigureAwait(false);
}

// Populates a re-ordered copy of current state. Run once after the migration that creates the
// table, and again after any rebuild of binding_current that bypasses the materialized view -
// an EXCHANGE TABLES swap fires no insert and so mirrors nothing. See ADR-008 and ADR-009.
if (args.Contains("--backfill-imsi") || args.Contains("--backfill-imei"))
{
    var analytics = host.Services.GetRequiredService<IAnalyticsIngestionStore>();
    var target = args.Contains("--backfill-imei")
        ? CurrentStateBackfill.ByImei
        : CurrentStateBackfill.ByImsi;

    return await CurrentStateBackfill
        .RunAsync(analytics, target, args.Contains("--truncate"), CancellationToken.None)
        .ConfigureAwait(false);
}

// Fail at startup, not at the first corrected file.
//
// Day-level idempotency is implemented as a partition drop, so the worker requires
// binding_event to be partitioned by day. On a monthly-partitioned table the same statement
// removes a whole month - a failure that would look like a successful import and be noticed
// weeks later, if at all.
await host.Services.GetRequiredService<IAnalyticsIngestionStore>()
    .EnsureSchemaAsync(CancellationToken.None).ConfigureAwait(false);

await host.RunAsync().ConfigureAwait(false);
return 0;
