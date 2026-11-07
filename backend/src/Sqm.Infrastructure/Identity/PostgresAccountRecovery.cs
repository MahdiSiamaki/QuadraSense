using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Sqm.Application.Identity;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Infrastructure.Identity;

/// <summary>Password recovery by a code sent to a proved contact.</summary>
public sealed partial class PostgresAccountRecovery : IAccountRecovery
{
    [LoggerMessage(EventId = 4200, Level = LogLevel.Information,
        Message = "Recovery request ended without a code: {Reason}")]
    private partial void LogNoCode(string reason);

    [LoggerMessage(EventId = 4201, Level = LogLevel.Information,
        Message = "Recovery code sent to user {UserId} by {Channel}")]
    private partial void LogSent(long userId, ContactChannel channel);

    [LoggerMessage(EventId = 4202, Level = LogLevel.Error,
        Message = "A message to user {UserId} by {Channel} could not be sent")]
    private partial void LogDeliveryFailed(Exception exception, long userId, ContactChannel channel);

    [LoggerMessage(EventId = 4203, Level = LogLevel.Information,
        Message = "User {UserId} reset their password by recovery")]
    private partial void LogReset(long userId);

    private readonly IdentityDataSource _db;
    private readonly IPasswordHasher _hasher;
    private readonly IContactDelivery _delivery;
    private readonly IAccountOutbox _outbox;
    private readonly OneTimeCodes _codes;
    private readonly RecoveryOptions _options;
    private readonly PasswordPolicyOptions _policy;
    private readonly int _commandTimeout;
    private readonly ILogger<PostgresAccountRecovery> _logger;

    /// <summary>Creates the service.</summary>
    public PostgresAccountRecovery(
        IdentityDataSource db,
        IPasswordHasher hasher,
        IContactDelivery delivery,
        IAccountOutbox outbox,
        IOptions<AuthOptions> auth,
        IOptions<PostgresOptions> postgres,
        ILogger<PostgresAccountRecovery> logger)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(postgres);

        _db = db;
        _hasher = hasher;
        _delivery = delivery;
        _outbox = outbox;
        _options = auth.Value.Recovery;
        _policy = auth.Value.Policy;
        _codes = new OneTimeCodes(db, hasher, _options);
        _commandTimeout = postgres.Value.CommandTimeoutSeconds;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task SendCodeAsync(
        string identifier, ContactChannel channel, AuthenticationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var account = await FindAsync(identifier, ct).ConfigureAwait(false);

        if (account is null)
        {
            await AuditRequestAsync(null, identifier, AuditOutcome.Failure, "unknown_account",
                channel, context, ct).ConfigureAwait(false);
            LogNoCode("unknown account");
            return;
        }

        var destination = channel == ContactChannel.Email
            ? account.VerifiedEmail
            : account.VerifiedPhone;

        if (destination is null)
        {
            await AuditRequestAsync(account, identifier, AuditOutcome.Denied, "no_proved_contact",
                channel, context, ct).ConfigureAwait(false);
            LogNoCode("no proved contact on this channel");
            return;
        }

        var code = await _codes
            .IssueAsync(account.Id, OneTimeCodes.PasswordReset, channel, destination, ct)
            .ConfigureAwait(false);

        if (code is null)
        {
            await AuditRequestAsync(account, identifier, AuditOutcome.Denied, "too_many_codes",
                channel, context, ct).ConfigureAwait(false);
            LogNoCode("too many codes for this account");
            return;
        }

        try
        {
            await _delivery.SendAsync(
                channel, destination,
                AccountMessages.ResetCode(code, account.Username, _options.CodeLifetime),
                ct).ConfigureAwait(false);
        }
        catch (ContactDeliveryException ex)
        {
            LogDeliveryFailed(ex, account.Id, channel);
            await AuditRequestAsync(account, identifier, AuditOutcome.Failure, "delivery_failed",
                channel, context, ct).ConfigureAwait(false);
            return;
        }

        await AuditRequestAsync(account, identifier, AuditOutcome.Success, null, channel, context, ct)
            .ConfigureAwait(false);
        LogSent(account.Id, channel);
    }

    /// <inheritdoc />
    public async Task SendNoticeAsync(long userId, AccountNotice notice, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var account = await connection.QuerySingleOrDefaultAsync<AccountRow>(_db.Command($"""
            SELECT {AccountColumns}
            FROM auth.user_account
            WHERE id = @userId
            """, new { userId }, ct)).ConfigureAwait(false);

        if (account is null)
        {
            return;
        }

        var message = AccountMessages.Notice(notice, account.Username);

        foreach (var (channel, destination) in new[]
        {
            (ContactChannel.Email, account.VerifiedEmail),
            (ContactChannel.Sms, account.VerifiedPhone),
        })
        {
            if (destination is null || !_delivery.IsAvailable(channel))
            {
                continue;
            }

            try
            {
                await _delivery.SendAsync(channel, destination, message, ct).ConfigureAwait(false);
            }
            catch (ContactDeliveryException ex)
            {
                // A notice is a courtesy. The change it reports has already happened.
                LogDeliveryFailed(ex, userId, channel);
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> CompleteAsync(
        string identifier, string code, string newPassword, AuthenticationContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The rules that need no account, before anything else: a password too short tells the
        // person so without spending an attempt, and it says nothing about any account.
        var problem = PasswordPolicy.Validate(newPassword, _policy, null, null);
        if (problem is not null)
        {
            throw new PasswordPolicyException(problem);
        }

        var account = await FindAsync(identifier, ct).ConfigureAwait(false);

        var live = account is null
            ? null
            : await _codes.FindLiveAsync(account.Id, OneTimeCodes.PasswordReset, ct)
                .ConfigureAwait(false);

        var check = await _codes.CheckAsync(live, code, ct).ConfigureAwait(false);

        if (check != CodeCheck.Right)
        {
            await AuditCompletionAsync(account, identifier, AuditOutcome.Failure,
                check == CodeCheck.Void ? "code_void" : "bad_code", context, ct)
                .ConfigureAwait(false);
            return false;
        }

        // A right code and a password with the owner's name in it. Whoever holds the code holds
        // the contact, so telling them this reveals nothing, and the code stays usable.
        problem = PasswordPolicy.Validate(
            newPassword, _policy, account!.Username, account.DisplayName);
        if (problem is not null)
        {
            throw new PasswordPolicyException(problem);
        }

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        if (!await _codes.ConsumeAsync(connection, transaction, live!.Id, ct).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }

        const string sql = """
            UPDATE auth.user_account
            SET password_hash        = @hash,
                password_updated_at  = now(),
                must_change_password = false,
                failed_login_count   = 0,
                locked_until         = NULL,
                updated_at           = now(),
                updated_by           = username
            WHERE id = @userId
            """;

        await connection.ExecuteAsync(_db.Command(
            sql, new { userId = account.Id, hash = _hasher.Hash(newPassword) }, ct, transaction))
            .ConfigureAwait(false);

        // Every session, this time: there is no "current" one, and a person recovering an account
        // is exactly the person who does not know who else is in it.
        var revoked = await PostgresSessionStore.RevokeAllForUserAsync(
            connection, transaction, account.Id, null, account.Username,
            "password reset by recovery", _commandTimeout, ct).ConfigureAwait(false);

        await IdentitySql.WriteAuditAsync(connection, transaction, new AuditEntry(
            ActorName: account.Username,
            Action: "auth.recovery.complete",
            Category: AuditCategory.Authentication,
            Outcome: AuditOutcome.Success,
            ActorUserId: account.Id,
            TargetType: "user",
            TargetId: account.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            TargetName: account.Username,
            Ip: context.Ip,
            UserAgent: context.UserAgent,
            CorrelationId: context.CorrelationId,
            Detail: new Dictionary<string, object?>
            {
                ["channel"] = live.Channel,
                ["sessionsRevoked"] = revoked,
            }), _commandTimeout, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        LogReset(account.Id);
        _outbox.TryEnqueue(new AccountNoticeWork(account.Id, AccountNotice.PasswordReset));

        return true;
    }

    private const string AccountColumns = """
        id AS Id, username AS Username, display_name AS DisplayName,
        CASE WHEN email_verified_at IS NOT NULL THEN email END AS VerifiedEmail,
        CASE WHEN phone_verified_at IS NOT NULL THEN phone END AS VerifiedPhone
        """;

    /// <summary>The active local account this identifier names, if any.</summary>
    /// <remarks>
    /// A contact only names an account once it is proved, so nobody can type an address they
    /// merely put on their profile and have it find an account. A deactivated account and one
    /// whose password lives in a directory find nothing, and are told nothing different.
    /// </remarks>
    private async Task<AccountRow?> FindAsync(string identifier, CancellationToken ct)
    {
        var key = identifier.Trim();

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        return await connection.QueryFirstOrDefaultAsync<AccountRow>(_db.Command($"""
            SELECT {AccountColumns}
            FROM auth.user_account
            WHERE is_active AND provider = 'local'
              AND (lower(username) = lower(@key)
                   OR (email_verified_at IS NOT NULL AND lower(email) = lower(@key))
                   OR (phone_verified_at IS NOT NULL AND phone = @phone))
            ORDER BY (lower(username) = lower(@key)) DESC
            LIMIT 1
            """,
            new { key, phone = PhoneNumber.Normalise(key, _options.DefaultCountryCode) }, ct))
            .ConfigureAwait(false);
    }

    private Task AuditRequestAsync(
        AccountRow? account, string identifier, AuditOutcome outcome, string? reason,
        ContactChannel channel, AuthenticationContext context, CancellationToken ct)
    {
        var detail = new Dictionary<string, object?> { ["channel"] = OneTimeCodes.ToDatabase(channel) };
        if (reason is not null)
        {
            detail["reason"] = reason;
        }

        return AuditAsync(account, identifier, "auth.recovery.request", outcome, detail, context, ct);
    }

    private Task AuditCompletionAsync(
        AccountRow? account, string identifier, AuditOutcome outcome, string reason,
        AuthenticationContext context, CancellationToken ct) =>
        AuditAsync(account, identifier, "auth.recovery.complete", outcome,
            new Dictionary<string, object?> { ["reason"] = reason }, context, ct);

    private async Task AuditAsync(
        AccountRow? account, string identifier, string action, AuditOutcome outcome,
        IReadOnlyDictionary<string, object?> detail, AuthenticationContext context,
        CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        await IdentitySql.WriteAuditAsync(connection, null, new AuditEntry(
            // As typed when it matches nothing, as a failed sign-in is: a run of requests for
            // names that do not exist is the pattern this log most needs to show.
            ActorName: account?.Username ?? IdentitySql.Truncate(identifier, 200) ?? "(empty)",
            Action: action,
            Category: AuditCategory.Authentication,
            Outcome: outcome,
            ActorUserId: account?.Id,
            Ip: context.Ip,
            UserAgent: context.UserAgent,
            CorrelationId: context.CorrelationId,
            Detail: detail), _commandTimeout, ct).ConfigureAwait(false);
    }

    private sealed record AccountRow(
        long Id, string Username, string DisplayName, string? VerifiedEmail, string? VerifiedPhone);
}
