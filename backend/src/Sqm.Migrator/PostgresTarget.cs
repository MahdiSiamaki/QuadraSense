using System.Diagnostics;
using Npgsql;

namespace Sqm.Migrator;

/// <summary>Applies migrations to PostgreSQL, one transaction per migration.</summary>
internal sealed class PostgresTarget(string connectionString) : IMigrationTarget
{
    private NpgsqlConnection? _connection;

    public string Description => "PostgreSQL";

    public bool IsTransactional => true;

    private NpgsqlConnection Connection =>
        _connection ?? throw new InvalidOperationException("connection not opened");

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        _connection = new NpgsqlConnection(connectionString);
        await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureHistoryTableAsync(CancellationToken cancellationToken)
    {
        const string Sql = """
            CREATE TABLE IF NOT EXISTS schema_migration (
                version     text        PRIMARY KEY,
                name        text        NOT NULL,
                checksum    text        NOT NULL,
                applied_at  timestamptz NOT NULL DEFAULT now(),
                duration_ms integer     NOT NULL
            );
            """;

        await using var command = new NpgsqlCommand(Sql, Connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAppliedAsync(CancellationToken cancellationToken)
    {
        var applied = new Dictionary<string, string>(StringComparer.Ordinal);

        await using var command = new NpgsqlCommand(
            "SELECT version, checksum FROM schema_migration", Connection);
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
        var stopwatch = Stopwatch.StartNew();

        await using var transaction = await Connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // The whole file goes in as one command. Npgsql sends a multi-statement script in a
        // single round trip, so splitting it would add a parser and buy nothing.
        await using (var command = new NpgsqlCommand(migration.Sql, Connection, transaction))
        {
            command.CommandTimeout = 0; // an index build on a large table is not a hung query
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        stopwatch.Stop();

        await using (var record = new NpgsqlCommand(
            """
            INSERT INTO schema_migration (version, name, checksum, duration_ms)
            VALUES (@version, @name, @checksum, @duration)
            """, Connection, transaction))
        {
            record.Parameters.AddWithValue("version", migration.Version);
            record.Parameters.AddWithValue("name", migration.Name);
            record.Parameters.AddWithValue("checksum", migration.Checksum);
            record.Parameters.AddWithValue("duration", (int)stopwatch.ElapsedMilliseconds);
            await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        progress($"    {stopwatch.ElapsedMilliseconds,6} ms  applied in one transaction");
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
