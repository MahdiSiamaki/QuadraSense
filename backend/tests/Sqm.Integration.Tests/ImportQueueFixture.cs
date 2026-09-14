using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Integration.Tests;

/// <summary>
/// A real PostgreSQL connection, or a clear skip.
/// </summary>
/// <remarks>
/// <para>
/// These tests run against the actual database, not a fake. The behaviour being tested is the
/// behaviour of PostgreSQL itself — <c>FOR UPDATE SKIP LOCKED</c>, partial unique indexes,
/// transaction isolation — and a mock of a database cannot be wrong in the ways a database is
/// wrong. A fake that returns what we expect would test our expectations.
/// </para>
/// <para>
/// When no database is reachable the tests skip rather than fail. A developer without the
/// container running should see "skipped: no database", not a wall of red that trains them to
/// ignore test output.
/// </para>
/// </remarks>
public sealed class ImportQueueFixture : IAsyncLifetime
{
    /// <summary>Connection string, from the environment or the development default.</summary>
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("SQM_TEST_POSTGRES")
        ?? "Host=localhost;Port=15432;Database=sqm;Username=sqm;Password=sqm_dev;Timeout=3";

    public bool IsAvailable { get; private set; }

    public string? UnavailableReason { get; private set; }

    public PostgresImportJobRepository Repository { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        try
        {
            await using var probe = new NpgsqlConnection(ConnectionString);
            await probe.OpenAsync();

            await using var command = new NpgsqlCommand(
                "SELECT 1 FROM information_schema.tables "
                + "WHERE table_schema = 'imports' AND table_name = 'import_job'", probe);

            if (await command.ExecuteScalarAsync() is null)
            {
                UnavailableReason = "the imports schema is not applied; run the migrator first";
                return;
            }

            Repository = new PostgresImportJobRepository(
                Options.Create(new PostgresOptions { ConnectionString = ConnectionString }),
                NullLogger<PostgresImportJobRepository>.Instance);

            IsAvailable = true;
        }
        catch (NpgsqlException ex)
        {
            UnavailableReason = $"no database at {ConnectionString.Split(';')[0]}: {ex.Message}";
        }
    }

    /// <summary>
    /// Removes everything a test created, identified by the source it wrote under.
    /// </summary>
    /// <remarks>
    /// Tests write under their own data source code rather than SQM or TAC, so cleaning up can
    /// be a delete scoped to that code. Truncating the real tables would destroy the import
    /// history of whatever is running on the same development database.
    /// </remarks>
    public async Task CleanupAsync(string sourceCode)
    {
        if (!IsAvailable)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            DELETE FROM imports.import_job  WHERE source_code = @source;
            DELETE FROM imports.import_file WHERE source_code = @source;
            DELETE FROM imports.data_source WHERE code = @source;
            """, connection);

        command.Parameters.AddWithValue("source", sourceCode);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Registers a throwaway data source for one test.</summary>
    public async Task EnsureSourceAsync(string sourceCode)
    {
        if (!IsAvailable)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO imports.data_source
                (code, display_name, description, business_date_source, revision_strategy)
            VALUES (@source, 'Integration test source', 'Created by a test.', 'filename',
                    'replace_by_date')
            ON CONFLICT (code) DO NOTHING
            """, connection);

        command.Parameters.AddWithValue("source", sourceCode);
        await command.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
