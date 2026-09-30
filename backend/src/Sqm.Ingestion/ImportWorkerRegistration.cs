using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sqm.Application.DataImport;
using Sqm.Infrastructure.ClickHouse;
using Sqm.Infrastructure.DataImport;
using Sqm.Ingestion.Processing;

namespace Sqm.Ingestion;

/// <summary>
/// Registers the import worker and everything it needs.
/// </summary>
/// <remarks>
/// <para>
/// <b>One definition, two hosts.</b> The worker runs as its own process in production and inside
/// the API process in development, and both get the pipeline from here - so "what the worker
/// consists of" is written once. Two copies of this list would drift on the first change, and the
/// symptom would be a processor that exists in one deployment and not the other.
/// </para>
/// <para>
/// <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService, TImplementation}"/>
/// throughout, because the API already registers several of these for the Import Center's read
/// endpoints. Registering them twice would give the host two instances of a store that holds a
/// connection pool.
/// </para>
/// </remarks>
public static class ImportWorkerRegistration
{
    /// <summary>
    /// How long a bulk insert may run.
    /// </summary>
    /// <remarks>
    /// Thirty minutes, against the query path's 180 seconds. A gigabyte insert legitimately runs
    /// for minutes; a dashboard query that has not answered in three has gone wrong. That is why
    /// the two use differently named clients - see
    /// <see cref="ClickHouseIngestionStore.HttpClientName"/>.
    /// </remarks>
    private static readonly TimeSpan IngestionHttpTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Adds the import pipeline and its two hosted services.</summary>
    /// <param name="services">The container.</param>
    /// <param name="configuration">Where the options sections are read from.</param>
    /// <returns>The container, for chaining.</returns>
    /// <param name="hostedInApi">
    /// True when the API is hosting this worker rather than it running as its own process.
    /// Recorded on the heartbeat so the Import Center can say so.
    /// </param>
    public static IServiceCollection AddImportWorker(
        this IServiceCollection services, IConfiguration configuration, bool hostedInApi = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddImportPipeline(configuration);
        services.PostConfigure<ImportWorkerOptions>(o => o.HostedInApi = hostedInApi);

        services.AddHostedService<ImportWorker>();
        services.AddHostedService<LeaseRecoveryService>();

        return services;
    }

    /// <summary>
    /// Adds the pipeline without the hosted services, for the batch commands.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="configuration">Where the options sections are read from.</param>
    /// <returns>The container, for chaining.</returns>
    /// <remarks>
    /// <c>--refresh-marts</c>, <c>--backfill-imei</c> and the rest need the same stores and the
    /// same processors, and must NOT also start a worker that would claim jobs while a batch job
    /// is running.
    /// </remarks>
    public static IServiceCollection AddImportPipeline(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<ImportWorkerOptions>(
            configuration.GetSection(ImportWorkerOptions.SectionName));
        services.Configure<PostgresOptions>(
            configuration.GetSection(PostgresOptions.SectionName));
        services.Configure<ImportStorageOptions>(
            configuration.GetSection(ImportStorageOptions.SectionName));
        services.Configure<ClickHouseOptions>(
            configuration.GetSection(ClickHouseOptions.SectionName));

        services.AddHttpClient(ClickHouseIngestionStore.HttpClientName, client =>
            {
                client.Timeout = IngestionHttpTimeout;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 8,
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            });

        services.TryAddSingleton<IImportJobRepository, PostgresImportJobRepository>();
        services.TryAddSingleton<IImportFileStore, DirectoryImportFileStore>();
        services.TryAddSingleton<IAnalyticsIngestionStore, ClickHouseIngestionStore>();
        services.TryAddSingleton<ITacVersionStore, ClickHouseTacVersionStore>();

        services.TryAddSingleton<MartRefresh>();

        // Each day's file measured and judged against the ordinary days; see FeedQualityMonitor.
        // The reader goes through the read-only query path by the API's client name. Hosted in the
        // API, that client is the API's; in the standalone worker it is the factory's default,
        // which is enough for a read of one row per day and is not re-registered here because a
        // second registration of the name would change the API's own handler.
        services.Configure<Sqm.Application.Quality.FeedQualityOptions>(
            configuration.GetSection(Sqm.Application.Quality.FeedQualityOptions.SectionName));
        services.TryAddSingleton<Sqm.Application.Quality.IFeedQualityReader, ClickHouseFeedQualityStore>();
        services.TryAddSingleton<FeedQualityMonitor>();

        // Rebuilt once per run of files; the worker settles what a stopped run left owed.
        services.TryAddSingleton<DashboardSnapshot>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIdleTask, DashboardSnapshot>(
            sp => sp.GetRequiredService<DashboardSnapshot>()));

        // One processor per data source, resolved by source code at claim time. TryAddEnumerable
        // rather than TryAddSingleton: these are a collection, and TryAdd on a collection would
        // keep only the first.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IImportProcessor, SqmDailyProcessor>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IImportProcessor, TacSnapshotProcessor>());

        return services;
    }
}
