using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Sqm.Application.Identity;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Infrastructure.Identity;

/// <summary>Proves the e-mail address and phone number on a person's own profile.</summary>
public sealed partial class PostgresContactVerification : IContactVerification
{
    private const string Action = "auth.contact.verify";

    [LoggerMessage(EventId = 4210, Level = LogLevel.Error,
        Message = "A verification code to user {UserId} by {Channel} could not be sent")]
    private partial void LogDeliveryFailed(Exception exception, long userId, ContactChannel channel);

    private readonly IdentityDataSource _db;
    private readonly IPasswordHasher _hasher;
    private readonly IContactDelivery _delivery;
    private readonly OneTimeCodes _codes;
    private readonly RecoveryOptions _options;
    private readonly int _commandTimeout;
    private readonly ILogger<PostgresContactVerification> _logger;

    /// <summary>Creates the service.</summary>
    public PostgresContactVerification(
        IdentityDataSource db,
        IPasswordHasher hasher,
        IContactDelivery delivery,
        IOptions<AuthOptions> auth,
        IOptions<PostgresOptions> postgres,
        ILogger<PostgresContactVerification> logger)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(postgres);

        _db = db;
        _hasher = hasher;
        _delivery = delivery;
        _options = auth.Value.Recovery;
        _codes = new OneTimeCodes(db, hasher, _options);
        _commandTimeout = postgres.Value.CommandTimeoutSeconds;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ContactCodeOutcome> SendCodeAsync(
        long userId, ContactChannel channel, string currentPassword, AuthenticationContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var proved = await IdentitySql.VerifyOwnPasswordAsync(
            connection, _hasher, userId, currentPassword, Action, context, _commandTimeout, ct)
            .ConfigureAwait(false);

        if (!proved)
        {
            return ContactCodeOutcome.WrongPassword;
        }

        var contact = await ReadAsync(connection, userId, ct).ConfigureAwait(false);
        var destination = Destination(contact, channel);

        if (destination is null)
        {
            return ContactCodeOutcome.NoContact;
        }

        if (IsProved(contact, channel))
        {
            return ContactCodeOutcome.AlreadyVerified;
        }

        var code = await _codes
            .IssueAsync(userId, OneTimeCodes.VerifyContact, channel, destination, ct)
            .ConfigureAwait(false);

        if (code is null)
        {
            return ContactCodeOutcome.TooSoon;
        }

        try
        {
            await _delivery.SendAsync(
                channel, destination, AccountMessages.VerifyCode(code, _options.CodeLifetime), ct)
                .ConfigureAwait(false);
        }
        catch (ContactDeliveryException ex)
        {
            LogDeliveryFailed(ex, userId, channel);
            await AuditAsync(connection, null, contact, channel, AuditOutcome.Failure,
                "delivery_failed", context, ct).ConfigureAwait(false);
            return ContactCodeOutcome.DeliveryFailed;
        }

        return ContactCodeOutcome.Sent;
    }

    /// <inheritdoc />
    public async Task<ContactConfirmOutcome> ConfirmAsync(
        long userId, ContactChannel channel, string code, AuthenticationContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var contact = await ReadAsync(connection, userId, ct).ConfigureAwait(false);
        var destination = Destination(contact, channel);

        var live = await _codes.FindLiveAsync(userId, OneTimeCodes.VerifyContact, ct)
            .ConfigureAwait(false);

        // A code proves the value it was sent to. If the profile has changed since, or this code
        // went out on the other channel, it proves nothing about what is there now.
        if (live is not null
            && (live.Channel != OneTimeCodes.ToDatabase(channel)
                || !string.Equals(live.Destination, destination, StringComparison.OrdinalIgnoreCase)))
        {
            live = null;
        }

        var check = await _codes.CheckAsync(live, code, ct).ConfigureAwait(false);

        if (check != CodeCheck.Right)
        {
            await AuditAsync(connection, null, contact, channel, AuditOutcome.Failure,
                check == CodeCheck.Void ? "code_void" : "bad_code", context, ct)
                .ConfigureAwait(false);
            return ContactConfirmOutcome.InvalidCode;
        }

        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        if (!await _codes.ConsumeAsync(connection, transaction, live!.Id, ct).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return ContactConfirmOutcome.InvalidCode;
        }

        // A number is stored in its international form from here on, so that it can be found again
        // from whichever way the person writes it.
        var sql = channel == ContactChannel.Email
            ? "UPDATE auth.user_account SET email_verified_at = now() WHERE id = @userId"
            : "UPDATE auth.user_account SET phone = @destination, phone_verified_at = now() "
                + "WHERE id = @userId";

        try
        {
            await connection.ExecuteAsync(_db.Command(
                sql, new { userId, destination }, ct, transaction)).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            await AuditAsync(connection, null, contact, channel, AuditOutcome.Denied,
                "in_use", context, ct).ConfigureAwait(false);
            return ContactConfirmOutcome.InUse;
        }

        await AuditAsync(connection, transaction, contact, channel, AuditOutcome.Success, null,
            context, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return ContactConfirmOutcome.Verified;
    }

    private async Task<ContactRow> ReadAsync(
        NpgsqlConnection connection, long userId, CancellationToken ct) =>
        await connection.QuerySingleAsync<ContactRow>(_db.Command("""
            SELECT id AS Id, username AS Username, email AS Email, phone AS Phone,
                   email_verified_at IS NOT NULL AS EmailProved,
                   phone_verified_at IS NOT NULL AS PhoneProved
            FROM auth.user_account
            WHERE id = @userId
            """, new { userId }, ct)).ConfigureAwait(false);

    private string? Destination(ContactRow contact, ContactChannel channel)
    {
        if (channel == ContactChannel.Sms)
        {
            return PhoneNumber.Normalise(contact.Phone, _options.DefaultCountryCode);
        }

        var email = contact.Email?.Trim();
        return string.IsNullOrEmpty(email) || !email.Contains('@', StringComparison.Ordinal)
            ? null
            : email;
    }

    private static bool IsProved(ContactRow contact, ContactChannel channel) =>
        channel == ContactChannel.Email ? contact.EmailProved : contact.PhoneProved;

    private Task AuditAsync(
        NpgsqlConnection connection, System.Data.IDbTransaction? transaction, ContactRow contact,
        ContactChannel channel, AuditOutcome outcome, string? reason,
        AuthenticationContext context, CancellationToken ct)
    {
        var detail = new Dictionary<string, object?> { ["channel"] = OneTimeCodes.ToDatabase(channel) };
        if (reason is not null)
        {
            detail["reason"] = reason;
        }

        return IdentitySql.WriteAuditAsync(connection, transaction, new AuditEntry(
            ActorName: contact.Username,
            Action: Action,
            Category: AuditCategory.Authentication,
            Outcome: outcome,
            ActorUserId: contact.Id,
            Ip: context.Ip,
            UserAgent: context.UserAgent,
            CorrelationId: context.CorrelationId,
            Detail: detail), _commandTimeout, ct);
    }

    private sealed record ContactRow(
        long Id, string Username, string? Email, string? Phone, bool EmailProved, bool PhoneProved);
}
