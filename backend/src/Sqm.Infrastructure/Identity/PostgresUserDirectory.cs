using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Sqm.Application.Identity;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Infrastructure.Identity;

/// <summary>User accounts, roles and permission overrides, in PostgreSQL.</summary>
/// <remarks>
/// <para>
/// Reads are here; writes are in the <c>.Writes.cs</c> partial. The split is by risk rather than
/// by size: every method in the other file opens a transaction, writes an audit entry and checks
/// the last-administrator guard, and keeping them together makes it obvious when one does not.
/// </para>
/// <para>
/// Every statement is parameterised. The only text assembled at runtime is a WHERE or ORDER BY
/// fragment whose wording is fixed at compile time and selected by an enum - never a column name
/// or a value that came from a request.
/// </para>
/// </remarks>
public sealed partial class PostgresUserDirectory : IUserDirectory
{
    [LoggerMessage(EventId = 4100, Level = LogLevel.Information,
        Message = "{Actor} {Action} user {UserId}")]
    private partial void LogUserChange(string actor, string action, long userId);

    private readonly IdentityDataSource _db;
    private readonly IPasswordHasher _hasher;
    private readonly PasswordPolicyOptions _policy;
    private readonly int _commandTimeout;
    private readonly ILogger<PostgresUserDirectory> _logger;

    /// <summary>Creates the directory.</summary>
    public PostgresUserDirectory(
        IdentityDataSource db,
        IPasswordHasher hasher,
        IOptions<AuthOptions> auth,
        IOptions<PostgresOptions> postgres,
        ILogger<PostgresUserDirectory> logger)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(postgres);

        _db = db;
        _hasher = hasher;
        _policy = auth.Value.Policy;
        _commandTimeout = postgres.Value.CommandTimeoutSeconds;
        _logger = logger;
    }

    // =======================================================================
    // Reads
    // =======================================================================

    /// <inheritdoc />
    public async Task<UserPage> SearchAsync(UserQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        // The sort is chosen from an enum, so the text below can never come from a request. NULLS
        // LAST on last_login matters: "never signed in" is the interesting end of that list and
        // PostgreSQL would otherwise sort NULLs first on a DESC order.
        var order = query.Sort switch
        {
            UserSort.LastLogin => "u.last_login_at DESC NULLS LAST, lower(u.display_name)",
            UserSort.Created => "u.created_at DESC",
            _ => "lower(u.display_name), lower(u.username)",
        };

        var sql = $"""
            -- ::int because count() is bigint and the model says int. The same mismatch
            -- produced two 500s on the Import Center once already.
            SELECT count(*) OVER()::int   AS Total,
                   u.id                   AS Id,
                   u.username             AS Username,
                   u.display_name         AS DisplayName,
                   u.email                AS Email,
                   u.job_title            AS JobTitle,
                   u.provider             AS Provider,
                   u.is_active            AS IsActive,
                   (u.locked_until IS NOT NULL AND u.locked_until > now()) AS IsLocked,
                   u.last_login_at        AS LastLoginAt,
                   u.created_at           AS CreatedAt
            FROM auth.user_account u
            WHERE (@search IS NULL
                   OR u.username ILIKE @pattern
                   OR u.display_name ILIKE @pattern
                   OR u.email ILIKE @pattern)
              AND (@roleCode IS NULL
                   OR EXISTS (SELECT 1
                              FROM auth.user_role ur
                              JOIN auth.role r ON r.id = ur.role_id
                              WHERE ur.user_id = u.id AND r.code = @roleCode))
              AND (@isActive::boolean IS NULL OR u.is_active = @isActive::boolean)
            ORDER BY {order}
            LIMIT @limit OFFSET @offset
            """;

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var rows = (await connection.QueryAsync<UserRow>(_db.Command(sql, new
        {
            search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search,
            pattern = LikePattern(query.Search),
            roleCode = query.RoleCode,
            isActive = query.IsActive,
            limit = pageSize,
            offset = (page - 1) * pageSize,
        }, ct)).ConfigureAwait(false)).AsList();

        if (rows.Count == 0)
        {
            return new UserPage([], 0, page, pageSize);
        }

        var roles = await LoadRolesAsync(connection, rows.Select(r => r.Id).ToArray(), ct)
            .ConfigureAwait(false);

        var items = rows.Select(r => new UserSummary(
            r.Id, r.Username, r.DisplayName, r.Email, r.JobTitle, r.Provider,
            r.IsActive, r.IsLocked, r.LastLoginAt, r.CreatedAt,
            roles.TryGetValue(r.Id, out var list) ? list : [])).ToList();

        return new UserPage(items, rows[0].Total, page, pageSize);
    }

    /// <inheritdoc />
    public Task<UserDetail?> GetAsync(long userId, CancellationToken ct) =>
        GetDetailAsync("u.id = @key", userId, ct);

    /// <inheritdoc />
    public Task<UserDetail?> GetByUsernameAsync(string username, CancellationToken ct) =>
        GetDetailAsync("lower(u.username) = lower(@key)", username, ct);

    private async Task<UserDetail?> GetDetailAsync(
        string predicate, object key, CancellationToken ct)
    {
        var sql = $"""
            SELECT u.id, u.username, u.display_name AS DisplayName, u.email, u.job_title AS JobTitle,
                   u.phone, u.provider, u.is_active AS IsActive,
                   u.must_change_password AS MustChangePassword,
                   u.last_login_at AS LastLoginAt, u.previous_login_at AS PreviousLoginAt,
                   host(u.last_login_ip) AS LastLoginIp,
                   u.failed_login_count AS FailedLoginCount, u.locked_until AS LockedUntil,
                   u.password_updated_at AS PasswordUpdatedAt,
                   u.deactivated_at AS DeactivatedAt, u.deactivated_by AS DeactivatedBy,
                   u.deactivation_reason AS DeactivationReason,
                   u.created_at AS CreatedAt, u.created_by AS CreatedBy,
                   u.updated_at AS UpdatedAt, u.updated_by AS UpdatedBy,
                   (SELECT count(*)::int FROM auth.session s
                    WHERE s.user_id = u.id AND s.revoked_at IS NULL
                      AND s.idle_expires_at > now() AND s.absolute_expires_at > now())
                        AS ActiveSessions
            FROM auth.user_account u
            WHERE {predicate}
            """;

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var row = await connection.QuerySingleOrDefaultAsync<DetailRow>(
            _db.Command(sql, new { key }, ct)).ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        var roles = await LoadRolesAsync(connection, [row.Id], ct).ConfigureAwait(false);
        var permissions = await LoadPermissionsAsync(connection, row.Id, ct).ConfigureAwait(false);

        return new UserDetail(
            row.Id, row.Username, row.DisplayName, row.Email, row.JobTitle, row.Phone,
            row.Provider, row.IsActive, row.MustChangePassword,
            row.LastLoginAt, row.PreviousLoginAt, row.LastLoginIp,
            row.FailedLoginCount, row.LockedUntil, row.PasswordUpdatedAt,
            row.DeactivatedAt, row.DeactivatedBy, row.DeactivationReason,
            row.CreatedAt, row.CreatedBy, row.UpdatedAt, row.UpdatedBy,
            roles.TryGetValue(row.Id, out var list) ? list : [],
            permissions,
            row.ActiveSessions);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PermissionResolution>> GetEffectivePermissionsAsync(
        long userId, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        return await LoadPermissionsAsync(connection, userId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> AdministrationIsReachableAsync(CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            await IdentitySql.EnsureAdministrationReachableAsync(
                connection, transaction, _commandTimeout, ct).ConfigureAwait(false);
            return true;
        }
        catch (LastAdministratorException)
        {
            return false;
        }
        finally
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
        }
    }

    // =======================================================================
    // Shared loaders
    // =======================================================================

    private async Task<Dictionary<long, List<RoleRef>>> LoadRolesAsync(
        NpgsqlConnection connection, long[] userIds, CancellationToken ct)
    {
        const string sql = """
            SELECT ur.user_id AS UserId, r.id AS Id, r.code AS Code, r.display_name AS DisplayName
            FROM auth.user_role ur
            JOIN auth.role r ON r.id = ur.role_id
            WHERE ur.user_id = ANY(@userIds)
            ORDER BY r.display_name
            """;

        var rows = await connection.QueryAsync<UserRoleRow>(
            _db.Command(sql, new { userIds }, ct)).ConfigureAwait(false);

        var map = new Dictionary<long, List<RoleRef>>();
        foreach (var row in rows)
        {
            if (!map.TryGetValue(row.UserId, out var list))
            {
                list = [];
                map[row.UserId] = list;
            }

            list.Add(new RoleRef(row.Id, row.Code, row.DisplayName));
        }

        return map;
    }

    /// <summary>
    /// Every permission in the catalogue, with the full account of why this user has it or not.
    /// </summary>
    /// <remarks>
    /// Returns all 17 rows, not just the granted ones. The user detail page has to show what
    /// someone does NOT have as clearly as what they do - a screen listing only granted
    /// permissions cannot answer "should she be able to activate a TAC version?", which is the
    /// question an access review actually asks.
    /// </remarks>
    private async Task<List<PermissionResolution>> LoadPermissionsAsync(
        NpgsqlConnection connection, long userId, CancellationToken ct)
    {
        const string sql = """
            SELECT p.code            AS Code,
                   p.category        AS Category,
                   p.display_name    AS DisplayName,
                   p.description     AS Description,
                   p.is_dangerous    AS IsDangerous,
                   -- No COALESCE: ARRAY(subquery) yields an empty array, never NULL, and
                   -- COALESCE with an untyped '{}' literal made PostgreSQL report the column as
                   -- an untyped array - which reaches Dapper as System.Array and matches no
                   -- constructor parameter.
                   ARRAY(SELECT r.display_name
                         FROM auth.user_role ur
                         JOIN auth.role r ON r.id = ur.role_id
                         JOIN auth.role_permission rp
                              ON rp.role_id = r.id AND rp.permission_code = p.code
                         WHERE ur.user_id = @userId
                         ORDER BY r.display_name)  AS GrantedByRoles,
                   -- COALESCE, not a bare comparison. The LEFT JOIN leaves `up.effect` NULL
                   -- for every permission with no override, and `NULL = 'grant'` is NULL, not
                   -- false - which arrives as a null boolean and fails to materialise into the
                   -- record with an error that names the constructor, not the column.
                   COALESCE(up.effect = 'grant', false) AS GrantedDirectly,
                   COALESCE(up.effect = 'deny', false)  AS DeniedDirectly,
                   up.reason         AS OverrideReason
            FROM auth.permission p
            LEFT JOIN auth.user_permission up
                   ON up.user_id = @userId AND up.permission_code = p.code
            ORDER BY p.category, p.sort_order, p.code
            """;

        var rows = await connection.QueryAsync<PermissionRow>(
            _db.Command(sql, new { userId }, ct)).ConfigureAwait(false);

        // The effective rule, applied once here and once in SQL for the request pipeline. Both
        // are exercised by the same integration test, which asserts they agree for a user
        // holding a role, a direct grant and a direct deny at the same time.
        return rows.Select(r => new PermissionResolution(
            r.Code, r.Category, r.DisplayName, r.Description, r.IsDangerous,
            IsGranted: !r.DeniedDirectly && (r.GrantedDirectly || r.GrantedByRoles.Length > 0),
            GrantedByRoles: r.GrantedByRoles,
            GrantedDirectly: r.GrantedDirectly,
            DeniedDirectly: r.DeniedDirectly,
            OverrideReason: r.OverrideReason)).ToList();
    }

    /// <summary>Escapes a search term so <c>%</c> and <c>_</c> are literal characters.</summary>
    /// <remarks>
    /// Without this, searching for <c>_</c> matches every user and searching for <c>%</c> matches
    /// every user - not a security hole, since the value is still bound, but a search box that
    /// silently ignores what was typed.
    /// </remarks>
    private static string? LikePattern(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return null;
        }

        var escaped = search.Trim()
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

        return $"%{escaped}%";
    }

    private sealed record UserRow(
        int Total, long Id, string Username, string DisplayName, string? Email, string? JobTitle,
        string Provider, bool IsActive, bool IsLocked, DateTimeOffset? LastLoginAt,
        DateTimeOffset CreatedAt);

    private sealed record UserRoleRow(long UserId, long Id, string Code, string DisplayName);

    /// <remarks>
    /// A class with settable properties rather than a positional record, and the reason is
    /// Npgsql: it reports a <c>text[]</c> column's field type as <see cref="Array"/>, which
    /// matches no constructor parameter of type <c>string[]</c>. Dapper's constructor matching
    /// then fails with a message naming the whole signature. Its PROPERTY mapping path has no
    /// such problem, so every row type carrying an array is shaped for that path.
    /// </remarks>
    private sealed class PermissionRow
    {
        public string Code { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool IsDangerous { get; set; }
        public string[] GrantedByRoles { get; set; } = [];
        public bool GrantedDirectly { get; set; }
        public bool DeniedDirectly { get; set; }
        public string? OverrideReason { get; set; }
    }

    private sealed record DetailRow(
        long Id, string Username, string DisplayName, string? Email, string? JobTitle,
        string? Phone, string Provider, bool IsActive, bool MustChangePassword,
        DateTimeOffset? LastLoginAt, DateTimeOffset? PreviousLoginAt, string? LastLoginIp,
        short FailedLoginCount, DateTimeOffset? LockedUntil, DateTimeOffset? PasswordUpdatedAt,
        DateTimeOffset? DeactivatedAt, string? DeactivatedBy, string? DeactivationReason,
        DateTimeOffset CreatedAt, string? CreatedBy, DateTimeOffset UpdatedAt, string? UpdatedBy,
        int ActiveSessions);
}
