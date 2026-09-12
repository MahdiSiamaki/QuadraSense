using System.Text.Json.Serialization;
using Sqm.Api.Endpoints;
using Sqm.Api.Infrastructure;
using Sqm.Application.Abstractions;
using Sqm.Infrastructure.ClickHouse;

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
        MaxConnectionsPerServer = 32,

        // Required, not optional. ClickHouse compresses its HTTP responses, and
        // supplying our own handler replaces the driver's default one — which had
        // decompression enabled. Without this every query fails with
        // "server returned compressed result but HttpClient did not decompress it".
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    });

builder.Services.AddSingleton<IDeviceAnalyticsStore, ClickHouseAnalyticsStore>();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

// CORS is restricted to the known frontend origin. No wildcard: the API will
// eventually carry credentials, and `*` is incompatible with that anyway.
var corsOrigins = builder.Configuration["Api:CorsOrigins"]?.Split(',', StringSplitOptions.RemoveEmptyEntries)
                  ?? ["http://localhost:5173"];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(corsOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

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

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Liveness deliberately does not touch dependencies: a database blip should not
// cause an orchestrator to kill an otherwise healthy process.
app.MapHealthChecks("/health/live", new()
{
    Predicate = _ => false,
});
app.MapHealthChecks("/health/ready", new()
{
    Predicate = c => c.Tags.Contains("ready"),
});

app.MapDashboardEndpoints();
app.MapLookupEndpoints();

await app.RunAsync().ConfigureAwait(false);

/// <summary>Exposed so integration tests can spin up the real host.</summary>
public partial class Program;
