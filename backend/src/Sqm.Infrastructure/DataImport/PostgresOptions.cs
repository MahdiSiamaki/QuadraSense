namespace Sqm.Infrastructure.DataImport;

/// <summary>Connection settings for the operational store.</summary>
public sealed class PostgresOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Postgres";

    /// <summary>ADO connection string. Supplied by the environment, never committed.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Statement timeout applied to operational queries, in seconds.</summary>
    /// <remarks>
    /// Short on purpose. Every query here touches tables measured in thousands of rows, so a
    /// query that runs for 30 seconds is a bug, not a slow report, and failing fast surfaces it.
    /// The bulk work happens in ClickHouse, under its own much longer timeout.
    /// </remarks>
    public int CommandTimeoutSeconds { get; set; } = 30;
}
