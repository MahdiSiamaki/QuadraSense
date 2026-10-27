using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sqm.Api.Auth;
using Sqm.Api.Endpoints;
using Sqm.Api.Infrastructure;
using Sqm.Application.Abstractions;
using Sqm.Application.DataImport;
using Sqm.Application.Identity;
using Sqm.Infrastructure.Catalog;
using Sqm.Infrastructure.ClickHouse;
using Sqm.Infrastructure.DataImport;
using Sqm.Infrastructure.Identity;
using Sqm.Ingestion;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- logging
// JSON to stdout so every line is machine-parsable. Scopes are enabled because
// the correlation ID is attached as a scope by CorrelationIdMiddleware.
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.UseUtcTimestamp = true;
});

// ---------------------------------------------------------------- options
builder.Services.Configure<ClickHouseOptions>(
    builder.Configuration.GetSection(ClickHouseOptions.SectionName));
builder.Services.Configure<PostgresOptions>(
    builder.Configuration.GetSection(PostgresOptions.SectionName));
builder.Services.Configure<ImportStorageOptions>(
    builder.Configuration.GetSection(ImportStorageOptions.SectionName));
builder.Services.Configure<AuthOptions>(
    builder.Configuration.GetSection(AuthOptions.SectionName));

// ---------------------------------------------------------------- services
// One pooled HttpClient for every ClickHouse call. Without this the driver builds
// a client per connection, paying a TCP handshake on each query and leaking sockets
// into TIME_WAIT under load.
builder.Services.AddHttpClient(ClickHouseAnalyticsStore.HttpClientName, client =>
    {
        // Must exceed ClickHouse:QueryTimeoutSeconds. HttpClient defaults to 100s, which was
        // below the 120s query timeout - so a slow query was abandoned by the client while the
        // server kept working on it, and the caller saw an opaque 500 instead of a timeout.
        client.Timeout = TimeSpan.FromSeconds(180);
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),

        // Below ClickHouse's keep_alive_timeout (10 s on this server, measured 2026-10-01). The
        // default here is a minute, so a connection idle between 10 and 60 seconds is one the
        // server has already closed - and a request sent on it as the close arrives fails with
        // "connection forcibly closed". Dropping it first costs one new connection.
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(5),
        MaxConnectionsPerServer = 32,

        // Required, not optional. ClickHouse compresses its HTTP responses, and
        // supplying our own handler replaces the driver's default one — which had
        // decompression enabled. Without this every query fails with
        // "server returned compressed result but HttpClient did not decompress it".
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    // A query the API stops waiting for is killed rather than left to run (AbandonedQueryCanceller):
    // abandoned dashboard reads ran on for minutes and slowed every read after them.
    .AddHttpMessageHandler(() => new AbandonedQueryCanceller());

builder.Services.AddSingleton<IDeviceAnalyticsStore, ClickHouseAnalyticsStore>();

// Each day's file judged against the ordinary days. TryAdd, because the in-process worker
// registers the same reader and options for the import's own check - one instance, one judgement.
builder.Services.Configure<Sqm.Application.Quality.FeedQualityOptions>(
    builder.Configuration.GetSection(Sqm.Application.Quality.FeedQualityOptions.SectionName));
builder.Services.TryAddSingleton<Sqm.Application.Quality.IFeedQualityReader, ClickHouseFeedQualityStore>();

// The Explorer's query engine: allow-listed fields compiled to parameterised SQL, planned with
// EXPLAIN ESTIMATE, run within a budget the server enforces. See ADR-012.
builder.Services.Configure<Sqm.Application.Explorer.ExplorerOptions>(
    builder.Configuration.GetSection(Sqm.Application.Explorer.ExplorerOptions.SectionName));
builder.Services.AddSingleton<Sqm.Application.Explorer.IExplorerEngine,
    Sqm.Infrastructure.ClickHouse.Explorer.ClickHouseExplorerEngine>();
builder.Services.AddSingleton<Sqm.Application.Explorer.IExplorerSavedQueryStore, PostgresExplorerSavedQueryStore>();

// Timelines, from the binding history (analytics migration 022). A singleton: it caches for a
// minute whether the history is complete, which every timeline request asks.
builder.Services.Configure<Sqm.Application.Timeline.TimelineOptions>(
    builder.Configuration.GetSection(Sqm.Application.Timeline.TimelineOptions.SectionName));
builder.Services.AddSingleton<Sqm.Application.Timeline.ITimelineStore,
    Sqm.Infrastructure.ClickHouse.ClickHouseTimelineStore>();

// Risk signals: the rule set (judged here, when read) and the reader of the worker's measures
// (analytics migrations 023 and 024). The same section the worker binds, so both see one rule set.
builder.Services.Configure<Sqm.Application.Risk.RiskOptions>(
    builder.Configuration.GetSection(Sqm.Application.Risk.RiskOptions.SectionName));
builder.Services.AddSingleton<Sqm.Application.Risk.IRiskReader, Sqm.Infrastructure.ClickHouse.ClickHouseRiskReader>();

// Network planning: the reader of the worker's planning snapshot (analytics migrations 029, 030), and
// the controlled export of campaign lists from it (operational migration 021).
builder.Services.AddSingleton<Sqm.Application.Planning.IPlanningReader, Sqm.Infrastructure.ClickHouse.ClickHousePlanningReader>();
builder.Services.Configure<Sqm.Application.Campaigns.CampaignExportOptions>(
    builder.Configuration.GetSection(Sqm.Application.Campaigns.CampaignExportOptions.SectionName));
builder.Services.AddSingleton<Sqm.Application.Campaigns.ICampaignExportStore, Sqm.Infrastructure.Identity.PostgresCampaignExportStore>();

// New models and network age (analytics migration 025).
builder.Services.AddSingleton<Sqm.Application.Devices.IModelArrivalReader, Sqm.Infrastructure.ClickHouse.ClickHouseModelArrivalReader>();
builder.Services.AddSingleton<Sqm.Application.Quality.IQualityReader, Sqm.Infrastructure.ClickHouse.ClickHouseQualityReader>();

// The dashboard builder (ADR-016): the widget contract the frontend registry is typed against, the
// validator that refuses anything it does not name, the PostgreSQL store, and the rules of who may do
// what. The application layer takes plain option values; they are read here, once.
builder.Services.Configure<Sqm.Application.Dashboards.DashboardOptions>(
    builder.Configuration.GetSection(Sqm.Application.Dashboards.DashboardOptions.SectionName));
builder.Services.AddSingleton<Sqm.Application.Dashboards.IWidgetContract>(_ => Sqm.Application.Dashboards.WidgetContract.Load());
builder.Services.AddSingleton<Sqm.Application.Dashboards.IDashboardDefinitionValidator>(sp =>
    new Sqm.Application.Dashboards.DashboardDefinitionValidator(
        sp.GetRequiredService<Sqm.Application.Dashboards.IWidgetContract>(),
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Sqm.Application.Dashboards.DashboardOptions>>().Value,
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Sqm.Application.Explorer.ExplorerOptions>>().Value,
        sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<Sqm.Application.Dashboards.IDashboardStore, PostgresDashboardStore>();
builder.Services.AddSingleton(sp => new Sqm.Application.Dashboards.DashboardService(
    sp.GetRequiredService<Sqm.Application.Dashboards.IDashboardStore>(),
    sp.GetRequiredService<Sqm.Application.Dashboards.IDashboardDefinitionValidator>(),
    sp.GetRequiredService<Sqm.Application.Dashboards.IWidgetContract>(),
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Sqm.Application.Dashboards.DashboardOptions>>().Value,
    sp.GetRequiredService<IAuditLog>(),
    sp.GetRequiredService<TimeProvider>()));

// Exports read each widget through the composition its own endpoint uses, for the person exporting.
// Scoped: one request's widgets share one read of the daily series, the feed-quality days and the
// risk context.
builder.Services.AddScoped<Sqm.Application.Dashboards.IWidgetDataLoader>(sp =>
    new Sqm.Application.Dashboards.DashboardWidgetDataLoader(
        sp.GetRequiredService<IDeviceAnalyticsStore>(),
        sp.GetRequiredService<IImportJobRepository>(),
        sp.GetRequiredService<Sqm.Application.Risk.IRiskReader>(),
        sp.GetRequiredService<Sqm.Application.Quality.IFeedQualityReader>(),
        sp.GetRequiredService<Sqm.Application.Quality.IQualityReader>(),
        sp.GetRequiredService<Sqm.Application.Devices.IModelArrivalReader>(),
        sp.GetRequiredService<IDeviceImageStore>(),
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Sqm.Application.Quality.FeedQualityOptions>>().Value,
        () => sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Sqm.Application.Risk.RiskOptions>>().CurrentValue,
        sp.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped(sp => new Sqm.Application.Dashboards.DashboardExportService(
    sp.GetRequiredService<Sqm.Application.Dashboards.IWidgetContract>(),
    sp.GetRequiredService<Sqm.Application.Dashboards.IDashboardDefinitionValidator>(),
    sp.GetRequiredService<Sqm.Application.Dashboards.DashboardService>(),
    sp.GetRequiredService<Sqm.Application.Dashboards.IWidgetDataLoader>(),
    sp.GetRequiredService<IAuditLog>(),
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Sqm.Application.Dashboards.DashboardOptions>>().Value,
    sp.GetRequiredService<TimeProvider>(),
    (message, ex) => DashboardExportEndpoints.ReportFailure(
        sp.GetRequiredService<ILogger<Sqm.Application.Dashboards.DashboardExportService>>(), message, ex)));

// Curated device photographs. The GSMA TAC record carries none and this deployment has no
// internet access, so they are uploaded here and held in PostgreSQL. See ADR-009.
builder.Services.AddSingleton<IDeviceImageStore, PostgresDeviceImageStore>();
builder.Services.AddSingleton<IDeviceImageCandidateStore, PostgresDeviceImageCandidateStore>();

// The import platform's operational store and file store. No ENDPOINT can write to the analytics
// store - none of them takes IAnalyticsIngestionStore, so a bug in a handler cannot drop a day's
// data. Below, the worker may be hosted in this process, and then the PROCESS can write; see the
// note there about what that does and does not change.
builder.Services.AddSingleton<IImportJobRepository, PostgresImportJobRepository>();
builder.Services.AddSingleton<IImportFileStore, DirectoryImportFileStore>();
builder.Services.AddSingleton<ITacVersionStore, ClickHouseTacVersionStore>();
builder.Services.AddSingleton(TimeProvider.System);

// ------------------------------------------------------- the worker, optionally
//
// WHY THIS EXISTS. The worker is a separate process, and a queued job therefore sits untouched
// until somebody remembers to start it. That is correct in production and a trap in development:
// a 319.6 MB file was uploaded, the Import Center showed "Queued" with a progress panel that
// never appeared, and nothing anywhere said why - because nothing was wrong except that no
// worker existed.
//
// So in development the API hosts it, and one `dotnet run` is the whole system.
//
// WHAT THIS COSTS, stated rather than discovered later. A heavy import competes with the API for
// this process's CPU and memory - and it is this process that serves the progress page somebody
// is watching while it runs. The mart rebuild after a daily file is minutes of that. It also
// means the two can no longer be scaled or restarted independently.
//
// WHAT IT DOES NOT COST. The separation of code paths survives: no endpoint takes
// IAnalyticsIngestionStore, and the worker's services are reachable only from the worker. What
// is given up is process isolation, not the boundary.
//
// Default: on in Development, off everywhere else. Production keeps two processes, which is what
// ADR-007 will decide properly.
var runWorkerInProcess = builder.Configuration.GetValue(
    "Import:RunWorkerInProcess", builder.Environment.IsDevelopment());

if (runWorkerInProcess)
{
    builder.Services.AddImportWorker(builder.Configuration, hostedInApi: true);
}

// ---------------------------------------------------------------- identity
// One pool for every identity repository, rather than one per repository.
builder.Services.AddSingleton<IdentityDataSource>();
builder.Services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
builder.Services.AddSingleton<IPasswordAuthenticator, LocalPasswordAuthenticator>();
builder.Services.AddSingleton<ISessionStore, PostgresSessionStore>();
builder.Services.AddSingleton<IUserDirectory, PostgresUserDirectory>();
builder.Services.AddSingleton<IRoleDirectory, PostgresRoleDirectory>();
builder.Services.AddSingleton<IAuditLog, PostgresAuditLog>();
builder.Services.AddHostedService<SessionSweeper>();

builder.Services.AddAuthentication(SessionAuthenticationHandler.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions,
        SessionAuthenticationHandler>(SessionAuthenticationHandler.SchemeName, null);

// The policy provider builds a policy for any permission code on demand, and - the part that
// matters - supplies a non-null FALLBACK policy. An endpoint added without RequireAuthorization
// is therefore protected anyway; the ones that must be anonymous say so explicitly, which is
// visible in review where a missing call is not.
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler,
    AuditingAuthorizationResultHandler>();
builder.Services.AddAuthorization();

// Bounds what an unauthenticated caller can make the server spend.
//
// Argon2id is deliberately expensive - 79 ms and 19 MiB per verification - and the login endpoint
// runs it for usernames that do not exist too, so that a missing account cannot be told from a
// wrong password by timing. Without a limiter, that property is also a way for anyone on the
// network to occupy the process. Six attempts a minute per address is far above what a person
// types and far below what an attack needs.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(AuthEndpoints.LoginRateLimiter, http =>
        RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 6,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

builder.Services.ConfigureHttpJsonOptions(o =>
{
    // Nulls are written, not omitted.
    //
    // Omitting them saves a few bytes and costs a type lie: the TypeScript client declares
    // `daysBehind: number | null`, so a check for `!== null` narrows it to `number` - and the
    // value that actually arrives is `undefined`, which passes that check and renders as
    // "undefined days behind". Every such field becomes a small trap of the same shape.
    //
    // These payloads are counters and short lists. The bytes were never worth it.
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// Kestrel refuses a body over 30 MB by default. Daily imports reach a gigabyte, so the limit is
// lifted here and enforced where it can actually be enforced: the file store counts bytes as it
// writes them, against a configured maximum. A declared content length is a client's claim.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = null);

builder.Services.AddProblemDetails();
// A store timeout is a 504 that says so (StoreTimeoutExceptionHandler); everything else stays a 500.
builder.Services.AddExceptionHandler<Sqm.Api.Infrastructure.StoreTimeoutExceptionHandler>();
builder.Services.AddOpenApi();

// CORS is restricted to the known frontend origin. No wildcard: the API will
// eventually carry credentials, and `*` is incompatible with that anyway.
var corsOrigins = builder.Configuration["Api:CorsOrigins"]?.Split(',', StringSplitOptions.RemoveEmptyEntries)
                  ?? ["http://localhost:5173"];
// The headers the client reads are exposed: across origins (development: the SPA and the API on
// different ports) a browser hides every other header from scripts, so a download lost its file name -
// and with it the scope a dashboard CSV's name carries - and an error lost its correlation id.
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(corsOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()
    .WithExposedHeaders(
        "Content-Disposition", "X-Export-Scope", "X-Correlation-Id",
        // The tus protocol's (ADR-018): where an upload lives, and how much of it has arrived.
        "Location", "Upload-Offset", "Upload-Length", "Upload-Expires",
        "Tus-Resumable", "Tus-Version", "Tus-Max-Size", "Tus-Extension")));

// Resumable uploads (ADR-018): the tus endpoint's store and configuration, and the hourly removal
// of uploads nobody finished.
builder.Services.AddSingleton<Sqm.Api.Infrastructure.ResumableUploads>();
builder.Services.AddHostedService<Sqm.Api.Infrastructure.ResumableUploadCleanup>();

builder.Services.AddHealthChecks()
    .AddCheck<ClickHouseHealthCheck>("clickhouse", tags: ["ready"]);

var app = builder.Build();

// ---------------------------------------------------------------- pipeline
// Order matters: correlation ID first so every later log line and every error
// response carries it.
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseRateLimiter();

// Order: authenticate, then check the CSRF token, then authorise. The CSRF check reads the
// session cookie rather than the authenticated principal, so it could sit either side of
// authentication - it sits after it so that an expired session produces a 401 rather than a
// confusing 403 about a token.
app.UseAuthentication();
app.UseMiddleware<CsrfMiddleware>();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

// Liveness deliberately does not touch dependencies: a database blip should not
// cause an orchestrator to kill an otherwise healthy process.
app.MapHealthChecks("/health/live", new()
{
    Predicate = _ => false,
}).AllowAnonymous();
app.MapHealthChecks("/health/ready", new()
{
    Predicate = c => c.Tags.Contains("ready"),
}).AllowAnonymous();

app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapRoleEndpoints();
app.MapAuditEndpoints();

app.MapDashboardEndpoints();
app.MapDashboardBuilderEndpoints();
app.MapDashboardExportEndpoints();
app.MapImportEndpoints();
app.MapResumableUploadEndpoints();
app.MapTacEndpoints();
app.MapLookupEndpoints();
app.MapImsiEndpoints();
app.MapDeviceEndpoints();
app.MapRelationshipEndpoints();
app.MapQualityEndpoints();
app.MapExplorerEndpoints();
app.MapTimelineEndpoints();
app.MapRiskEndpoints();
app.MapPlanningEndpoints();
app.MapCampaignExportEndpoints();
app.MapModelArrivalEndpoints();
app.MapDeviceImageReviewEndpoints();

await app.RunAsync().ConfigureAwait(false);

/// <summary>Exposed so integration tests can spin up the real host.</summary>
public partial class Program;
