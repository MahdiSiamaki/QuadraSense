using ClickHouse.Client.ADO;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Sqm.Infrastructure.ClickHouse;

namespace Sqm.Api.Infrastructure;

/// <summary>Readiness check for the analytics store.</summary>
/// <remarks>
/// Deliberately trivial (<c>SELECT 1</c>). A readiness probe must be cheap enough to run every few
/// seconds forever; anything that touches real tables would add load proportional to probe frequency.
/// </remarks>
public sealed class ClickHouseHealthCheck(IOptions<ClickHouseOptions> options) : IHealthCheck
{
    private readonly ClickHouseOptions _options = options?.Value
        ?? throw new ArgumentNullException(nameof(options));

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new ClickHouseConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.CommandTimeout = 5;

            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture) == 1
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Unexpected response from analytics store.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The message is safe to surface on an internal readiness endpoint, but it never reaches
            // an end user: /health/ready is not exposed through the UI.
            return HealthCheckResult.Unhealthy("Analytics store unreachable.", ex);
        }
    }
}
