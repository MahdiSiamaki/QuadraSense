using System.Diagnostics;
using System.Globalization;
using ClickHouse.Client.ADO;

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

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        _connection = new ClickHouseConnection(connectionString);
        await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureHistoryTableAsync(CancellationToken cancellationToken)
    {
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
