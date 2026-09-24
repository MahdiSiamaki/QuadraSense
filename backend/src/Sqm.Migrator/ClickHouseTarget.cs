using System.Diagnostics;
using System.Globalization;
using ClickHouse.Client.ADO;
using Sqm.Application.Sql;

namespace Sqm.Migrator;

/// <summary>Applies migrations to ClickHouse, one statement at a time.</summary>
/// <remarks>
/// <para>
/// ClickHouse has no DDL transactions, so a migration that fails on its fourth statement leaves
/// the first three applied. The migrator does not pretend otherwise: it stops at the failure,
/// does not record the migration as applied, and names the statement that failed so the operator
/// can see exactly how far it got. Analytics migrations are written to tolerate this - they use
/// <c>IF NOT EXISTS</c> and <c>IF EXISTS</c> so that re-running after a partial failure converges.
/// </para>
/// <para>
/// Statements also go one per request for a second reason, found the hard way during the Phase 0
/// benchmark: sending a whole script as one multi-query session made heavy statements fail
/// non-deterministically with MEMORY_LIMIT_EXCEEDED against a server that had gigabytes free.
/// </para>
/// </remarks>
internal sealed class ClickHouseTarget(string connectionString) : IMigrationTarget
{
    private ClickHouseConnection? _connection;

    public string Description => "ClickHouse";

    public bool IsTransactional => false;

    private ClickHouseConnection Connection =>
        _connection ?? throw new InvalidOperationException("connection not opened");

    /// <summary>
    /// One HTTP client, with no timeout at all.
    /// </summary>
    /// <remarks>
    /// The driver's default client gives up after 120 seconds. A migration that rebuilds a
    /// billion-row table to change its partition key takes far longer than that, and when the
    /// client abandons it the server carries on working — so the tool reports a failure for a
    /// statement that is still running and may yet succeed. That is the worst possible answer: it
    /// invites the operator to re-run a migration that is mid-flight.
    ///
    /// Cancellation still works, through the token. What is removed is the arbitrary wall clock.
    /// </remarks>
    private static readonly HttpClient LongRunningClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(30),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => LongRunningClient;
    }

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        await EnsureDatabaseAsync(cancellationToken).ConfigureAwait(false);

        _connection = new ClickHouseConnection(
            connectionString, new SingleClientFactory(), "migrator");
        await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the database the connection string names, through a connection that names none.
    /// </summary>
    /// <remarks>
    /// The driver sends the connection string's database with every request, the first one
    /// included - its version probe inside <c>OpenAsync</c>. Against a server where that database
    /// does not exist yet, the server refuses the request before running it, so the
    /// <c>CREATE DATABASE IF NOT EXISTS</c> this used to issue on the open connection could never
    /// run on the one server that needed it. A fresh install failed at its first step.
    /// </remarks>
    private async Task EnsureDatabaseAsync(CancellationToken cancellationToken)
    {
        var builder = new ClickHouseConnectionStringBuilder(connectionString);
        var database = string.IsNullOrEmpty(builder.Database) ? "sqm" : builder.Database;
        builder.Database = string.Empty;

        await using var bootstrap = new ClickHouseConnection(
            builder.ConnectionString, new SingleClientFactory(), "migrator-bootstrap");
        await bootstrap.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = bootstrap.CreateCommand();
        command.CommandText = $"CREATE DATABASE IF NOT EXISTS `{database.Replace("`", "``", StringComparison.Ordinal)}`";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureHistoryTableAsync(CancellationToken cancellationToken)
    {
        // The migrations name the sqm database explicitly, so it must exist whatever the
        // connection string's own database is.
        await ExecuteAsync("CREATE DATABASE IF NOT EXISTS sqm", cancellationToken).ConfigureAwait(false);

        // ReplacingMergeTree, not MergeTree: re-recording a version after a partial failure
        // should replace the earlier row rather than leave two contradictory ones.
        await ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS sqm.schema_migration
            (
                version     String,
                name        String,
                checksum    String,
                applied_at  DateTime64(3, 'UTC'),
                duration_ms UInt32
            )
            ENGINE = ReplacingMergeTree(applied_at)
            ORDER BY version
            """,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAppliedAsync(CancellationToken cancellationToken)
    {
        var applied = new Dictionary<string, string>(StringComparer.Ordinal);

        await using var command = Connection.CreateCommand();
        command.CommandText = "SELECT version, checksum FROM sqm.schema_migration FINAL";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            applied[reader.GetString(0)] = reader.GetString(1);
        }

        return applied;
    }

    public async Task ApplyAsync(
        Migration migration, Action<string> progress, CancellationToken cancellationToken)
    {
        var statements = SqlScript.Split(migration.Sql);
        var total = Stopwatch.StartNew();

        for (var index = 0; index < statements.Count; index++)
        {
            var statement = statements[index];
            var label = SqlScript.Label(statement, index + 1);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                await ExecuteAsync(statement, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                progress($"    FAILED at statement {index + 1} of {statements.Count}: {label}");
                throw new InvalidOperationException(
                    $"{migration.Name} failed at statement {index + 1} of {statements.Count} "
                    + $"({label}). ClickHouse has no DDL transactions, so statements 1 to {index} "
                    + $"are already applied. Original error: {ex.Message}", ex);
            }

            stopwatch.Stop();
            progress($"    {stopwatch.ElapsedMilliseconds,6} ms  {label}");
        }

        total.Stop();

        await ExecuteAsync(
            "INSERT INTO sqm.schema_migration (version, name, checksum, applied_at, duration_ms) "
            + $"VALUES ('{Escape(migration.Version)}', '{Escape(migration.Name)}', "
            + $"'{Escape(migration.Checksum)}', now64(3), "
            + total.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + ")",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordWithoutRunningAsync(
        Migration migration, CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            "INSERT INTO sqm.schema_migration (version, name, checksum, applied_at, duration_ms) "
            + $"VALUES ('{Escape(migration.Version)}', '{Escape(migration.Name)}', "
            + $"'{Escape(migration.Checksum)}', now64(3), 0)",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using var command = Connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 0; // a mart rebuild over 126 million rows is not a hung query
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Escapes a literal for the bookkeeping INSERT. These values are version strings, file names
    /// and hex checksums the migrator produced itself, never user input, but escaping them keeps
    /// a file named with an apostrophe from writing broken SQL.
    /// </summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("'", "\\'", StringComparison.Ordinal);

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
