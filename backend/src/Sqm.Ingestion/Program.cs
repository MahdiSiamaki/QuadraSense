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

builder.Services.Configure<ImportWorkerOptions>(
    builder.Configuration.GetSection(ImportWorkerOptions.SectionName));
builder.Services.Configure<PostgresOptions>(
    builder.Configuration.GetSection(PostgresOptions.SectionName));
builder.Services.Configure<ImportStorageOptions>(
    builder.Configuration.GetSection(ImportStorageOptions.SectionName));
builder.Services.Configure<ClickHouseOptions>(
    builder.Configuration.GetSection(ClickHouseOptions.SectionName));

// The same pooled-handler configuration the API uses. The worker posts gigabyte request bodies,
// so the timeout is the one number that differs: an insert may legitimately run for minutes.
builder.Services.AddHttpClient(ClickHouseAnalyticsStore.HttpClientName, client =>
    {
        client.Timeout = TimeSpan.FromMinutes(30);
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 8,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    });

builder.Services.AddSingleton<IImportJobRepository, PostgresImportJobRepository>();
builder.Services.AddSingleton<IImportFileStore, DirectoryImportFileStore>();
builder.Services.AddSingleton<IAnalyticsIngestionStore, ClickHouseIngestionStore>();
builder.Services.AddSingleton<ITacVersionStore, ClickHouseTacVersionStore>();

// One processor per data source, resolved by source code at claim time.
builder.Services.AddSingleton<MartRefresh>();
builder.Services.AddSingleton<IImportProcessor, SqmDailyProcessor>();
builder.Services.AddSingleton<IImportProcessor, TacSnapshotProcessor>();

builder.Services.AddHostedService<ImportWorker>();
builder.Services.AddHostedService<LeaseRecoveryService>();

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
if (args.Contains("--refresh-dashboard"))
{
    var analytics = host.Services.GetRequiredService<IAnalyticsIngestionStore>();
    var refresh = host.Services.GetRequiredService<MartRefresh>();

    var index = Array.IndexOf(args, "--seq");
    var sequence = index >= 0 && index + 1 < args.Length
        && int.TryParse(args[index + 1], out var parsed)
        ? parsed
        : await analytics.GetMaxSequenceAsync(CancellationToken.None).ConfigureAwait(false);

    Console.WriteLine($"rebuilding dashboard marts for delivery {sequence}");

    var failed = await refresh.RunAsync(
        sequence,
        message => { Console.WriteLine("  " + message); return Task.CompletedTask; },
        CancellationToken.None).ConfigureAwait(false);

    Console.WriteLine(failed == 0
        ? "dashboard marts rebuilt"
        : $"{failed} statement(s) failed; re-run to finish them");

    return failed == 0 ? 0 : 1;
}

if (args.Contains("--refresh-marts"))
{
    var analytics = host.Services.GetRequiredService<IAnalyticsIngestionStore>();
    var (from, to) = MartBackfill.ParseRange(args);
    return await MartBackfill.RunAsync(analytics, from, to, CancellationToken.None)
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
