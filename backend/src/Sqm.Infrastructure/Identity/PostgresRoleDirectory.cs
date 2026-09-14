using System.Data;
using System.Globalization;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Sqm.Application.Identity;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Infrastructure.Identity;

/// <summary>Roles and the permission catalogue, in PostgreSQL.</summary>
public sealed partial class PostgresRoleDirectory : IRoleDirectory
{
    [LoggerMessage(EventId = 4200, Level = LogLevel.Information,
        Message = "{Actor} {Action} role {RoleId}")]
    private partial void LogRoleChange(string actor, string action, long roleId);

    private readonly IdentityDataSource _db;
    private readonly int _commandTimeout;
    private readonly ILogger<PostgresRoleDirectory> _logger;

    /// <summary>Creates the directory.</summary>
    public PostgresRoleDirectory(
        IdentityDataSource db,
        IOptions<PostgresOptions> postgres,
        ILogger<PostgresRoleDirectory> logger)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        _db = db;
        _commandTimeout = postgres.Value.CommandTimeoutSeconds;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PermissionDefinition>> GetPermissionCatalogueAsync(
        CancellationToken ct)
    {
        const string sql = """
            SELECT code         AS Code,
                   category     AS Category,
                   display_name AS DisplayName,
                   description  AS Description,
                   is_dangerous AS IsDangerous,
                   sort_order::int AS SortOrder
            FROM auth.permission
            ORDER BY category, sort_order, code
            """;

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var rows = await connection.QueryAsync<PermissionDefinition>(_db.Command(sql, null, ct))
            .ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RoleDetail>> GetRolesAsync(CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        return await LoadRolesAsync(connection, null, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RoleDetail?> GetRoleAsync(long roleId, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var roles = await LoadRolesAsync(connection, roleId, ct).ConfigureAwait(false);
        return roles.Count == 0 ? null : roles[0];
    }

    private async Task<List<RoleDetail>> LoadRolesAsync(
        NpgsqlConnection connection, long? roleId, CancellationToken ct)
    {
        // Member count and permission codes come back with the role. Three separate queries
        // would mean the roles page fires one plus two-per-role, which is the classic N+1 and
        // shows up as a page that feels fine with four roles and awful with forty.
        const string sql = """
            SELECT r.id            AS Id,
                   r.code          AS Code,
                   r.display_name  AS DisplayName,
                   r.description   AS Description,
                   r.is_system     AS IsSystem,
                   (SELECT count(*)::int FROM auth.user_role ur WHERE ur.role_id = r.id)
                                   AS MemberCount,
                   ARRAY(SELECT rp.permission_code
                         FROM auth.role_permission rp
                         WHERE rp.role_id = r.id
                         ORDER BY rp.permission_code) AS PermissionCodes,
                   r.created_at    AS CreatedAt,
                   r.created_by    AS CreatedBy,
                   r.updated_at    AS UpdatedAt,
                   r.updated_by    AS UpdatedBy
            FROM auth.role r
            WHERE (@roleId::bigint IS NULL OR r.id = @roleId::bigint)
            ORDER BY r.is_system DESC, lower(r.display_name)
            """;

        var rows = await connection.QueryAsync<RoleRow>(_db.Command(sql, new { roleId }, ct))
            .ConfigureAwait(false);

        return rows.Select(r => new RoleDetail(
            r.Id, r.Code, r.DisplayName, r.Description, r.IsSystem, r.MemberCount,
            r.PermissionCodes, r.CreatedAt, r.CreatedBy, r.UpdatedAt, r.UpdatedBy)).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserSummary>> GetMembersAsync(long roleId, CancellationToken ct)
    {
        const string sql = """
            SELECT u.id            AS Id,
                   u.username      AS Username,
                   u.display_name  AS DisplayName,
                   u.email         AS Email,
                   u.job_title     AS JobTitle,
                   u.provider      AS Provider,
                   u.is_active     AS IsActive,
                   (u.locked_until IS NOT NULL AND u.locked_until > now()) AS IsLocked,
                   u.last_login_at AS LastLoginAt,
                   u.created_at    AS CreatedAt
            FROM auth.user_role ur
            JOIN auth.user_account u ON u.id = ur.user_id
            WHERE ur.role_id = @roleId
            ORDER BY u.is_active DESC, lower(u.display_name)
            """;

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var rows = await connection.QueryAsync<MemberRow>(_db.Command(sql, new { roleId }, ct))
            .ConfigureAwait(false);

        return rows.Select(r => new UserSummary(
            r.Id, r.Username, r.DisplayName, r.Email, r.JobTitle, r.Provider,
            r.IsActive, r.IsLocked, r.LastLoginAt, r.CreatedAt, [])).ToList();
    }

    /// <inheritdoc />
    public async Task<long> CreateRoleAsync(
        string code, string displayName, string description, IReadOnlyList<string> permissionCodes,
        string actor, AuthenticationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(permissionCodes);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var normalised = NormaliseCode(code);

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        long roleId;
        try
        {
            roleId = await connection.ExecuteScalarAsync<long>(_db.Command("""
                INSERT INTO auth.role (code, display_name, description, is_system, created_by, updated_by)
                VALUES (@code, @displayName, @description, false, @actor, @actor)
                RETURNING id
                """, new
            {
                code = normalised,
                displayName = displayName.Trim(),
                description = description?.Trim() ?? string.Empty,
                actor,
            }, ct, transaction)).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new RoleChangeRefusedException($"A role with the code '{normalised}' already exists.", ex);
        }

        await ReplacePermissionsAsync(connection, transaction, roleId, permissionCodes, actor, ct)
            .ConfigureAwait(false);

        await AuditAsync(connection, transaction, actor, "role.create", roleId, normalised,
            context, new Dictionary<string, object?> { ["permissions"] = permissionCodes }, ct)
            .ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        LogRoleChange(actor, "created", roleId);
        return roleId;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateRoleAsync(
        long roleId, string displayName, string description, string actor,
        AuthenticationContext context, CancellationToken ct)
    {
        // The code is deliberately absent from the SET list. It is what audit entries, deployment
        // notes and any future directory-group mapping refer to; renaming it makes every one of
        // those silently wrong rather than loudly broken.
        const string sql = """
            UPDATE auth.role
            SET display_name = @displayName, description = @description,
                updated_at = now(), updated_by = @actor
            WHERE id = @roleId
            RETURNING code
            """;

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var code = await connection.ExecuteScalarAsync<string?>(_db.Command(sql, new
        {
            roleId,
            displayName = displayName?.Trim(),
            description = description?.Trim() ?? string.Empty,
            actor,
        }, ct, transaction)).ConfigureAwait(false);

        if (code is null)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }

        await AuditAsync(connection, transaction, actor, "role.update", roleId, code, context,
            null, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        LogRoleChange(actor, "renamed", roleId);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> SetRolePermissionsAsync(
        long roleId, IReadOnlyList<string> permissionCodes, string actor,
        AuthenticationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(permissionCodes);

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var code = await connection.ExecuteScalarAsync<string?>(_db.Command(
            "SELECT code FROM auth.role WHERE id = @roleId FOR UPDATE",
            new { roleId }, ct, transaction)).ConfigureAwait(false);

        if (code is null)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }

        var before = await ReadPermissionCodesAsync(connection, transaction, roleId, ct)
            .ConfigureAwait(false);

        await ReplacePermissionsAsync(connection, transaction, roleId, permissionCodes, actor, ct)
            .ConfigureAwait(false);

        var after = await ReadPermissionCodesAsync(connection, transaction, roleId, ct)
            .ConfigureAwait(false);

        await AuditAsync(connection, transaction, actor, "role.permissions.set", roleId, code,
            context, new Dictionary<string, object?>
            {
                ["before"] = before,
                ["after"] = after,
                ["added"] = after.Except(before, StringComparer.Ordinal).ToArray(),
                ["removed"] = before.Except(after, StringComparer.Ordinal).ToArray(),
            }, ct).ConfigureAwait(false);

        // This is the mis-click that matters: unchecking user.manage on the Administrator role
        // when no other active account holds it leaves a system whose only recovery is a database
        // session.
        await IdentitySql.EnsureAdministrationReachableAsync(
            connection, transaction, _commandTimeout, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        LogRoleChange(actor, "changed the permissions of", roleId);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteRoleAsync(
        long roleId, string actor, AuthenticationContext context, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var row = await connection.QuerySingleOrDefaultAsync<DeleteRow>(_db.Command("""
            SELECT r.code AS Code, r.is_system AS IsSystem,
                   (SELECT count(*)::int FROM auth.user_role ur WHERE ur.role_id = r.id) AS Members
            FROM auth.role r WHERE r.id = @roleId FOR UPDATE
            """, new { roleId }, ct, transaction)).ConfigureAwait(false);

        if (row is null)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }

        // Both refusals say what is wrong rather than returning false, because "nothing happened"
        // and "that is not allowed" look identical on a screen and only one of them is actionable.
        if (row.IsSystem)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            throw new RoleChangeRefusedException(
                $"'{row.Code}' is a built-in role and cannot be deleted. Its permissions can "
                + "still be changed.");
        }

        if (row.Members > 0)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            throw new RoleChangeRefusedException(
                $"{row.Members} user(s) still hold this role. Remove it from them first.");
        }

        await connection.ExecuteAsync(_db.Command(
            "DELETE FROM auth.role WHERE id = @roleId", new { roleId }, ct, transaction))
            .ConfigureAwait(false);

        await AuditAsync(connection, transaction, actor, "role.delete", roleId, row.Code, context,
            null, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        LogRoleChange(actor, "deleted", roleId);
        return true;
    }

    // =======================================================================
    // Helpers
    // =======================================================================

    private async Task ReplacePermissionsAsync(
        NpgsqlConnection connection, IDbTransaction transaction, long roleId,
        IReadOnlyList<string> permissionCodes, string actor, CancellationToken ct)
    {
        await connection.ExecuteAsync(_db.Command(
            "DELETE FROM auth.role_permission WHERE role_id = @roleId",
            new { roleId }, ct, transaction)).ConfigureAwait(false);

        if (permissionCodes.Count == 0)
        {
            return;
        }

        // Selecting from auth.permission rather than inserting the supplied codes directly means
        // an unknown code is dropped rather than rejected, so the count is compared afterwards.
        var inserted = await connection.ExecuteAsync(_db.Command("""
            INSERT INTO auth.role_permission (role_id, permission_code, granted_by)
            SELECT @roleId, p.code, @actor
            FROM auth.permission p
            WHERE p.code = ANY(@codes)
            """, new { roleId, codes = permissionCodes.ToArray(), actor }, ct, transaction))
            .ConfigureAwait(false);

        if (inserted != permissionCodes.Distinct(StringComparer.Ordinal).Count())
        {
            throw new RoleChangeRefusedException(
                "One or more of those permission codes does not exist.");
        }
    }

    private async Task<string[]> ReadPermissionCodesAsync(
        NpgsqlConnection connection, IDbTransaction transaction, long roleId, CancellationToken ct)
    {
        var rows = await connection.QueryAsync<string>(_db.Command(
            "SELECT permission_code FROM auth.role_permission WHERE role_id = @roleId "
            + "ORDER BY permission_code", new { roleId }, ct, transaction)).ConfigureAwait(false);

        return [.. rows];
    }

    /// <summary>Lower-cased, with spaces and hyphens folded to underscores.</summary>
    /// <remarks>
    /// So "Senior Analyst" typed into the form becomes <c>senior_analyst</c> rather than a code
    /// with a space in it that every later URL and audit entry has to escape.
    /// </remarks>
    private static string NormaliseCode(string code) =>
        code.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');

    private Task AuditAsync(
        NpgsqlConnection connection,
        IDbTransaction transaction,
        string actor,
        string action,
        long roleId,
        string roleCode,
        AuthenticationContext context,
        IReadOnlyDictionary<string, object?>? detail,
        CancellationToken ct) =>
        IdentitySql.WriteAuditAsync(connection, transaction, new AuditEntry(
            ActorName: actor,
            Action: action,
            Category: AuditCategory.Role,
            Outcome: AuditOutcome.Success,
            TargetType: "role",
            TargetId: roleId.ToString(CultureInfo.InvariantCulture),
            TargetName: roleCode,
            Ip: context?.Ip,
            UserAgent: context?.UserAgent,
            CorrelationId: context?.CorrelationId,
            Detail: detail), _commandTimeout, ct);

    /// <remarks>
    /// A class with settable properties rather than a positional record, and the reason is
    /// Npgsql: it reports a <c>text[]</c> column's field type as <see cref="Array"/>, which
    /// matches no constructor parameter of type <c>string[]</c>. Dapper's constructor matching
    /// then fails with a message naming the whole signature. Its PROPERTY mapping path has no
    /// such problem, so every row type carrying an array is shaped for that path.
    /// </remarks>
    private sealed class RoleRow
    {
        public long Id { get; set; }
        public string Code { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool IsSystem { get; set; }
        public int MemberCount { get; set; }
        public string[] PermissionCodes { get; set; } = [];
        public DateTimeOffset CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public string? UpdatedBy { get; set; }
    }

    private sealed record MemberRow(
        long Id, string Username, string DisplayName, string? Email, string? JobTitle,
        string Provider, bool IsActive, bool IsLocked, DateTimeOffset? LastLoginAt,
        DateTimeOffset CreatedAt);

    private sealed record DeleteRow(string Code, bool IsSystem, int Members);
}
