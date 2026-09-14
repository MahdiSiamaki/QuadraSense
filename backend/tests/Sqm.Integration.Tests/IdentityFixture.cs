using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Sqm.Application.Identity;
using Sqm.Infrastructure.DataImport;
using Sqm.Infrastructure.Identity;

namespace Sqm.Integration.Tests;

/// <summary>
/// The identity stack wired to a real database, or a clear skip.
/// </summary>
/// <remarks>
/// <para>
/// Connects as <c>sqm_app</c> - the least-privilege application role - rather than as the owner.
/// That is deliberate: the append-only guarantee on the audit tables is a GRANT, and a test suite
/// connecting as the owner would prove nothing about it while appearing to pass.
/// </para>
/// <para>
/// The authorisation rule under test is SQL. Roles, direct grants, denies and their precedence
/// are resolved by a query, so a mocked repository would only ever confirm what the mock was told
/// to say. These tests run against PostgreSQL for the same reason the import queue tests do.
/// </para>
/// </remarks>
public sealed class IdentityFixture : IAsyncLifetime
{
    /// <summary>Connection string for the application role.</summary>
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("SQM_TEST_POSTGRES_APP")
        ?? "Host=localhost;Port=15432;Database=sqm;Username=sqm_app;Password=sqm_dev;Timeout=3";

    /// <summary>Whether a usable database with the auth schema was found.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>Why not, when it was not.</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>The user directory under test.</summary>
    public PostgresUserDirectory Users { get; private set; } = null!;

    /// <summary>The role directory under test.</summary>
    public PostgresRoleDirectory Roles { get; private set; } = null!;

    /// <summary>The session store under test.</summary>
    public PostgresSessionStore Sessions { get; private set; } = null!;

    /// <summary>The authenticator under test.</summary>
    public LocalPasswordAuthenticator Authenticator { get; private set; } = null!;

    /// <summary>The audit log under test.</summary>
    public PostgresAuditLog Audit { get; private set; } = null!;

    /// <summary>The options the stack was built with.</summary>
    public AuthOptions Options { get; } = new();

    private IdentityDataSource? _dataSource;

    /// <summary>Where audited actions in these tests claim to come from.</summary>
    public static AuthenticationContext Context { get; } =
        new("127.0.0.1", "Sqm.Integration.Tests", "test");

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        try
        {
            await using var probe = new NpgsqlConnection(ConnectionString);
            await probe.OpenAsync();

            await using var command = new NpgsqlCommand(
                "SELECT 1 FROM information_schema.tables "
                + "WHERE table_schema = 'auth' AND table_name = 'user_account'", probe);

            if (await command.ExecuteScalarAsync() is null)
            {
                UnavailableReason = "the auth schema is not applied; run the migrator first";
                return;
            }

            var postgres = Microsoft.Extensions.Options.Options.Create(
                new PostgresOptions { ConnectionString = ConnectionString });
            var auth = Microsoft.Extensions.Options.Options.Create(Options);

            _dataSource = new IdentityDataSource(postgres);
            var hasher = new Argon2PasswordHasher(auth);

            Users = new PostgresUserDirectory(
                _dataSource, hasher, auth, postgres, NullLogger<PostgresUserDirectory>.Instance);
            Roles = new PostgresRoleDirectory(
                _dataSource, postgres, NullLogger<PostgresRoleDirectory>.Instance);
            Sessions = new PostgresSessionStore(_dataSource, auth, postgres);
            Authenticator = new LocalPasswordAuthenticator(
                _dataSource, hasher, auth, postgres,
                NullLogger<LocalPasswordAuthenticator>.Instance);
            Audit = new PostgresAuditLog(
                _dataSource, postgres, NullLogger<PostgresAuditLog>.Instance);

            IsAvailable = true;
        }
        catch (NpgsqlException ex)
        {
            UnavailableReason = $"no database at {ConnectionString.Split(';')[0]}: {ex.Message}";
        }
    }

    /// <summary>A username no real account will collide with.</summary>
    public static string UniqueUsername(string hint) =>
        $"test.{hint}.{Guid.NewGuid():N}"[..Math.Min(40, 6 + hint.Length + 33)];

    /// <summary>
    /// Removes an account the test created.
    /// </summary>
    /// <remarks>
    /// Scoped by id, never a TRUNCATE. These tests run against the development database, which
    /// holds the real administrator account and the real audit history; a suite that clears
    /// tables would destroy both. The audit entries the test produced are left behind on
    /// purpose - the application role cannot delete them, which is the point.
    /// </remarks>
    public async Task DeleteUserAsync(long userId)
    {
        if (!IsAvailable)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "DELETE FROM auth.user_account WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", userId);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Removes a role the test created.</summary>
    public async Task DeleteRoleAsync(long roleId)
    {
        if (!IsAvailable)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "DELETE FROM auth.role WHERE id = @id AND NOT is_system", connection);
        command.Parameters.AddWithValue("id", roleId);
        await command.ExecuteNonQueryAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }
    }
}
