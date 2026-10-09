using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Sqm.Application.Dashboards;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Infrastructure.Identity;

/// <summary>Dashboards, their grants and each person's preferences, in PostgreSQL (operational migration 016).</summary>
/// <remarks>
/// <para>
/// <b>Access is not decided here.</b> Every read answers who owns a dashboard and which grant the
/// reading person holds - directly or through a role they hold at the moment of the read - and
/// <see cref="DashboardAccess"/> decides what that allows. Because roles are joined fresh on every
/// read, a removed role, a revoked grant or a deactivated account takes effect on the next request.
/// </para>
/// <para>
/// Every statement is parameterised and scoped by id; the caller has checked access before a write.
/// </para>
/// </remarks>
public sealed class PostgresDashboardStore : IDashboardStore
{
    private const string UniqueViolation = "23505";

    private readonly IdentityDataSource _db;
    private readonly int _commandTimeout;

    /// <summary>Creates the store over the operational database.</summary>
    public PostgresDashboardStore(IdentityDataSource db, IOptions<PostgresOptions> postgres)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        _db = db;
        _commandTimeout = postgres.Value.CommandTimeoutSeconds;
    }

    /// <summary>
    /// A dashboard as the reader sees it. <c>@reader</c> is the person reading; their grant is the
    /// strongest of a direct grant and grants to any role they hold now.
    /// </summary>
    private const string Select = """
        SELECT d.id AS Id, d.owner_user_id AS OwnerUserId, o.display_name AS OwnerDisplayName,
               d.name AS Name, d.description AS Description, d.definition::text AS DefinitionJson,
               d.schema_version AS SchemaVersion, d.revision AS Revision, d.source_template AS SourceTemplate,
               d.created_at AS CreatedAt, d.updated_at AS UpdatedAt, ub.display_name AS UpdatedByDisplayName,
               (SELECT CASE WHEN bool_or(g.level = 'editor') THEN 'editor'
                            WHEN count(*) > 0 THEN 'viewer' END
                  FROM dashboard.dashboard_grant g
                 WHERE g.dashboard_id = d.id
                   AND (g.user_id = @reader
                        OR g.role_id IN (SELECT ur.role_id FROM auth.user_role ur WHERE ur.user_id = @reader))) AS GrantLevel,
               EXISTS (SELECT 1 FROM dashboard.favorite f WHERE f.user_id = @reader AND f.dashboard_id = d.id) AS IsFavorite,
               EXISTS (SELECT 1 FROM dashboard.user_default ud WHERE ud.user_id = @reader AND ud.dashboard_id = d.id) AS IsDefault,
               (SELECT count(*) FROM dashboard.dashboard_grant g2 WHERE g2.dashboard_id = d.id)::int AS GrantCount,
               CASE WHEN jsonb_typeof(d.definition -> 'widgets') = 'array'
                    THEN jsonb_array_length(d.definition -> 'widgets') ELSE 0 END AS WidgetCount
        FROM dashboard.dashboard d
        JOIN auth.user_account o ON o.id = d.owner_user_id
        LEFT JOIN auth.user_account ub ON ub.id = d.updated_by_user_id
        """;

    /// <summary>Whether the reader holds any grant on <c>d</c>, directly or through a role.</summary>
    private const string SharedWithReader = """
        EXISTS (SELECT 1 FROM dashboard.dashboard_grant g
                 WHERE g.dashboard_id = d.id
                   AND (g.user_id = @reader
                        OR g.role_id IN (SELECT ur.role_id FROM auth.user_role ur WHERE ur.user_id = @reader)))
        """;

    private const string GrantSelect = """
        SELECT g.id AS Id, g.dashboard_id AS DashboardId,
               CASE WHEN g.user_id IS NOT NULL THEN 'user' ELSE 'role' END AS SubjectType,
               COALESCE(g.user_id, g.role_id) AS SubjectId,
               COALESCE(u.display_name, r.display_name) AS SubjectName,
               u.username AS SubjectDetail,
               g.level AS Level, g.granted_at AS GrantedAt, gb.display_name AS GrantedByDisplayName
        FROM dashboard.dashboard_grant g
        LEFT JOIN auth.user_account u ON u.id = g.user_id
        LEFT JOIN auth.role r ON r.id = g.role_id
        LEFT JOIN auth.user_account gb ON gb.id = g.granted_by_user_id
        """;

    /// <inheritdoc />
    public Task<IReadOnlyList<DashboardRecord>> ListOwnedAsync(long userId, CancellationToken ct) =>
        ListAsync($"{Select} WHERE d.owner_user_id = @reader ORDER BY d.updated_at DESC, d.id DESC", userId, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<DashboardRecord>> ListSharedWithAsync(long userId, CancellationToken ct) =>
        ListAsync($"{Select} WHERE d.owner_user_id <> @reader AND {SharedWithReader} ORDER BY d.updated_at DESC, d.id DESC", userId, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<DashboardRecord>> ListOthersAsync(long userId, CancellationToken ct) =>
        ListAsync($"{Select} WHERE d.owner_user_id <> @reader AND NOT {SharedWithReader} ORDER BY lower(o.display_name), lower(d.name), d.id", userId, ct);

    private async Task<IReadOnlyList<DashboardRecord>> ListAsync(string sql, long reader, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var rows = await connection.QueryAsync<Row>(Command(sql, new { reader }, ct)).ConfigureAwait(false);
        return [.. rows.Select(Map)];
    }

    /// <inheritdoc />
    public async Task<DashboardRecord?> GetAsync(long id, long readerUserId, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        return await GetAsync(connection, id, readerUserId, ct).ConfigureAwait(false);
    }

    private async Task<DashboardRecord?> GetAsync(NpgsqlConnection connection, long id, long reader, CancellationToken ct)
    {
        var row = await connection.QuerySingleOrDefaultAsync<Row>(
            Command($"{Select} WHERE d.id = @id", new { id, reader }, ct)).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    /// <inheritdoc />
    public async Task<(DashboardWriteOutcome Outcome, DashboardRecord? Dashboard)> CreateAsync(NewDashboard dashboard, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dashboard);

        try
        {
            await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
            var id = await connection.ExecuteScalarAsync<long>(Command(
                """
                INSERT INTO dashboard.dashboard
                    (owner_user_id, name, description, definition, schema_version, source_template, updated_by_user_id)
                VALUES (@owner, @name, @description, @definition::jsonb, @schemaVersion, @sourceTemplate, @owner)
                RETURNING id
                """,
                new
                {
                    owner = dashboard.OwnerUserId,
                    name = dashboard.Name.Trim(),
                    description = dashboard.Description,
                    definition = dashboard.DefinitionJson,
                    schemaVersion = dashboard.SchemaVersion,
                    sourceTemplate = dashboard.SourceTemplate,
                }, ct)).ConfigureAwait(false);

            return (DashboardWriteOutcome.Saved, await GetAsync(connection, id, dashboard.OwnerUserId, ct).ConfigureAwait(false));
        }
        catch (PostgresException ex) when (ex.SqlState == UniqueViolation)
        {
            return (DashboardWriteOutcome.NameTaken, null);
        }
    }

    /// <inheritdoc />
    public async Task<(DashboardWriteOutcome Outcome, DashboardRecord? Dashboard, DashboardConflict? Conflict)> UpdateAsync(
        long id, long actorUserId, string name, string description, string definitionJson, int schemaVersion,
        int expectedRevision, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);

        try
        {
            await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

            // One guarded statement: the check and the write cannot be separated by another save.
            var updated = await connection.ExecuteScalarAsync<long?>(Command(
                """
                UPDATE dashboard.dashboard
                SET name = @name, description = @description, definition = @definition::jsonb,
                    schema_version = @schemaVersion, revision = revision + 1, updated_at = now(),
                    updated_by_user_id = @actor
                WHERE id = @id AND revision = @expected
                RETURNING id
                """,
                new
                {
                    id, name = name.Trim(), description, definition = definitionJson, schemaVersion,
                    actor = actorUserId, expected = expectedRevision,
                }, ct)).ConfigureAwait(false);

            if (updated is not null)
            {
                return (DashboardWriteOutcome.Saved, await GetAsync(connection, id, actorUserId, ct).ConfigureAwait(false), null);
            }

            var current = await connection.QuerySingleOrDefaultAsync<ConflictRow>(Command(
                """
                SELECT d.revision AS Revision, d.updated_at AS UpdatedAt, u.display_name AS UpdatedBy
                FROM dashboard.dashboard d
                LEFT JOIN auth.user_account u ON u.id = d.updated_by_user_id
                WHERE d.id = @id
                """, new { id }, ct)).ConfigureAwait(false);

            return current is null
                ? (DashboardWriteOutcome.NotFound, null, null)
                : (DashboardWriteOutcome.Conflict, null, new DashboardConflict(current.Revision, current.UpdatedAt, current.UpdatedBy));
        }
        catch (PostgresException ex) when (ex.SqlState == UniqueViolation)
        {
            return (DashboardWriteOutcome.NameTaken, null, null);
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(long id, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        return await connection.ExecuteAsync(Command(
            "DELETE FROM dashboard.dashboard WHERE id = @id", new { id }, ct)).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public async Task<bool> NameExistsAsync(long ownerUserId, string name, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<bool>(Command(
            """
            SELECT EXISTS (SELECT 1 FROM dashboard.dashboard
                           WHERE owner_user_id = @owner AND lower(btrim(name)) = lower(btrim(@name)))
            """, new { owner = ownerUserId, name }, ct)).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ grants

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardGrantRecord>> ListGrantsAsync(long dashboardId, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var rows = await connection.QueryAsync<GrantRow>(Command(
            $"""
            {GrantSelect}
            WHERE g.dashboard_id = @dashboardId
            ORDER BY (g.user_id IS NULL), lower(COALESCE(u.display_name, r.display_name)), g.id
            """, new { dashboardId }, ct)).ConfigureAwait(false);
        return [.. rows.Select(Map)];
    }

    /// <inheritdoc />
    public async Task<(GrantWriteOutcome Outcome, DashboardGrantRecord? Grant)> AddGrantAsync(
        long dashboardId, string subjectType, long subjectId, string level, long actorUserId, int maxGrants, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Locks the dashboard so two simultaneous grants cannot both pass the limit.
        var owner = await connection.ExecuteScalarAsync<long?>(Command(
            "SELECT owner_user_id FROM dashboard.dashboard WHERE id = @dashboardId FOR UPDATE",
            new { dashboardId }, ct, transaction)).ConfigureAwait(false);
        if (owner is null)
        {
            return (GrantWriteOutcome.NotFound, null);
        }

        var isUser = subjectType == "user";
        if (isUser && subjectId == owner)
        {
            return (GrantWriteOutcome.SubjectIsOwner, null);
        }

        var exists = await connection.ExecuteScalarAsync<bool>(Command(
            isUser
                ? "SELECT EXISTS (SELECT 1 FROM auth.user_account WHERE id = @subjectId AND is_active)"
                : "SELECT EXISTS (SELECT 1 FROM auth.role WHERE id = @subjectId)",
            new { subjectId }, ct, transaction)).ConfigureAwait(false);
        if (!exists)
        {
            return (GrantWriteOutcome.SubjectNotFound, null);
        }

        var count = await connection.ExecuteScalarAsync<int>(Command(
            "SELECT count(*)::int FROM dashboard.dashboard_grant WHERE dashboard_id = @dashboardId",
            new { dashboardId }, ct, transaction)).ConfigureAwait(false);
        if (count >= maxGrants)
        {
            return (GrantWriteOutcome.LimitReached, null);
        }

        long grantId;
        try
        {
            grantId = await connection.ExecuteScalarAsync<long>(Command(
                """
                INSERT INTO dashboard.dashboard_grant (dashboard_id, user_id, role_id, level, granted_by_user_id)
                VALUES (@dashboardId, @userId, @roleId, @level, @actor)
                RETURNING id
                """,
                new
                {
                    dashboardId,
                    userId = isUser ? subjectId : (long?)null,
                    roleId = isUser ? (long?)null : subjectId,
                    level,
                    actor = actorUserId,
                }, ct, transaction)).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == UniqueViolation)
        {
            return (GrantWriteOutcome.Duplicate, null);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return (GrantWriteOutcome.Saved, await GrantAsync(connection, dashboardId, grantId, ct).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<(GrantWriteOutcome Outcome, DashboardGrantRecord? Grant)> UpdateGrantAsync(
        long dashboardId, long grantId, string level, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var updated = await connection.ExecuteAsync(Command(
            "UPDATE dashboard.dashboard_grant SET level = @level WHERE dashboard_id = @dashboardId AND id = @grantId",
            new { dashboardId, grantId, level }, ct)).ConfigureAwait(false);

        return updated == 0
            ? (GrantWriteOutcome.NotFound, null)
            : (GrantWriteOutcome.Saved, await GrantAsync(connection, dashboardId, grantId, ct).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<DashboardGrantRecord?> RemoveGrantAsync(long dashboardId, long grantId, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var grant = await GrantAsync(connection, dashboardId, grantId, ct).ConfigureAwait(false);
        if (grant is null)
        {
            return null;
        }

        var removed = await connection.ExecuteAsync(Command(
            "DELETE FROM dashboard.dashboard_grant WHERE dashboard_id = @dashboardId AND id = @grantId",
            new { dashboardId, grantId }, ct)).ConfigureAwait(false);
        return removed > 0 ? grant : null;
    }

    private async Task<DashboardGrantRecord?> GrantAsync(NpgsqlConnection connection, long dashboardId, long grantId, CancellationToken ct)
    {
        var row = await connection.QuerySingleOrDefaultAsync<GrantRow>(Command(
            $"{GrantSelect} WHERE g.dashboard_id = @dashboardId AND g.id = @grantId",
            new { dashboardId, grantId }, ct)).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    // ------------------------------------------------------------- preferences

    /// <inheritdoc />
    public async Task SetFavoriteAsync(long userId, long dashboardId, bool favorite, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(Command(
            favorite
                ? """
                  INSERT INTO dashboard.favorite (user_id, dashboard_id) VALUES (@userId, @dashboardId)
                  ON CONFLICT (user_id, dashboard_id) DO NOTHING
                  """
                : "DELETE FROM dashboard.favorite WHERE user_id = @userId AND dashboard_id = @dashboardId",
            new { userId, dashboardId }, ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DashboardDefaultPreference> GetDefaultAsync(long userId, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<DefaultRow>(Command(
            "SELECT dashboard_id AS DashboardId, template_key AS TemplateKey FROM dashboard.user_default WHERE user_id = @userId",
            new { userId }, ct)).ConfigureAwait(false);
        return row is null ? new DashboardDefaultPreference(null, null) : new DashboardDefaultPreference(row.DashboardId, row.TemplateKey);
    }

    /// <inheritdoc />
    public async Task SetDefaultAsync(long userId, long? dashboardId, string? templateKey, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(Command(
            dashboardId is null && templateKey is null
                ? "DELETE FROM dashboard.user_default WHERE user_id = @userId"
                : """
                  INSERT INTO dashboard.user_default (user_id, dashboard_id, template_key)
                  VALUES (@userId, @dashboardId, @templateKey)
                  ON CONFLICT (user_id) DO UPDATE
                  SET dashboard_id = EXCLUDED.dashboard_id, template_key = EXCLUDED.template_key, updated_at = now()
                  """,
            new { userId, dashboardId, templateKey }, ct)).ConfigureAwait(false);
    }

    // --------------------------------------------------------------- directory

    /// <inheritdoc />
    public async Task<(IReadOnlyList<ShareDirectoryEntry> Users, IReadOnlyList<ShareDirectoryEntry> Roles)> SearchShareDirectoryAsync(
        string term, int limit, int minUserQuery, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(term);

        var trimmed = term.Trim();
        var pattern = $"%{Escape(trimmed)}%";

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        // Only what a picker needs: a name and, for people, the username that tells two of the same
        // name apart. Never email, never login data.
        IReadOnlyList<ShareDirectoryEntry> users = [];
        if (trimmed.Length >= minUserQuery)
        {
            var userRows = await connection.QueryAsync<DirectoryRow>(Command(
                """
                SELECT id AS Id, display_name AS Name, username AS Detail
                FROM auth.user_account
                WHERE is_active
                  AND (display_name ILIKE @pattern ESCAPE '\' OR username ILIKE @pattern ESCAPE '\')
                ORDER BY lower(display_name), id
                LIMIT @limit
                """, new { pattern, limit }, ct)).ConfigureAwait(false);
            users = [.. userRows.Select(r => new ShareDirectoryEntry("user", r.Id, r.Name, r.Detail))];
        }

        var roleRows = await connection.QueryAsync<DirectoryRow>(Command(
            """
            SELECT id AS Id, display_name AS Name, NULL::text AS Detail
            FROM auth.role
            WHERE display_name ILIKE @pattern ESCAPE '\' OR code ILIKE @pattern ESCAPE '\'
            ORDER BY lower(display_name), id
            LIMIT @limit
            """, new { pattern, limit }, ct)).ConfigureAwait(false);

        return (users, [.. roleRows.Select(r => new ShareDirectoryEntry("role", r.Id, r.Name, null))]);
    }

    /// <summary>Makes a search term literal for ILIKE: a typed % or _ is not a wildcard.</summary>
    private static string Escape(string term) => term
        .Replace(@"\", @"\\", StringComparison.Ordinal)
        .Replace("%", @"\%", StringComparison.Ordinal)
        .Replace("_", @"\_", StringComparison.Ordinal);

    private CommandDefinition Command(string sql, object parameters, CancellationToken ct, NpgsqlTransaction? transaction = null) =>
        new(sql, parameters, transaction, _commandTimeout, cancellationToken: ct);

    private static DashboardRecord Map(Row r) => new(
        r.Id, r.OwnerUserId, r.OwnerDisplayName, r.Name, r.Description, r.DefinitionJson, r.SchemaVersion, r.Revision,
        r.SourceTemplate, r.CreatedAt, r.UpdatedAt, r.UpdatedByDisplayName, r.GrantLevel, r.IsFavorite, r.IsDefault,
        r.GrantCount, r.WidgetCount);

    private static DashboardGrantRecord Map(GrantRow r) => new(
        r.Id, r.DashboardId, r.SubjectType, r.SubjectId, r.SubjectName, r.SubjectDetail, r.Level, r.GrantedAt, r.GrantedByDisplayName);

    private sealed record Row(
        long Id, long OwnerUserId, string OwnerDisplayName, string Name, string Description, string DefinitionJson,
        int SchemaVersion, int Revision, string? SourceTemplate, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
        string? UpdatedByDisplayName, string? GrantLevel, bool IsFavorite, bool IsDefault, int GrantCount, int WidgetCount);

    private sealed record GrantRow(
        long Id, long DashboardId, string SubjectType, long SubjectId, string SubjectName, string? SubjectDetail,
        string Level, DateTimeOffset GrantedAt, string? GrantedByDisplayName);

    private sealed record ConflictRow(int Revision, DateTimeOffset UpdatedAt, string? UpdatedBy);

    private sealed record DefaultRow(long? DashboardId, string? TemplateKey);

    private sealed record DirectoryRow(long Id, string Name, string? Detail);
}
