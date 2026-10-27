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

// This process is the worker on its own, so the query client the API would otherwise provide is
// registered here - see AddStandaloneQueryClient for what failed without it.
builder.Services.AddStandaloneQueryClient();

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
            (done, total) => { Console.WriteLine($"  {done} of {total} statements"); return Task.CompletedTask; },
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

// Load the operator's initial dump into an empty store - once per environment, before the first
// daily file. Then the two things derived from the dump: its bindings in the history, and its
// device models. See DumpLoad.
//
//   dotnet Sqm.Ingestion.dll --load-dump <dump_subs_device_sim_info_*.csv[.gz]>
if (args.Contains("--load-dump"))
{
    var index = Array.IndexOf(args, "--load-dump");
    if (index + 1 >= args.Length)
    {
        Console.Error.WriteLine("usage: --load-dump <file>");
        return 2;
    }

    var analytics = host.Services.GetRequiredService<IAnalyticsIngestionStore>();

    var started = DateTimeOffset.UtcNow;
    var loaded = await DumpLoad.RunAsync(analytics, args[index + 1], CancellationToken.None).ConfigureAwait(false);
    if (loaded != 0)
    {
        return loaded;
    }

    // In the Import Center, as the load it was. Failing to record it does not undo a reconciled
    // load: the steps after it still run, and --record-dump records it once the cause is fixed.
    int recorded;
    try
    {
        recorded = await DumpHistory.RecordAsync(
            analytics,
            host.Services.GetRequiredService<IImportJobRepository>(),
            host.Services.GetRequiredService<IImportFileStore>(),
            args[index + 1], started, DateTimeOffset.UtcNow, verifyRows: false, CancellationToken.None).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        Console.Error.WriteLine($"the dump is loaded, but recording it in the Import Center failed: {ex.Message}");
        Console.Error.WriteLine("once the cause is fixed: --record-dump <file>");
        recorded = 1;
    }

    var history = await HistoryBackfill.RunAsync(analytics, null, null, false, CancellationToken.None).ConfigureAwait(false);
    if (history != 0)
    {
        return history;
    }

    var models = await TacDayBackfill.RunAsync(analytics, null, null, false, CancellationToken.None).ConfigureAwait(false);
    return models != 0 ? models : recorded;
}

// Hands a file already on the server to the Import Center, as an upload would - for files that
// cannot cross the network in one piece. A .gz is decompressed as it is stored. See ImportFile.
//
//   dotnet Sqm.Ingestion.dll --import-file <daily_subs_device_sim_info_YYYY-MM-DD.csv[.gz]> [--source SQM|TAC]
if (args.Contains("--import-file"))
{
    var index = Array.IndexOf(args, "--import-file");
    if (index + 1 >= args.Length)
    {
        Console.Error.WriteLine("usage: --import-file <file> [--source SQM|TAC]");
        return 2;
    }

    var sourceAt = Array.IndexOf(args, "--source");
    return await ImportFile.RunAsync(
        host.Services.GetRequiredService<IImportJobRepository>(),
        host.Services.GetRequiredService<IImportFileStore>(),
        sourceAt >= 0 && sourceAt + 1 < args.Length ? args[sourceAt + 1] : "SQM",
        args[index + 1], CancellationToken.None).ConfigureAwait(false);
}

// "Import again" from the shell: queues a finished import's file as its day's next revision.
// See ReprocessJob.
//
//   dotnet Sqm.Ingestion.dll --reprocess <job id>
if (args.Contains("--reprocess"))
{
    var index = Array.IndexOf(args, "--reprocess");
    if (index + 1 >= args.Length
        || !long.TryParse(args[index + 1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var jobId))
    {
        Console.Error.WriteLine("usage: --reprocess <job id>");
        return 2;
    }

    return await ReprocessJob.RunAsync(
        host.Services.GetRequiredService<IImportJobRepository>(),
        host.Services.GetRequiredService<IImportFileStore>(),
        jobId, CancellationToken.None).ConfigureAwait(false);
}

// Records a dump loaded before --load-dump recorded it: its original into the file store, and a
// completed import in the history. The file must hold exactly as many rows as binding_snapshot.
// See DumpHistory.
//
//   dotnet Sqm.Ingestion.dll --record-dump <dump_subs_device_sim_info_*.csv[.gz]>
if (args.Contains("--record-dump"))
{
    var index = Array.IndexOf(args, "--record-dump");
    if (index + 1 >= args.Length)
    {
        Console.Error.WriteLine("usage: --record-dump <file>");
        return 2;
    }

    return await DumpHistory.RecordAsync(
        host.Services.GetRequiredService<IAnalyticsIngestionStore>(),
        host.Services.GetRequiredService<IImportJobRepository>(),
        host.Services.GetRequiredService<IImportFileStore>(),
        args[index + 1], null, null, verifyRows: true, CancellationToken.None).ConfigureAwait(false);
}

// Measure feed quality for days imported before it existed, or again after a new GSMA version.
//
//   dotnet run --project backend/src/Sqm.Ingestion -- --refresh-quality [--from d] [--to d] [--force]
//
// The same per-day call the import makes; 6-12 seconds a day on the real files.
if (args.Contains("--refresh-quality"))
{
    var (from, to) = DayBackfill.ParseRange(args);
    return await FeedQualityBackfill
        .RunAsync(
            host.Services.GetRequiredService<IAnalyticsIngestionStore>(),
            host.Services.GetRequiredService<Sqm.Application.Quality.IFeedQualityReader>(),
            from, to, args.Contains("--force"), CancellationToken.None)
        .ConfigureAwait(false);
}

// Writes each day's SIM changes for the risk pages (analytics migration 023), for days imported
// before it existed, or again after their feed quality was re-measured. The same per-day call the
// import makes. Run after --refresh-quality: the screen reads its multi-number SIM lists.
//
//   dotnet run --project backend/src/Sqm.Ingestion -- --refresh-risk-days [--from d] [--to d] [--force]
// Writes the per-model day counts (analytics migration 025) for new models and network age, and the
// initial dump's models. Run once after the migration; the import keeps it current.
//
//   dotnet run --project backend/src/Sqm.Ingestion -- --refresh-tac-days [--from d] [--to d] [--force]
if (args.Contains("--refresh-tac-days"))
{
    var (from, to) = DayBackfill.ParseRange(args);
    return await TacDayBackfill
        .RunAsync(host.Services.GetRequiredService<IAnalyticsIngestionStore>(), from, to, args.Contains("--force"), CancellationToken.None)
        .ConfigureAwait(false);
}

if (args.Contains("--refresh-risk-days"))
{
    var (from, to) = DayBackfill.ParseRange(args);
    return await RiskDayBackfill
        .RunAsync(host.Services.GetRequiredService<IAnalyticsIngestionStore>(), from, to, args.Contains("--force"), CancellationToken.None)
        .ConfigureAwait(false);
}

// Builds the planning snapshot (analytics migration 030) now, step by step, instead of a chunk per
// idle moment of the worker. Stop the worker first, or the two build the same run.
//
//   dotnet run --project backend/src/Sqm.Ingestion -- --refresh-planning [--force]
if (args.Contains("--refresh-planning"))
{
    return await PlanningSnapshotRefresh
        .RunAsync(host.Services, args.Contains("--force"), CancellationToken.None)
        .ConfigureAwait(false);
}

// Parses a TAC version's radio profiles (analytics migration 029) - the active one unless
// --version names another. A version is parsed when it is loaded and before a dashboard rebuild
// when the parser has changed, so this is for versions loaded before the migration, or to look.
//
//   dotnet run --project backend/src/Sqm.Ingestion -- --parse-tac-radio [--version N]
if (args.Contains("--parse-tac-radio"))
{
    var tacStore = host.Services.GetRequiredService<ITacVersionStore>();
    var at = Array.IndexOf(args, "--version");
    var version = at >= 0 && at + 1 < args.Length
        && int.TryParse(args[at + 1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var named)
        ? named
        : await tacStore.GetActiveVersionIdAsync(CancellationToken.None).ConfigureAwait(false);

    if (version is not { } id)
    {
        Console.Error.WriteLine("no TAC version is active; name one with --version");
        return 2;
    }

    var radio = await tacStore.ParseRadioAsync(id, CancellationToken.None).ConfigureAwait(false);
    if (radio is null)
    {
        Console.Error.WriteLine("analytics migration 029 has not been applied");
        return 2;
    }

    Console.WriteLine($"TAC version {id}: {radio.Rows:N0} radio profiles, {radio.UnparsedTacs:N0} with a token no rule recognises");
    return 0;
}

// Builds the risk snapshot (analytics migration 024) now, step by step, instead of a chunk per idle
// moment of the worker. For the first build after the migration, and to retry a failed run; stop the
// worker first, or the two build the same run and publication refuses it.
//
//   dotnet run --project backend/src/Sqm.Ingestion -- --refresh-risk [--force]
if (args.Contains("--refresh-risk"))
{
    return await RiskSnapshotRefresh
        .RunAsync(host.Services, args.Contains("--force"), CancellationToken.None)
        .ConfigureAwait(false);
}

// Fills the binding history (analytics migration 022) - every binding's dated events, by number,
// SIM and IMEI - from the event log and the initial dump. Run once after the migration; the daily
// import keeps it current. Resumable: months that already reconcile are skipped.
//
//   dotnet run --project backend/src/Sqm.Ingestion -- --backfill-history [--from d] [--to d] [--force]
if (args.Contains("--backfill-history"))
{
    var (from, to) = DayBackfill.ParseRange(args);
    return await HistoryBackfill
        .RunAsync(
            host.Services.GetRequiredService<IAnalyticsIngestionStore>(),
            from, to, args.Contains("--force"), CancellationToken.None)
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
