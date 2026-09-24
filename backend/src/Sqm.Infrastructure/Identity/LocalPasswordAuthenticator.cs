using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sqm.Application.Identity;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Infrastructure.Identity;

/// <summary>Authenticates against locally stored Argon2id password hashes.</summary>
/// <remarks>
/// <para>
/// The order of checks here is deliberate and worth reading before changing. The password is
/// verified FIRST, before the lockout and active-account checks, so that a wrong password always
/// produces the same answer - <see cref="LoginStatus.InvalidCredentials"/> - whether or not the
/// account exists, is locked, or has been deactivated. Only a caller who already proved they know
/// the password is told anything more specific.
/// </para>
/// <para>
/// That costs one Argon2 derivation on a locked account, which is the same cost already paid for
/// a username that does not exist, so it opens no new lever. What it buys is that an attacker
/// learns nothing from the login endpoint at all, while the real owner of a locked account is
/// told plainly that it is locked and when it frees up.
/// </para>
/// </remarks>
public sealed partial class LocalPasswordAuthenticator : IPasswordAuthenticator
{
    [LoggerMessage(EventId = 4000, Level = LogLevel.Information,
        Message = "Sign-in succeeded for user {UserId}")]
    private partial void LogSuccess(long userId);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Warning,
        Message = "Sign-in failed: {Reason}. Failures on this account: {FailedCount}")]
    private partial void LogFailure(string reason, int failedCount);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Information,
        Message = "Password for user {UserId} rehashed with the current work factor")]
    private partial void LogRehashed(long userId);

    private readonly IdentityDataSource _db;
    private readonly IPasswordHasher _hasher;
    private readonly LockoutOptions _lockout;
    private readonly int _commandTimeout;
    private readonly ILogger<LocalPasswordAuthenticator> _logger;

    /// <summary>Creates the authenticator.</summary>
    public LocalPasswordAuthenticator(
        IdentityDataSource db,
        IPasswordHasher hasher,
        IOptions<AuthOptions> auth,
        IOptions<PostgresOptions> postgres,
        ILogger<LocalPasswordAuthenticator> logger)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(postgres);

        _db = db;
        _hasher = hasher;
        _lockout = auth.Value.Lockout;
        _commandTimeout = postgres.Value.CommandTimeoutSeconds;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Provider => "local";

    /// <inheritdoc />
    public async Task<LoginOutcome> AuthenticateAsync(
        string username, string password, AuthenticationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        const string findSql = """
            SELECT id, username, display_name AS DisplayName, password_hash AS PasswordHash,
                   provider, is_active AS IsActive, must_change_password AS MustChangePassword,
                   failed_login_count AS FailedLoginCount, locked_until AS LockedUntil
            FROM auth.user_account
            WHERE lower(username) = lower(@username)
            """;

        var account = await connection.QuerySingleOrDefaultAsync<AccountRow>(
            _db.Command(findSql, new { username }, ct)).ConfigureAwait(false);

        // No such user, or an account whose passwords live in a directory rather than here.
        // Both burn the same time a real verification would, so the response cannot be timed to
        // discover which usernames exist.
        if (account?.PasswordHash is null
            || !string.Equals(account.Provider, Provider, StringComparison.Ordinal))
        {
            _hasher.BurnTime();
            await AuditAsync(connection, null, username, "auth.login", AuditOutcome.Failure,
                context, new Dictionary<string, object?> { ["reason"] = "unknown_user" }, ct)
                .ConfigureAwait(false);
            LogFailure("unknown user", 0);
            return new LoginOutcome(LoginStatus.InvalidCredentials);
        }

        var verification = _hasher.Verify(password, account.PasswordHash);

        if (verification == PasswordVerification.Failed)
        {
            var failures = await RecordFailureAsync(connection, account.Id, ct).ConfigureAwait(false);

            await AuditAsync(connection, account.Id, account.Username, "auth.login",
                AuditOutcome.Failure, context, new Dictionary<string, object?>
                {
                    ["reason"] = "bad_password",
                    ["failedAttempts"] = failures.FailedLoginCount,
                    ["lockedUntil"] = failures.LockedUntil,
                }, ct).ConfigureAwait(false);

            LogFailure("wrong password", failures.FailedLoginCount);
            return new LoginOutcome(LoginStatus.InvalidCredentials);
        }

        // From here the caller has proved they know the password, so telling them why they still
        // cannot sign in reveals nothing they did not already have.
        if (account.LockedUntil is { } until && until > DateTimeOffset.UtcNow)
        {
            await AuditAsync(connection, account.Id, account.Username, "auth.login",
                AuditOutcome.Denied, context,
                new Dictionary<string, object?> { ["reason"] = "locked", ["until"] = until }, ct)
                .ConfigureAwait(false);

            LogFailure("account locked", account.FailedLoginCount);
            return new LoginOutcome(LoginStatus.AccountLocked, LockedUntil: until);
        }

        if (!account.IsActive)
        {
            await AuditAsync(connection, account.Id, account.Username, "auth.login",
                AuditOutcome.Denied, context,
                new Dictionary<string, object?> { ["reason"] = "deactivated" }, ct)
                .ConfigureAwait(false);

            LogFailure("account deactivated", account.FailedLoginCount);
            return new LoginOutcome(LoginStatus.AccountDisabled);
        }

        // The one moment the plaintext is available and the parameters are known to be stale.
        if (verification == PasswordVerification.SuccessNeedsRehash)
        {
            await connection.ExecuteAsync(_db.Command(
                "UPDATE auth.user_account SET password_hash = @hash WHERE id = @id",
                new { hash = _hasher.Hash(password), id = account.Id }, ct)).ConfigureAwait(false);

            LogRehashed(account.Id);
        }

        await RecordSuccessAsync(connection, account.Id, context.Ip, ct).ConfigureAwait(false);

        await AuditAsync(connection, account.Id, account.Username, "auth.login",
            AuditOutcome.Success, context, null, ct).ConfigureAwait(false);

        LogSuccess(account.Id);

        return new LoginOutcome(
            LoginStatus.Success,
            account.Id,
            account.Username,
            account.DisplayName,
            account.MustChangePassword);
    }

    /// <summary>Increments the failure count and locks the account once it crosses the threshold.</summary>
    /// <remarks>
    /// Done in SQL rather than read-modify-write, so two simultaneous wrong guesses count as two.
    /// A counter incremented in application code loses one of them, which is exactly the case an
    /// attacker produces.
    /// </remarks>
    private async Task<FailureRow> RecordFailureAsync(
        Npgsql.NpgsqlConnection connection, long userId, CancellationToken ct)
    {
        const string sql = """
            UPDATE auth.user_account
            SET failed_login_count   = LEAST(failed_login_count + 1, 30000),
                last_failed_login_at = now(),
                locked_until = CASE
                    WHEN failed_login_count + 1 >= @maxAttempts
                    -- The exponent is capped before it is used. Uncapped, 2^38 minutes is out of
                    -- interval range: from the 43rd failure every wrong password was a 500, not
                    -- counted, not locked, not audited - and guessing continued once the last
                    -- lock ran out. 2^20 minutes is already far past any MaxLockout. The count
                    -- is capped for the same reason: it is a smallint.
                    THEN now() + LEAST(
                        @initial * power(2, LEAST(failed_login_count + 1 - @maxAttempts, 20)),
                        @maxLockout)
                    ELSE locked_until
                END
            WHERE id = @userId
            RETURNING failed_login_count AS FailedLoginCount, locked_until AS LockedUntil
            """;

        return await connection.QuerySingleAsync<FailureRow>(_db.Command(sql, new
        {
            userId,
            maxAttempts = _lockout.MaxFailedAttempts,
            initial = _lockout.InitialLockout,
            maxLockout = _lockout.MaxLockout,
        }, ct)).ConfigureAwait(false);
    }

    /// <remarks>
    /// <c>previous_login_at</c> is shifted before <c>last_login_at</c> is overwritten. That pair is
    /// what lets the profile page say "last sign-in: Tuesday 09:14" during a session - which is
    /// the only form of it a user can act on. Showing them the session they are currently in tells
    /// them nothing about whether anyone else has used the account.
    /// </remarks>
    private async Task RecordSuccessAsync(
        Npgsql.NpgsqlConnection connection, long userId, string? ip, CancellationToken ct)
    {
        const string sql = """
            UPDATE auth.user_account
            SET failed_login_count = 0,
                locked_until       = NULL,
                previous_login_at  = last_login_at,
                last_login_at      = now(),
                last_login_ip      = @ip::inet
            WHERE id = @userId
            """;

        await connection.ExecuteAsync(
            _db.Command(sql, new { userId, ip = IdentitySql.NormaliseIp(ip) }, ct))
            .ConfigureAwait(false);
    }

    private Task AuditAsync(
        Npgsql.NpgsqlConnection connection,
        long? userId,
        string actorName,
        string action,
        AuditOutcome outcome,
        AuthenticationContext context,
        IReadOnlyDictionary<string, object?>? detail,
        CancellationToken ct) =>
        IdentitySql.WriteAuditAsync(connection, null, new AuditEntry(
            // The username as typed, even when it matches no account. An audit log that drops
            // failed attempts against names that do not exist cannot show a password spray,
            // which is the pattern it most needs to show.
            ActorName: IdentitySql.Truncate(actorName, 200) ?? "(empty)",
            Action: action,
            Category: AuditCategory.Authentication,
            Outcome: outcome,
            ActorUserId: userId,
            Ip: context.Ip,
            UserAgent: context.UserAgent,
            CorrelationId: context.CorrelationId,
            Detail: detail), _commandTimeout, ct);

    private sealed record AccountRow(
        long Id,
        string Username,
        string DisplayName,
        string? PasswordHash,
        string Provider,
        bool IsActive,
        bool MustChangePassword,
        short FailedLoginCount,
        DateTimeOffset? LockedUntil);

    private sealed record FailureRow(short FailedLoginCount, DateTimeOffset? LockedUntil);
}
