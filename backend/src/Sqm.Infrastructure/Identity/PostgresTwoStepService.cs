using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Sqm.Application.Identity;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Infrastructure.Identity;

/// <summary>Two-step sign-in with an authenticator app and recovery codes.</summary>
public sealed partial class PostgresTwoStepService : ITwoStepService
{
    [LoggerMessage(EventId = 4220, Level = LogLevel.Warning,
        Message = "The authenticator secret of user {UserId} cannot be opened with this key")]
    private partial void LogUnreadable(long userId);

    [LoggerMessage(EventId = 4221, Level = LogLevel.Information,
        Message = "Two-step sign-in {Change} for user {UserId}")]
    private partial void LogChange(string change, long userId);

    private readonly IdentityDataSource _db;
    private readonly IPasswordHasher _hasher;
    private readonly IAccountOutbox _outbox;
    private readonly TwoStepOptions _options;
    private readonly LockoutOptions _lockout;
    private readonly SecretBox? _box;
    private readonly TimeProvider _clock;
    private readonly int _commandTimeout;
    private readonly ILogger<PostgresTwoStepService> _logger;

    /// <summary>Creates the service.</summary>
    public PostgresTwoStepService(
        IdentityDataSource db,
        IPasswordHasher hasher,
        IAccountOutbox outbox,
        IOptions<AuthOptions> auth,
        IOptions<PostgresOptions> postgres,
        ILogger<PostgresTwoStepService> logger,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(postgres);

        _db = db;
        _hasher = hasher;
        _outbox = outbox;
        _options = auth.Value.TwoStep;
        _lockout = auth.Value.Lockout;
        _box = SecretBox.FromConfiguration(_options.SecretKey);
        _clock = clock ?? TimeProvider.System;
        _commandTimeout = postgres.Value.CommandTimeoutSeconds;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsAvailable => _box is not null;

    // =======================================================================
    // Signing in
    // =======================================================================

    /// <inheritdoc />
    public async Task<TwoStepChallenge> BeginChallengeAsync(
        long userId, bool remember, CancellationToken ct)
    {
        var ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var expires = await connection.ExecuteScalarAsync<DateTimeOffset>(_db.Command("""
            INSERT INTO auth.sign_in_challenge (token_sha256, user_id, remember, expires_at)
            VALUES (@hash, @userId, @remember, now() + @lifetime)
            RETURNING expires_at
            """,
            new { hash = Sha256(ticket), userId, remember, lifetime = _options.ChallengeLifetime },
            ct)).ConfigureAwait(false);

        return new TwoStepChallenge(ticket, expires);
    }

    /// <inheritdoc />
    public async Task<TwoStepResult> CompleteChallengeAsync(
        string ticket, string code, AuthenticationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var hash = Sha256(ticket);

        // The attempt is counted before the code is compared, in the statement that refuses too
        // many, so guesses sent at the same moment cannot all see room for one more.
        var challenge = await connection.QuerySingleOrDefaultAsync<ChallengeRow>(_db.Command("""
            UPDATE auth.sign_in_challenge
            SET attempts = attempts + 1
            WHERE token_sha256 = @hash AND expires_at > now() AND attempts < @max
            RETURNING user_id AS UserId, remember AS Remember, attempts AS Attempts
            """,
            new { hash, max = _options.MaxChallengeAttempts }, ct)).ConfigureAwait(false);

        if (challenge is null)
        {
            return new TwoStepResult(TwoStepResultStatus.ChallengeExpired);
        }

        var account = await ReadAccountAsync(connection, challenge.UserId, ct).ConfigureAwait(false);

        // Deactivated or turned off since the password step: nothing this ticket proved still holds.
        if (account is null || !account.IsActive || account.ConfirmedAt is null)
        {
            await DeleteChallengeAsync(connection, hash, ct).ConfigureAwait(false);
            return new TwoStepResult(TwoStepResultStatus.ChallengeExpired);
        }

        if (account.LockedUntil is { } until && until > _clock.GetUtcNow())
        {
            await DeleteChallengeAsync(connection, hash, ct).ConfigureAwait(false);
            return new TwoStepResult(TwoStepResultStatus.AccountLocked, LockedUntil: until);
        }

        var method = await CheckCodeAsync(connection, account, code, ct).ConfigureAwait(false);

        if (method is null)
        {
            var failure = await IdentitySql.RecordFailedSignInAsync(
                connection, null, account.Id, _lockout, _commandTimeout, ct).ConfigureAwait(false);

            await AuditSignInAsync(connection, null, account, AuditOutcome.Failure, context,
                new Dictionary<string, object?>
                {
                    ["reason"] = "bad_second_step",
                    ["failedAttempts"] = failure.FailedLoginCount,
                    ["lockedUntil"] = failure.LockedUntil,
                }, ct).ConfigureAwait(false);

            if (challenge.Attempts >= _options.MaxChallengeAttempts)
            {
                await DeleteChallengeAsync(connection, hash, ct).ConfigureAwait(false);
                return new TwoStepResult(TwoStepResultStatus.ChallengeExpired);
            }

            return failure.LockedUntil is { } lockedUntil && lockedUntil > _clock.GetUtcNow()
                ? new TwoStepResult(TwoStepResultStatus.AccountLocked, LockedUntil: lockedUntil)
                : new TwoStepResult(TwoStepResultStatus.InvalidCode);
        }

        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var taken = await connection.ExecuteAsync(_db.Command(
            "DELETE FROM auth.sign_in_challenge WHERE token_sha256 = @hash",
            new { hash }, ct, transaction)).ConfigureAwait(false);

        if (taken == 0)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return new TwoStepResult(TwoStepResultStatus.ChallengeExpired);
        }

        await IdentitySql.RecordSignInAsync(
            connection, transaction, account.Id, context.Ip, _commandTimeout, ct).ConfigureAwait(false);

        await AuditSignInAsync(connection, transaction, account, AuditOutcome.Success, context,
            new Dictionary<string, object?> { ["secondStep"] = method }, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return new TwoStepResult(TwoStepResultStatus.Success, account.Id, challenge.Remember);
    }

    // =======================================================================
    // Managing it
    // =======================================================================

    /// <inheritdoc />
    public async Task<TwoStepStatus> GetStatusAsync(long userId, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var row = await connection.QuerySingleAsync<StatusRow>(_db.Command("""
            SELECT (SELECT confirmed_at FROM auth.user_two_step WHERE user_id = @userId) AS EnabledAt,
                   (SELECT count(*)::int FROM auth.user_recovery_code
                    WHERE user_id = @userId AND used_at IS NULL) AS RecoveryCodesLeft,
                   EXISTS (SELECT 1 FROM auth.user_role ur
                           JOIN auth.role r ON r.id = ur.role_id
                           WHERE ur.user_id = @userId AND r.two_step_required) AS Required
            """, new { userId }, ct)).ConfigureAwait(false);

        return new TwoStepStatus(
            IsAvailable, row.EnabledAt is not null, row.EnabledAt, row.RecoveryCodesLeft,
            row.Required && IsAvailable);
    }

    /// <inheritdoc />
    public async Task<TwoStepSetup?> BeginSetupAsync(
        long userId, string currentPassword, AuthenticationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var box = _box ?? throw new TwoStepRefusedException(
            "Two-step sign-in is not available on this system.");

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var proved = await IdentitySql.VerifyOwnPasswordAsync(
            connection, _hasher, userId, currentPassword, "auth.two_step.enable", context,
            _commandTimeout, ct).ConfigureAwait(false);

        if (!proved)
        {
            return null;
        }

        var account = await ReadAccountAsync(connection, userId, ct).ConfigureAwait(false);

        if (account?.ConfirmedAt is not null)
        {
            throw new TwoStepRefusedException("Two-step sign-in is already on.");
        }

        var secret = RandomNumberGenerator.GetBytes(Totp.SecretLength);

        // Replaces a setup abandoned earlier. Nothing depends on an unconfirmed row.
        await connection.ExecuteAsync(_db.Command("""
            INSERT INTO auth.user_two_step (user_id, secret)
            VALUES (@userId, @sealed)
            ON CONFLICT (user_id) DO UPDATE
            SET secret = EXCLUDED.secret, created_at = now(), last_step = 0
            """,
            new { userId, @sealed = box.Seal(secret, userId) }, ct)).ConfigureAwait(false);

        var base32 = Base32.Encode(secret);

        return new TwoStepSetup(base32, Totp.Uri(_options.Issuer, account!.Username, base32));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>?> ConfirmSetupAsync(
        long userId, string code, Guid currentSessionId, AuthenticationContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var account = await ReadAccountAsync(connection, userId, ct).ConfigureAwait(false);

        if (account?.Secret is null || account.ConfirmedAt is not null)
        {
            return null;
        }

        var secret = _box?.Open(account.Secret, userId);
        var step = secret is null
            ? null
            : Totp.Match(secret, OneTimeCodes.Clean(code), _clock.GetUtcNow());

        if (step is null)
        {
            return null;
        }

        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var turnedOn = await connection.ExecuteAsync(_db.Command("""
            UPDATE auth.user_two_step
            SET confirmed_at = now(), last_step = @step
            WHERE user_id = @userId AND confirmed_at IS NULL
            """, new { userId, step }, ct, transaction)).ConfigureAwait(false);

        if (turnedOn == 0)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return null;
        }

        var codes = await ReplaceRecoveryCodesAsync(connection, transaction, userId, ct)
            .ConfigureAwait(false);

        // A session that began before this protection should not outlive it. This one stays:
        // it has just proved both steps.
        var revoked = await PostgresSessionStore.RevokeAllForUserAsync(
            connection, transaction, userId, currentSessionId, account.Username,
            "two-step sign-in turned on", _commandTimeout, ct).ConfigureAwait(false);

        await AuditChangeAsync(connection, transaction, "auth.two_step.enable", account.Username,
            userId, account.Username, context,
            new Dictionary<string, object?> { ["sessionsRevoked"] = revoked }, ct)
            .ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        LogChange("turned on", userId);
        _outbox.TryEnqueue(new AccountNoticeWork(userId, AccountNotice.TwoStepOn));

        return codes;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>?> RegenerateRecoveryCodesAsync(
        long userId, string currentPassword, AuthenticationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var proved = await IdentitySql.VerifyOwnPasswordAsync(
            connection, _hasher, userId, currentPassword, "auth.two_step.recovery_codes", context,
            _commandTimeout, ct).ConfigureAwait(false);

        if (!proved)
        {
            return null;
        }

        var account = await ReadAccountAsync(connection, userId, ct).ConfigureAwait(false);

        if (account?.ConfirmedAt is null)
        {
            throw new TwoStepRefusedException("Two-step sign-in is not on.");
        }

        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var codes = await ReplaceRecoveryCodesAsync(connection, transaction, userId, ct)
            .ConfigureAwait(false);

        await AuditChangeAsync(connection, transaction, "auth.two_step.recovery_codes",
            account.Username, userId, account.Username, context, null, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return codes;
    }

    /// <inheritdoc />
    public async Task<bool> DisableAsync(
        long userId, string currentPassword, string code, AuthenticationContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var proved = await IdentitySql.VerifyOwnPasswordAsync(
            connection, _hasher, userId, currentPassword, "auth.two_step.disable", context,
            _commandTimeout, ct).ConfigureAwait(false);

        if (!proved)
        {
            return false;
        }

        var account = await ReadAccountAsync(connection, userId, ct).ConfigureAwait(false);

        if (account?.ConfirmedAt is null
            || await CheckCodeAsync(connection, account, code, ct).ConfigureAwait(false) is null)
        {
            return false;
        }

        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await RemoveAsync(connection, transaction, userId, ct).ConfigureAwait(false);

        await AuditChangeAsync(connection, transaction, "auth.two_step.disable", account.Username,
            userId, account.Username, context, null, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        LogChange("turned off", userId);
        _outbox.TryEnqueue(new AccountNoticeWork(userId, AccountNotice.TwoStepOff));

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> ResetAsync(
        long userId, string actor, AuthenticationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var username = await connection.ExecuteScalarAsync<string?>(_db.Command(
            "SELECT username FROM auth.user_account WHERE id = @userId", new { userId }, ct))
            .ConfigureAwait(false);

        if (username is null)
        {
            return false;
        }

        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await RemoveAsync(connection, transaction, userId, ct).ConfigureAwait(false);

        // Whoever was in with the old authenticator should have to come back through the new one.
        var revoked = await PostgresSessionStore.RevokeAllForUserAsync(
            connection, transaction, userId, null, actor, "two-step sign-in reset by administrator",
            _commandTimeout, ct).ConfigureAwait(false);

        await AuditChangeAsync(connection, transaction, "user.two_step.reset", actor, userId,
            username, context, new Dictionary<string, object?> { ["sessionsRevoked"] = revoked }, ct)
            .ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        LogChange("reset by an administrator", userId);
        _outbox.TryEnqueue(new AccountNoticeWork(userId, AccountNotice.TwoStepOff));

        return true;
    }

    // =======================================================================

    /// <summary>What proved the code, or <see langword="null"/> if nothing did.</summary>
    private async Task<string?> CheckCodeAsync(
        NpgsqlConnection connection, AccountRow account, string code, CancellationToken ct)
    {
        var recovery = RecoveryCodes.Normalise(code);

        if (recovery is not null)
        {
            var used = await connection.ExecuteAsync(_db.Command("""
                UPDATE auth.user_recovery_code
                SET used_at = now()
                WHERE user_id = @userId AND code_sha256 = @hash AND used_at IS NULL
                """,
                new { userId = account.Id, hash = RecoveryCodes.Hash(recovery) }, ct))
                .ConfigureAwait(false);

            return used == 1 ? "recovery_code" : null;
        }

        var secret = account.Secret is null ? null : _box?.Open(account.Secret, account.Id);

        if (secret is null)
        {
            LogUnreadable(account.Id);
            return null;
        }

        var step = Totp.Match(secret, OneTimeCodes.Clean(code), _clock.GetUtcNow());

        if (step is null)
        {
            return null;
        }

        // In one statement, so a code cannot be used twice even by two requests at once.
        var accepted = await connection.ExecuteAsync(_db.Command("""
            UPDATE auth.user_two_step
            SET last_step = @step
            WHERE user_id = @userId AND last_step < @step
            """, new { userId = account.Id, step }, ct)).ConfigureAwait(false);

        return accepted == 1 ? "authenticator" : null;
    }

    private async Task<IReadOnlyList<string>> ReplaceRecoveryCodesAsync(
        NpgsqlConnection connection, System.Data.IDbTransaction transaction, long userId,
        CancellationToken ct)
    {
        await connection.ExecuteAsync(_db.Command(
            "DELETE FROM auth.user_recovery_code WHERE user_id = @userId",
            new { userId }, ct, transaction)).ConfigureAwait(false);

        var codes = Enumerable.Range(0, _options.RecoveryCodeCount)
            .Select(_ => RecoveryCodes.Generate())
            .ToList();

        foreach (var code in codes)
        {
            await connection.ExecuteAsync(_db.Command(
                "INSERT INTO auth.user_recovery_code (user_id, code_sha256) VALUES (@userId, @hash)",
                new { userId, hash = RecoveryCodes.Hash(RecoveryCodes.Normalise(code)!) },
                ct, transaction)).ConfigureAwait(false);
        }

        return codes;
    }

    private async Task RemoveAsync(
        NpgsqlConnection connection, System.Data.IDbTransaction transaction, long userId,
        CancellationToken ct)
    {
        await connection.ExecuteAsync(_db.Command(
            "DELETE FROM auth.user_recovery_code WHERE user_id = @userId",
            new { userId }, ct, transaction)).ConfigureAwait(false);

        await connection.ExecuteAsync(_db.Command(
            "DELETE FROM auth.user_two_step WHERE user_id = @userId",
            new { userId }, ct, transaction)).ConfigureAwait(false);
    }

    private async Task<AccountRow?> ReadAccountAsync(
        NpgsqlConnection connection, long userId, CancellationToken ct) =>
        await connection.QuerySingleOrDefaultAsync<AccountRow>(_db.Command("""
            SELECT u.id AS Id, u.username AS Username, u.is_active AS IsActive,
                   u.locked_until AS LockedUntil, t.secret AS Secret,
                   t.confirmed_at AS ConfirmedAt
            FROM auth.user_account u
            LEFT JOIN auth.user_two_step t ON t.user_id = u.id
            WHERE u.id = @userId
            """, new { userId }, ct)).ConfigureAwait(false);

    private async Task DeleteChallengeAsync(
        NpgsqlConnection connection, byte[] hash, CancellationToken ct) =>
        await connection.ExecuteAsync(_db.Command(
            "DELETE FROM auth.sign_in_challenge WHERE token_sha256 = @hash", new { hash }, ct))
            .ConfigureAwait(false);

    private Task AuditSignInAsync(
        NpgsqlConnection connection, System.Data.IDbTransaction? transaction, AccountRow account,
        AuditOutcome outcome, AuthenticationContext context,
        IReadOnlyDictionary<string, object?> detail, CancellationToken ct) =>
        // The same action as the password step, so a sign-in reads as one thing in the log.
        IdentitySql.WriteAuditAsync(connection, transaction, new AuditEntry(
            ActorName: account.Username,
            Action: "auth.login",
            Category: AuditCategory.Authentication,
            Outcome: outcome,
            ActorUserId: account.Id,
            Ip: context.Ip,
            UserAgent: context.UserAgent,
            CorrelationId: context.CorrelationId,
            Detail: detail), _commandTimeout, ct);

    private async Task AuditChangeAsync(
        NpgsqlConnection connection, System.Data.IDbTransaction transaction, string action,
        string actor, long userId, string username, AuthenticationContext context,
        IReadOnlyDictionary<string, object?>? detail, CancellationToken ct)
    {
        var actorId = await IdentitySql
            .ResolveActorIdAsync(connection, transaction, actor, _commandTimeout, ct)
            .ConfigureAwait(false);

        await IdentitySql.WriteAuditAsync(connection, transaction, new AuditEntry(
            ActorName: actor,
            Action: action,
            Category: AuditCategory.Authentication,
            Outcome: AuditOutcome.Success,
            ActorUserId: actorId,
            TargetType: "user",
            TargetId: userId.ToString(CultureInfo.InvariantCulture),
            TargetName: username,
            Ip: context.Ip,
            UserAgent: context.UserAgent,
            CorrelationId: context.CorrelationId,
            Detail: detail), _commandTimeout, ct).ConfigureAwait(false);
    }

    private static byte[] Sha256(string ticket) => SHA256.HashData(Encoding.UTF8.GetBytes(ticket));

    private sealed record ChallengeRow(long UserId, bool Remember, short Attempts);

    private sealed record StatusRow(DateTimeOffset? EnabledAt, int RecoveryCodesLeft, bool Required);

    private sealed record AccountRow(
        long Id,
        string Username,
        bool IsActive,
        DateTimeOffset? LockedUntil,
        byte[]? Secret,
        DateTimeOffset? ConfirmedAt);
}
