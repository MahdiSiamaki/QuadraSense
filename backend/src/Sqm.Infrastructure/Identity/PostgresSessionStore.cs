using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Options;
using Sqm.Application.Identity;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Infrastructure.Identity;

/// <summary>Sessions in PostgreSQL, addressed by an opaque cookie value.</summary>
/// <remarks>
/// See ADR-006 for why sessions rather than tokens. The short version: deactivating a user has to
/// take effect, and a signed token cannot be withdrawn without adding back the per-request lookup
/// it was chosen to avoid.
/// </remarks>
public sealed class PostgresSessionStore : ISessionStore
{
    /// <summary>Token length in bytes.</summary>
    /// <remarks>
    /// 256 bits from <see cref="RandomNumberGenerator"/>. Unguessable by any margin that matters,
    /// and the reason the database can store a plain SHA-256 of it rather than a slow hash.
    /// </remarks>
    private const int TokenBytes = 32;

    private readonly IdentityDataSource _db;
    private readonly SessionSettings _options;
    private readonly int _commandTimeout;

    /// <summary>Creates the store.</summary>
    public PostgresSessionStore(
        IdentityDataSource db, IOptions<AuthOptions> auth, IOptions<PostgresOptions> postgres)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(postgres);

        _db = db;
        _options = auth.Value.Session;
        _commandTimeout = postgres.Value.CommandTimeoutSeconds;
    }

    /// <inheritdoc />
    public async Task<SessionCreation> CreateAsync(
        long userId, string? ip, string? userAgent, CancellationToken ct)
    {
        var token = GenerateToken();

        const string sql = """
            INSERT INTO auth.session
                (user_id, token_sha256, idle_expires_at, absolute_expires_at, ip, user_agent)
            VALUES
                (@userId, @hash, now() + @idle, now() + @absolute, @ip::inet, @userAgent)
            RETURNING id                  AS Id,
                      idle_expires_at     AS IdleExpiresAt,
                      absolute_expires_at AS AbsoluteExpiresAt
            """;

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var row = await connection.QuerySingleAsync<SessionRow>(_db.Command(sql, new
        {
            userId,
            hash = Sha256(token),
            idle = _options.IdleTimeout,
            absolute = _options.AbsoluteTimeout,
            ip = IdentitySql.NormaliseIp(ip),
            userAgent = IdentitySql.Truncate(userAgent, 512),
        }, ct)).ConfigureAwait(false);

        return new SessionCreation(row.Id, token, row.IdleExpiresAt, row.AbsoluteExpiresAt);
    }

    /// <inheritdoc />
    public async Task<ResolvedSession?> ResolveAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        // Every column is aliased to its record parameter name, and that is load-bearing rather
        // than tidiness. Dapper strips underscores when mapping to PROPERTIES but not when
        // matching a CONSTRUCTOR, so `idle_expires_at` never matches `IdleExpiresAt` on a
        // positional record - and the error it produces names the constructor signature, not the
        // column, so it reads as a type problem. (PostgreSQL folds an unquoted alias to lower
        // case; Dapper's comparison is case-insensitive, so the alias only has to lose the
        // underscores.)
        //
        // One round trip: the session, the account, and the effective permission set.
        //
        // Every condition that could invalidate the session is in the WHERE clause rather than
        // checked afterwards in C#, so there is no path on which a caller forgets one. A revoked
        // session, an expired session and a deactivated account all produce no row, and the
        // caller cannot tell them apart - which is correct, because the answer is the same.
        var sql = $"""
            WITH s AS (
                SELECT id, user_id, last_seen_at, idle_expires_at, absolute_expires_at
                FROM auth.session
                WHERE token_sha256 = @hash
                  AND revoked_at IS NULL
                  AND idle_expires_at > now()
                  AND absolute_expires_at > now()
            )
            SELECT s.id                   AS SessionId,
                   u.id                   AS UserId,
                   u.username             AS Username,
                   u.display_name         AS DisplayName,
                   u.must_change_password AS MustChangePassword,
                   s.last_seen_at         AS LastSeenAt,
                   s.idle_expires_at      AS IdleExpiresAt,
                   s.absolute_expires_at  AS AbsoluteExpiresAt,
                   ARRAY({IdentitySql.GrantedPermissionCodes("s.user_id")}) AS permissions
            FROM s
            JOIN auth.user_account u ON u.id = s.user_id AND u.is_active
            """;

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var row = await connection.QuerySingleOrDefaultAsync<ResolveRow>(
            _db.Command(sql, new { hash = Sha256(token) }, ct)).ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        var user = new AuthenticatedUser(
            row.UserId,
            row.Username,
            row.DisplayName,
            row.MustChangePassword,
            row.Permissions.ToFrozenSet(StringComparer.Ordinal));

        return new ResolvedSession(
            row.SessionId, user, row.LastSeenAt, row.IdleExpiresAt, row.AbsoluteExpiresAt);
    }

    /// <inheritdoc />
    public async Task TouchAsync(Guid sessionId, CancellationToken ct)
    {
        // LEAST, so an active session still dies at its absolute deadline. Without it the idle
        // window would push past the absolute one and the ceiling would mean nothing.
        const string sql = """
            UPDATE auth.session
            SET last_seen_at    = now(),
                idle_expires_at = LEAST(now() + @idle, absolute_expires_at)
            WHERE id = @sessionId
              AND revoked_at IS NULL
              AND last_seen_at < now() - @touchInterval
            """;

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        await connection.ExecuteAsync(_db.Command(sql, new
        {
            sessionId,
            idle = _options.IdleTimeout,
            touchInterval = _options.TouchInterval,
        }, ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> RevokeAsync(
        Guid sessionId, string revokedBy, string reason, CancellationToken ct)
    {
        const string sql = """
            UPDATE auth.session
            SET revoked_at = now(), revoked_by = @revokedBy, revoked_reason = @reason
            WHERE id = @sessionId AND revoked_at IS NULL
            """;

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var affected = await connection.ExecuteAsync(
            _db.Command(sql, new { sessionId, revokedBy, reason }, ct)).ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<int> RevokeAllForUserAsync(
        long userId, Guid? exceptSessionId, string revokedBy, string reason, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        return await RevokeAllForUserAsync(
            connection, null, userId, exceptSessionId, revokedBy, reason, _commandTimeout, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Revokes a user's sessions inside someone else's transaction.</summary>
    /// <remarks>
    /// Used by the user directory, so that deactivating an account and ending its sessions commit
    /// together. An account disabled in one transaction and logged out in another has a window -
    /// short, but real - in which it is disabled and still working.
    /// </remarks>
    internal static async Task<int> RevokeAllForUserAsync(
        Npgsql.NpgsqlConnection connection,
        System.Data.IDbTransaction? transaction,
        long userId,
        Guid? exceptSessionId,
        string revokedBy,
        string reason,
        int commandTimeout,
        CancellationToken ct)
    {
        const string sql = """
            UPDATE auth.session
            SET revoked_at = now(), revoked_by = @revokedBy, revoked_reason = @reason
            WHERE user_id = @userId
              AND revoked_at IS NULL
              AND (@except::uuid IS NULL OR id <> @except::uuid)
            """;

        return await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { userId, except = exceptSessionId, revokedBy, reason },
            transaction, commandTimeout, cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionInfo>> ListForUserAsync(
        long userId, Guid? currentSessionId, int limit, CancellationToken ct)
    {
        const string sql = """
            SELECT id                  AS Id,
                   created_at          AS CreatedAt,
                   last_seen_at        AS LastSeenAt,
                   idle_expires_at     AS IdleExpiresAt,
                   absolute_expires_at AS AbsoluteExpiresAt,
                   revoked_at          AS RevokedAt,
                   revoked_reason      AS RevokedReason,
                   host(ip)            AS Ip,
                   user_agent          AS UserAgent,
                   (id = @current)     AS IsCurrent
            FROM auth.session
            WHERE user_id = @userId
            ORDER BY (revoked_at IS NULL AND idle_expires_at > now()) DESC, last_seen_at DESC
            LIMIT @limit
            """;

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var rows = await connection.QueryAsync<SessionInfo>(
            _db.Command(sql, new { userId, current = currentSessionId, limit }, ct))
            .ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task<int> DeleteExpiredAsync(TimeSpan retainEndedFor, CancellationToken ct)
    {
        // Only sessions that ENDED long ago. A live session is never touched, however old.
        const string sql = """
            DELETE FROM auth.session
            WHERE (revoked_at IS NOT NULL AND revoked_at < now() - @retain)
               OR (revoked_at IS NULL AND absolute_expires_at < now() - @retain)
            """;

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        return await connection.ExecuteAsync(_db.Command(sql, new { retain = retainEndedFor }, ct))
            .ConfigureAwait(false);
    }

    /// <summary>Base64url of 32 CSPRNG bytes.</summary>
    /// <remarks>
    /// Base64url rather than base64, so the value is safe in a cookie without escaping - a
    /// <c>+</c> or <c>/</c> in a cookie value is a source of the kind of bug that only appears
    /// for some users, some of the time.
    /// </remarks>
    private static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[TokenBytes];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Sha256(string token) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private sealed record SessionRow(
        Guid Id, DateTimeOffset IdleExpiresAt, DateTimeOffset AbsoluteExpiresAt);

    /// <remarks>
    /// A class with settable properties rather than a positional record, and the reason is
    /// Npgsql: it reports a <c>text[]</c> column's field type as <see cref="Array"/>, which
    /// matches no constructor parameter of type <c>string[]</c>. Dapper's constructor matching
    /// then fails with a message naming the whole signature. Its PROPERTY mapping path has no
    /// such problem, so every row type carrying an array is shaped for that path.
    /// </remarks>
    private sealed class ResolveRow
    {
        public Guid SessionId { get; set; }
        public long UserId { get; set; }
        public string Username { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public bool MustChangePassword { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
        public DateTimeOffset IdleExpiresAt { get; set; }
        public DateTimeOffset AbsoluteExpiresAt { get; set; }
        public string[] Permissions { get; set; } = [];
    }
}
