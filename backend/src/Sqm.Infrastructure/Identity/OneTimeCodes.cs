using System.Globalization;
using System.Security.Cryptography;
using Dapper;
using Sqm.Application.Identity;

namespace Sqm.Infrastructure.Identity;

/// <summary>Issues and checks the codes in <c>auth.one_time_code</c>, for every purpose.</summary>
/// <remarks>
/// One implementation, because the terms are one rule: six random digits, ten minutes, void after
/// five failures, used once, hashed at rest. A copy per flow would be a copy that drifts from
/// what the red lines ask for.
/// </remarks>
internal sealed class OneTimeCodes
{
    /// <summary>Resets a password.</summary>
    public const string PasswordReset = "password_reset";

    /// <summary>Proves a contact.</summary>
    public const string VerifyContact = "verify_contact";

    private const int CodeRange = 1_000_000;

    private readonly IdentityDataSource _db;
    private readonly IPasswordHasher _hasher;
    private readonly RecoveryOptions _options;

    public OneTimeCodes(IdentityDataSource db, IPasswordHasher hasher, RecoveryOptions options)
    {
        _db = db;
        _hasher = hasher;
        _options = options;
    }

    /// <summary>The stored form of a channel.</summary>
    public static string ToDatabase(ContactChannel channel) => channel switch
    {
        ContactChannel.Email => "email",
        ContactChannel.Sms => "sms",
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null),
    };

    /// <summary>What a person typed, as the digits it should be compared as.</summary>
    public static string Clean(string code) =>
        new([.. Digits.ToAscii(code).Where(c => !char.IsWhiteSpace(c))]);

    /// <summary>
    /// Creates a code and voids any earlier one for the same purpose, or returns
    /// <see langword="null"/> when this account has been sent too many, too recently.
    /// </summary>
    /// <remarks>
    /// The account's row is locked for the length of this, so two requests at once cannot both
    /// pass the limit. The limits are counted per account, across purposes: they protect a
    /// person's phone and an SMS bill, and neither cares why the message was sent.
    /// </remarks>
    public async Task<string?> IssueAsync(
        long userId, string purpose, ContactChannel channel, string destination, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await connection.ExecuteAsync(_db.Command(
            "SELECT 1 FROM auth.user_account WHERE id = @userId FOR UPDATE",
            new { userId }, ct, transaction)).ConfigureAwait(false);

        var recent = await connection.QuerySingleAsync<RecentRow>(_db.Command("""
            SELECT count(*)::int AS Sent,
                   COALESCE(bool_or(created_at > now() - @cooldown), false) AS TooSoon
            FROM auth.one_time_code
            WHERE user_id = @userId AND created_at > now() - interval '1 hour'
            """,
            new { userId, cooldown = _options.ResendCooldown }, ct, transaction))
            .ConfigureAwait(false);

        if (recent.TooSoon || recent.Sent >= _options.MaxSendsPerHour)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return null;
        }

        await connection.ExecuteAsync(_db.Command("""
            UPDATE auth.one_time_code
            SET consumed_at = now()
            WHERE user_id = @userId AND purpose = @purpose AND consumed_at IS NULL
            """,
            new { userId, purpose }, ct, transaction)).ConfigureAwait(false);

        var code = RandomNumberGenerator.GetInt32(CodeRange)
            .ToString("D6", CultureInfo.InvariantCulture);

        await connection.ExecuteAsync(_db.Command("""
            INSERT INTO auth.one_time_code
                (user_id, purpose, channel, destination, code_hash, expires_at)
            VALUES
                (@userId, @purpose, @channel, @destination, @hash, now() + @lifetime)
            """,
            new
            {
                userId,
                purpose,
                channel = ToDatabase(channel),
                destination,
                hash = _hasher.Hash(code),
                lifetime = _options.CodeLifetime,
            }, ct, transaction)).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return code;
    }

    /// <summary>The live code for this purpose, if there is one.</summary>
    public async Task<LiveCode?> FindLiveAsync(long userId, string purpose, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<LiveCode>(_db.Command("""
            SELECT id AS Id, channel AS Channel, destination AS Destination, code_hash AS CodeHash
            FROM auth.one_time_code
            WHERE user_id = @userId AND purpose = @purpose
              AND consumed_at IS NULL AND expires_at > now() AND attempts < @max
            ORDER BY id DESC
            LIMIT 1
            """,
            new { userId, purpose, max = _options.MaxCodeAttempts }, ct)).ConfigureAwait(false);
    }

    /// <summary>Compares a typed code with a live one, counting the attempt.</summary>
    /// <remarks>
    /// <para>
    /// The attempt is taken in the database before the hash is compared, in one statement that
    /// refuses a sixth. Counting afterwards would let any number of guesses sent at the same moment
    /// all see four attempts used.
    /// </para>
    /// <para>
    /// Exactly one hash is computed whatever happens, including when there is no code at all, so
    /// this takes the same time for an account that does not exist.
    /// </para>
    /// </remarks>
    public async Task<CodeCheck> CheckAsync(LiveCode? live, string typed, CancellationToken ct)
    {
        if (live is null)
        {
            _hasher.BurnTime();
            return CodeCheck.Wrong;
        }

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var attempts = await connection.ExecuteScalarAsync<short?>(_db.Command("""
            UPDATE auth.one_time_code
            SET attempts = attempts + 1
            WHERE id = @id AND consumed_at IS NULL AND expires_at > now() AND attempts < @max
            RETURNING attempts
            """,
            new { id = live.Id, max = _options.MaxCodeAttempts }, ct)).ConfigureAwait(false);

        if (attempts is null)
        {
            _hasher.BurnTime();
            return CodeCheck.Wrong;
        }

        var right = _hasher.Verify(Clean(typed), live.CodeHash) != PasswordVerification.Failed;

        if (right)
        {
            return CodeCheck.Right;
        }

        if (attempts < _options.MaxCodeAttempts)
        {
            return CodeCheck.Wrong;
        }

        await connection.ExecuteAsync(_db.Command(
            "UPDATE auth.one_time_code SET consumed_at = now() WHERE id = @id",
            new { id = live.Id }, ct)).ConfigureAwait(false);

        return CodeCheck.Void;
    }

    /// <summary>Uses a code up, inside the change it allows.</summary>
    /// <returns><see langword="false"/> when something else used it first.</returns>
    public async Task<bool> ConsumeAsync(
        Npgsql.NpgsqlConnection connection, System.Data.IDbTransaction transaction, long id,
        CancellationToken ct)
    {
        var affected = await connection.ExecuteAsync(_db.Command(
            "UPDATE auth.one_time_code SET consumed_at = now() "
            + "WHERE id = @id AND consumed_at IS NULL",
            new { id }, ct, transaction)).ConfigureAwait(false);

        return affected == 1;
    }

    private sealed record RecentRow(int Sent, bool TooSoon);
}

/// <summary>A code that may still be used.</summary>
internal sealed record LiveCode(long Id, string Channel, string Destination, string CodeHash);

/// <summary>What a comparison found.</summary>
internal enum CodeCheck
{
    /// <summary>Right, and not yet used up.</summary>
    Right,

    /// <summary>Wrong, or nothing to compare with.</summary>
    Wrong,

    /// <summary>Wrong, and that was the last attempt.</summary>
    Void,
}
