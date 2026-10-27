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

                // Below ClickHouse's 10 s keep_alive_timeout; see the same setting in Sqm.Api.
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(5),
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
        // API, that client is the API's. It is not registered here, because a second registration
        // of the name would change the API's own handler - the standalone host registers it
        // instead (AddStandaloneQueryClient).
        services.Configure<Sqm.Application.Quality.FeedQualityOptions>(
            configuration.GetSection(Sqm.Application.Quality.FeedQualityOptions.SectionName));
        services.TryAddSingleton<Sqm.Application.Quality.IFeedQualityReader, ClickHouseFeedQualityStore>();
        services.TryAddSingleton<FeedQualityMonitor>();

        // Each day's SIM changes as rows for the risk pages, after feed quality has screened them.
        services.TryAddSingleton<RiskDayStep>();

        // Each day's per-model counts for new models and network age (analytics migration 025).
        services.TryAddSingleton<TacDayStep>();

        // Rebuilt once per run of files; the worker settles what a stopped run left owed.
        services.TryAddSingleton<DashboardSnapshot>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIdleTask, DashboardSnapshot>(
            sp => sp.GetRequiredService<DashboardSnapshot>()));

        // The risk lists' measures, a chunk per idle moment. After the dashboard in the idle list:
        // an owed dashboard is what people are already looking at. The same store instance as the
        // import's, not a second one.
        services.Configure<Sqm.Application.Risk.RiskOptions>(
            configuration.GetSection(Sqm.Application.Risk.RiskOptions.SectionName));
        services.TryAddSingleton<Sqm.Application.Risk.IRiskSnapshotStore>(
            sp => (Sqm.Application.Risk.IRiskSnapshotStore)sp.GetRequiredService<IAnalyticsIngestionStore>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<RiskSnapshot>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIdleTask, RiskSnapshot>(
            sp => sp.GetRequiredService<RiskSnapshot>()));

        // Network planning's per-number measures (analytics migration 030), a chunk per idle moment,
        // after the risk snapshot: the same store instance, the same limits.
        services.Configure<Sqm.Application.Planning.PlanningOptions>(
            configuration.GetSection(Sqm.Application.Planning.PlanningOptions.SectionName));
        services.TryAddSingleton<Sqm.Application.Planning.IPlanningSnapshotStore>(
            sp => (Sqm.Application.Planning.IPlanningSnapshotStore)sp.GetRequiredService<IAnalyticsIngestionStore>());
        services.TryAddSingleton<PlanningSnapshot>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIdleTask, PlanningSnapshot>(
            sp => sp.GetRequiredService<PlanningSnapshot>()));

        // One processor per data source, resolved by source code at claim time. TryAddEnumerable
        // rather than TryAddSingleton: these are a collection, and TryAdd on a collection would
        // keep only the first.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IImportProcessor, SqmDailyProcessor>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IImportProcessor, TacSnapshotProcessor>());

        return services;
    }

    /// <summary>
    /// The query client (<see cref="ClickHouseAnalyticsStore.HttpClientName"/>) for the worker when
    /// it is its own process. Never for the API, which registers its own.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <returns>The container, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// The TAC version store, the feed-quality reader and the other read paths the pipeline shares
    /// with the API ask for this name. Inside the API they get the API's client. In the standalone
    /// worker the name was registered nowhere, so the factory handed out its default handler - which
    /// does not decompress, while ClickHouse compresses every response. Every such read failed with
    /// "ClickHouse server returned compressed result but HttpClient did not decompress it": the
    /// first TAC upload to the containerised worker failed on every attempt (2026-10-22, ADR-007).
    /// Development never saw it, because there the worker lives in the API.
    /// </para>
    /// <para>
    /// The same settings as the API's client, less its per-request cancellation of abandoned
    /// queries - nothing abandons the worker's reads.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddStandaloneQueryClient(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(ClickHouseAnalyticsStore.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(180);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(5),
                MaxConnectionsPerServer = 8,
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            });

        return services;
    }
}
