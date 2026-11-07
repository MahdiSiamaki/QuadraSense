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

    /// <summary>Proposed device images, for the review-queue tests.</summary>
    public Sqm.Infrastructure.Catalog.PostgresDeviceImageCandidateStore ImageCandidates { get; private set; } = null!;

    /// <summary>Live device images, so a test can check one was or was not replaced.</summary>
    public Sqm.Infrastructure.Catalog.PostgresDeviceImageStore LiveImages { get; private set; } = null!;

    /// <summary>My Queries, for the saved-query tests.</summary>
    public PostgresExplorerSavedQueryStore SavedQueries { get; private set; } = null!;

    /// <summary>Dashboards, their grants and preferences (operational migration 016).</summary>
    public PostgresDashboardStore Dashboards { get; private set; } = null!;

    /// <summary>Password recovery, sending to <see cref="Delivery"/>.</summary>
    public PostgresAccountRecovery Recovery { get; private set; } = null!;

    /// <summary>Proving a contact, sending to <see cref="Delivery"/>.</summary>
    public PostgresContactVerification Contacts { get; private set; } = null!;

    /// <summary>Two-step sign-in, on <see cref="Clock"/>.</summary>
    public PostgresTwoStepService TwoStep { get; private set; } = null!;

    /// <summary>Everything sent, instead of it being sent.</summary>
    public CapturingDelivery Delivery { get; } = new();

    /// <summary>Everything queued. Nothing drains it here; a test reads it.</summary>
    public RecordingOutbox Outbox { get; } = new();

    /// <summary>The time two-step sign-in believes it is.</summary>
    public MovableClock Clock { get; } = new();

    /// <summary>Counts the hashes recovery computes, each one being real.</summary>
    public CountingHasher Hasher { get; private set; } = null!;

    /// <summary>The options the stack was built with.</summary>
    public AuthOptions Options { get; } = new()
    {
        TwoStep = new TwoStepOptions { SecretKey = Convert.ToBase64String(new byte[32]) },
    };

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
            ImageCandidates = new Sqm.Infrastructure.Catalog.PostgresDeviceImageCandidateStore(
                _dataSource, postgres);
            LiveImages = new Sqm.Infrastructure.Catalog.PostgresDeviceImageStore(
                _dataSource, postgres);
            SavedQueries = new PostgresExplorerSavedQueryStore(_dataSource, postgres);
            Dashboards = new PostgresDashboardStore(_dataSource, postgres);

            Hasher = new CountingHasher(hasher);
            Recovery = new PostgresAccountRecovery(
                _dataSource, Hasher, Delivery, Outbox, auth, postgres,
                NullLogger<PostgresAccountRecovery>.Instance);
            Contacts = new PostgresContactVerification(
                _dataSource, hasher, Delivery, auth, postgres,
                NullLogger<PostgresContactVerification>.Instance);
            TwoStep = new PostgresTwoStepService(
                _dataSource, hasher, Outbox, auth, postgres,
                NullLogger<PostgresTwoStepService>.Instance, Clock);

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

    /// <summary>Runs a statement a test needs to set the scene with, such as ageing a code.</summary>
    public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        if (!IsAvailable)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Reads one value.</summary>
    public async Task<T?> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        if (!IsAvailable)
        {
            return default;
        }

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
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

/// <summary>Keeps what would have been sent.</summary>
public sealed class CapturingDelivery : IContactDelivery
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<SentMessage> _sent = new();

    /// <summary>When set, every send fails as a mail server's refusal would.</summary>
    public bool Failing { get; set; }

    /// <summary>Everything sent so far.</summary>
    public IReadOnlyList<SentMessage> Sent => [.. _sent];

    /// <summary>What was sent to one address or number, oldest first.</summary>
    public IReadOnlyList<SentMessage> To(string destination) =>
        [.. _sent.Where(m => m.Destination == destination)];

    /// <summary>The six digits at the start of the latest message to this contact.</summary>
    public string LatestCode(string destination) => To(destination)[^1].Message.Text[..6];

    /// <inheritdoc />
    public bool IsAvailable(ContactChannel channel) => true;

    /// <inheritdoc />
    public Task SendAsync(
        ContactChannel channel, string destination, OutgoingMessage message, CancellationToken ct)
    {
        if (Failing)
        {
            throw new ContactDeliveryException("the server said no");
        }

        _sent.Enqueue(new SentMessage(channel, destination, message));
        return Task.CompletedTask;
    }
}

/// <summary>One message that was not really sent.</summary>
public sealed record SentMessage(ContactChannel Channel, string Destination, OutgoingMessage Message);

/// <summary>Keeps what was queued.</summary>
public sealed class RecordingOutbox : IAccountOutbox
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<AccountWork> _work = new();

    /// <summary>Everything queued so far.</summary>
    public IReadOnlyList<AccountWork> Work => [.. _work];

    /// <inheritdoc />
    public bool TryEnqueue(AccountWork work)
    {
        _work.Enqueue(work);
        return true;
    }
}

/// <summary>A clock a test can move, for codes that belong to a moment.</summary>
public sealed class MovableClock : TimeProvider
{
    private TimeSpan _offset;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + _offset;

    /// <summary>Moves it on.</summary>
    public void Advance(TimeSpan by) => _offset += by;
}

/// <summary>A real hasher that counts, because "one hash on every path" is a claim to test.</summary>
public sealed class CountingHasher : IPasswordHasher
{
    private readonly IPasswordHasher _inner;
    private int _work;

    /// <summary>Wraps a hasher.</summary>
    public CountingHasher(IPasswordHasher inner) => _inner = inner;

    /// <summary>Verifications and burns so far.</summary>
    public int Work => Volatile.Read(ref _work);

    /// <inheritdoc />
    public string Hash(string password) => _inner.Hash(password);

    /// <inheritdoc />
    public PasswordVerification Verify(string password, string storedHash)
    {
        Interlocked.Increment(ref _work);
        return _inner.Verify(password, storedHash);
    }

    /// <inheritdoc />
    public void BurnTime()
    {
        Interlocked.Increment(ref _work);
        _inner.BurnTime();
    }
}
