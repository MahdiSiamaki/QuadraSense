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
builder.Services.AddSingleton<IImportProcessor, SqmDailyProcessor>();
builder.Services.AddSingleton<IImportProcessor, TacSnapshotProcessor>();

builder.Services.AddHostedService<ImportWorker>();
builder.Services.AddHostedService<LeaseRecoveryService>();

var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
