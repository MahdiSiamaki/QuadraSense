using System.Data;
using Dapper;
using Npgsql;
using Sqm.Application.Identity;

namespace Sqm.Infrastructure.Identity;

/// <summary>
/// The mutating half of the user directory.
/// </summary>
/// <remarks>
/// Every method here follows the same three steps, and the shape is deliberate:
/// <list type="number">
/// <item>open a transaction,</item>
/// <item>make the change and write the audit entry inside it,</item>
/// <item>check that administration is still reachable before committing.</item>
/// </list>
/// The audit entry is not a separate call afterwards, because a change that commits while its
/// audit entry fails is a change nobody can see. The guard is not a check beforehand, because
/// beforehand is the wrong state to check.
/// </remarks>
public sealed partial class PostgresUserDirectory
{
    /// <inheritdoc />
    public async Task<long> CreateAsync(
        NewUser user, string initialPassword, string actor, AuthenticationContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(context);

        var problem = PasswordPolicy.Validate(
            initialPassword, _policy, user.Username, user.DisplayName);

        if (problem is not null)
        {
            throw new PasswordPolicyException(problem);
        }

        const string insertSql = """
            INSERT INTO auth.user_account
                (username, display_name, email, job_title, phone, provider,
                 password_hash, password_updated_at, must_change_password,
                 created_by, updated_by)
            VALUES
                (@username, @displayName, @email, @jobTitle, @phone, 'local',
                 @hash, now(), @mustChange, @actor, @actor)
            RETURNING id
            """;

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        long userId;
        try
        {
            userId = await connection.ExecuteScalarAsync<long>(_db.Command(insertSql, new
            {
                username = user.Username.Trim(),
                displayName = user.DisplayName.Trim(),
                email = Blank(user.Email),
                jobTitle = Blank(user.JobTitle),
                phone = Blank(user.Phone),
                hash = _hasher.Hash(initialPassword),
                mustChange = user.MustChangePassword,
                actor,
            }, ct, transaction)).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // From the index, not from a prior SELECT. Check-then-insert races: two
            // administrators creating the same username at the same moment both see it free.
            throw DuplicateUsernameException.For(user.Username, ex);
        }

        await ReplaceRolesAsync(connection, transaction, userId, user.RoleCodes, actor, ct)
            .ConfigureAwait(false);

        await AuditAsync(connection, transaction, actor, "user.create", userId, user.Username,
            context, new Dictionary<string, object?>
            {
                ["roles"] = user.RoleCodes,
                ["mustChangePassword"] = user.MustChangePassword,
            }, ct).ConfigureAwait(false);

        await IdentitySql.EnsureAdministrationReachableAsync(
            connection, transaction, _commandTimeout, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        LogUserChange(actor, "created", userId);
        return userId;
    }

    /// <inheritdoc />
    public Task<bool> UpdateAsync(
        long userId, UserUpdate update, string actor, AuthenticationContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);

        return UpdateFieldsAsync(
            userId,
            new ProfileUpdate(update.DisplayName, update.Email, update.JobTitle, update.Phone),
            actor, "user.update", context, ct);
    }

    /// <inheritdoc />
    public Task<bool> UpdateProfileAsync(
        long userId, ProfileUpdate update, AuthenticationContext context, CancellationToken ct) =>
        // The actor is the user themselves; resolved from the row so the audit entry records the
        // stored username rather than anything the request supplied.
        UpdateFieldsAsync(userId, update, actor: null, "profile.update", context, ct);

    private async Task<bool> UpdateFieldsAsync(
        long userId, ProfileUpdate update, string? actor, string action,
        AuthenticationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(context);

        // The SET list is exhaustive and fixed. This is what makes ProfileUpdate an allow-list
        // rather than a suggestion: there is no path here that can reach is_active, a role, or a
        // password, however the caller shapes the request body.
        const string sql = """
            UPDATE auth.user_account
            SET display_name = @displayName,
                email        = @email,
                job_title    = @jobTitle,
                phone        = @phone,
                updated_at   = now(),
                updated_by   = COALESCE(@actor, username)
            WHERE id = @userId
            RETURNING username
            """;

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var username = await connection.ExecuteScalarAsync<string?>(_db.Command(sql, new
        {
            userId,
            displayName = update.DisplayName.Trim(),
            email = Blank(update.Email),
            jobTitle = Blank(update.JobTitle),
            phone = Blank(update.Phone),
            actor,
        }, ct, transaction)).ConfigureAwait(false);

        if (username is null)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }

        await AuditAsync(connection, transaction, actor ?? username, action, userId, username,
            context, null, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        LogUserChange(actor ?? username, "updated", userId);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> SetActiveAsync(
        long userId, bool isActive, string? reason, string actor, AuthenticationContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        const string sql = """
            UPDATE auth.user_account
            SET is_active           = @isActive,
                deactivated_at      = CASE WHEN @isActive THEN NULL ELSE now() END,
                deactivated_by      = CASE WHEN @isActive THEN NULL ELSE @actor END,
                deactivation_reason = CASE WHEN @isActive THEN NULL ELSE @reason END,
                -- Reactivating clears the lockout too. Otherwise an account can be switched back
                -- on and still refuse its owner for fifteen minutes, with nothing on screen
                -- explaining why.
                failed_login_count  = CASE WHEN @isActive THEN 0 ELSE failed_login_count END,
                locked_until        = CASE WHEN @isActive THEN NULL ELSE locked_until END,
                updated_at          = now(),
                updated_by          = @actor
            WHERE id = @userId AND is_active <> @isActive
            RETURNING username
            """;

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var username = await connection.ExecuteScalarAsync<string?>(
            _db.Command(sql, new { userId, isActive, actor, reason = Blank(reason) }, ct, transaction))
            .ConfigureAwait(false);

        if (username is null)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }

        var revoked = 0;
        if (!isActive)
        {
            // In the same transaction. An account disabled in one transaction and signed out in
            // another has a window - short, but real - in which it is disabled and still working.
            revoked = await PostgresSessionStore.RevokeAllForUserAsync(
                connection, transaction, userId, null, actor, "account deactivated",
                _commandTimeout, ct).ConfigureAwait(false);
        }

        await AuditAsync(connection, transaction, actor,
            isActive ? "user.activate" : "user.deactivate", userId, username, context,
            new Dictionary<string, object?>
            {
                ["reason"] = reason,
                ["sessionsRevoked"] = revoked,
            }, ct).ConfigureAwait(false);

        await IdentitySql.EnsureAdministrationReachableAsync(
            connection, transaction, _commandTimeout, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        LogUserChange(actor, isActive ? "activated" : "deactivated", userId);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> SetRolesAsync(
        long userId, IReadOnlyList<string> roleCodes, string actor, AuthenticationContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roleCodes);
        ArgumentNullException.ThrowIfNull(context);

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var username = await connection.ExecuteScalarAsync<string?>(_db.Command(
            "SELECT username FROM auth.user_account WHERE id = @userId FOR UPDATE",
            new { userId }, ct, transaction)).ConfigureAwait(false);

        if (username is null)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }

        var before = await ReadRoleCodesAsync(connection, transaction, userId, ct)
            .ConfigureAwait(false);

        await ReplaceRolesAsync(connection, transaction, userId, roleCodes, actor, ct)
            .ConfigureAwait(false);

        var after = await ReadRoleCodesAsync(connection, transaction, userId, ct)
            .ConfigureAwait(false);

        // Before and after, not just after. "Roles are now Analyst" does not tell a reviewer
        // whether anything was taken away, which is the half of the change that matters.
        await AuditAsync(connection, transaction, actor, "user.roles.set", userId, username,
            context, new Dictionary<string, object?>
            {
                ["before"] = before,
                ["after"] = after,
                ["added"] = after.Except(before, StringComparer.Ordinal).ToArray(),
                ["removed"] = before.Except(after, StringComparer.Ordinal).ToArray(),
            }, ct).ConfigureAwait(false);

        await IdentitySql.EnsureAdministrationReachableAsync(
            connection, transaction, _commandTimeout, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        LogUserChange(actor, "roles changed on", userId);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> SetPermissionOverridesAsync(
        long userId, IReadOnlyList<PermissionOverride> permissionOverrides, string actor,
        AuthenticationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(permissionOverrides);
        ArgumentNullException.ThrowIfNull(context);

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var username = await connection.ExecuteScalarAsync<string?>(_db.Command(
            "SELECT username FROM auth.user_account WHERE id = @userId FOR UPDATE",
            new { userId }, ct, transaction)).ConfigureAwait(false);

        if (username is null)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }

        await connection.ExecuteAsync(_db.Command(
            "DELETE FROM auth.user_permission WHERE user_id = @userId",
            new { userId }, ct, transaction)).ConfigureAwait(false);

        if (permissionOverrides.Count > 0)
        {
            // The foreign key to auth.permission is what rejects an unknown code, rather than a
            // list checked in C# that would need updating every time a permission is added.
            const string insertSql = """
                INSERT INTO auth.user_permission
                    (user_id, permission_code, effect, reason, granted_by)
                VALUES (@userId, @code, @effect, @reason, @actor)
                """;

            foreach (var item in permissionOverrides)
            {
                await connection.ExecuteAsync(_db.Command(insertSql, new
                {
                    userId,
                    code = item.PermissionCode,
                    effect = item.Effect == PermissionEffect.Deny ? "deny" : "grant",
                    reason = Blank(item.Reason),
                    actor,
                }, ct, transaction)).ConfigureAwait(false);
            }
        }

        await AuditAsync(connection, transaction, actor, "user.permissions.set", userId, username,
            context, new Dictionary<string, object?>
            {
                ["grants"] = permissionOverrides
                    .Where(o => o.Effect == PermissionEffect.Grant)
                    .Select(o => o.PermissionCode).ToArray(),
                ["denies"] = permissionOverrides
                    .Where(o => o.Effect == PermissionEffect.Deny)
                    .Select(o => o.PermissionCode).ToArray(),
            }, ct).ConfigureAwait(false);

        await IdentitySql.EnsureAdministrationReachableAsync(
            connection, transaction, _commandTimeout, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        LogUserChange(actor, "permission overrides changed on", userId);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> ResetPasswordAsync(
        long userId, string newPassword, string actor, AuthenticationContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var target = await connection.QuerySingleOrDefaultAsync<NameRow>(_db.Command(
            "SELECT username AS Username, display_name AS DisplayName "
            + "FROM auth.user_account WHERE id = @userId FOR UPDATE",
            new { userId }, ct, transaction)).ConfigureAwait(false);

        if (target is null)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }

        var problem = PasswordPolicy.Validate(
            newPassword, _policy, target.Username, target.DisplayName);

        if (problem is not null)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            throw new PasswordPolicyException(problem);
        }

        const string sql = """
            UPDATE auth.user_account
            SET password_hash        = @hash,
                password_updated_at  = now(),
                must_change_password = true,
                failed_login_count   = 0,
                locked_until         = NULL,
                updated_at           = now(),
                updated_by           = @actor
            WHERE id = @userId
            """;

        await connection.ExecuteAsync(_db.Command(sql, new
        {
            userId, hash = _hasher.Hash(newPassword), actor,
        }, ct, transaction)).ConfigureAwait(false);

        var revoked = await PostgresSessionStore.RevokeAllForUserAsync(
            connection, transaction, userId, null, actor, "password reset by administrator",
            _commandTimeout, ct).ConfigureAwait(false);

        await AuditAsync(connection, transaction, actor, "user.password.reset", userId,
            target.Username, context,
            new Dictionary<string, object?> { ["sessionsRevoked"] = revoked }, ct)
            .ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        LogUserChange(actor, "reset the password of", userId);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> ChangeOwnPasswordAsync(
        long userId, string currentPassword, string newPassword, Guid currentSessionId,
        AuthenticationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var row = await connection.QuerySingleOrDefaultAsync<PasswordRow>(_db.Command(
            "SELECT username AS Username, display_name AS DisplayName, "
            + "password_hash AS PasswordHash FROM auth.user_account "
            + "WHERE id = @userId AND provider = 'local' FOR UPDATE",
            new { userId }, ct, transaction)).ConfigureAwait(false);

        if (row?.PasswordHash is null
            || _hasher.Verify(currentPassword, row.PasswordHash) == PasswordVerification.Failed)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);

            // Audited on its own connection, because this transaction is being abandoned. A
            // failed attempt to change someone's password is exactly the event worth keeping.
            await using var audit = await _db.OpenAsync(ct).ConfigureAwait(false);
            await IdentitySql.WriteAuditAsync(audit, null, new AuditEntry(
                ActorName: row?.Username ?? $"user:{userId}",
                Action: "user.password.change",
                Category: AuditCategory.Authentication,
                Outcome: AuditOutcome.Failure,
                ActorUserId: userId,
                Ip: context.Ip,
                UserAgent: context.UserAgent,
                CorrelationId: context.CorrelationId,
                Detail: new Dictionary<string, object?> { ["reason"] = "wrong_current_password" }),
                _commandTimeout, ct).ConfigureAwait(false);

            return false;
        }

        var problem = PasswordPolicy.Validate(newPassword, _policy, row.Username, row.DisplayName);
        if (problem is not null)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            throw new PasswordPolicyException(problem);
        }

        const string sql = """
            UPDATE auth.user_account
            SET password_hash        = @hash,
                password_updated_at  = now(),
                must_change_password = false,
                updated_at           = now(),
                updated_by           = username
            WHERE id = @userId
            """;

        await connection.ExecuteAsync(_db.Command(
            sql, new { userId, hash = _hasher.Hash(newPassword) }, ct, transaction))
            .ConfigureAwait(false);

        // Every session except this one. A password changed because the old one was believed
        // compromised has not recovered the account while the attacker's session still works -
        // and signing the user out of the tab they just used would be punishing them for it.
        var revoked = await PostgresSessionStore.RevokeAllForUserAsync(
            connection, transaction, userId, currentSessionId, row.Username,
            "password changed", _commandTimeout, ct).ConfigureAwait(false);

        await AuditAsync(connection, transaction, row.Username, "user.password.change", userId,
            row.Username, context,
            new Dictionary<string, object?> { ["sessionsRevoked"] = revoked }, ct)
            .ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        LogUserChange(row.Username, "changed the password of", userId);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> UnlockAsync(
        long userId, string actor, AuthenticationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        const string sql = """
            UPDATE auth.user_account
            SET failed_login_count = 0, locked_until = NULL, updated_at = now(), updated_by = @actor
            WHERE id = @userId
            RETURNING username
            """;

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var username = await connection.ExecuteScalarAsync<string?>(
            _db.Command(sql, new { userId, actor }, ct, transaction)).ConfigureAwait(false);

        if (username is null)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }

        await AuditAsync(connection, transaction, actor, "user.unlock", userId, username,
            context, null, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        LogUserChange(actor, "unlocked", userId);
        return true;
    }

    // =======================================================================
    // Helpers
    // =======================================================================

    private async Task ReplaceRolesAsync(
        NpgsqlConnection connection, IDbTransaction transaction, long userId,
        IReadOnlyList<string> roleCodes, string actor, CancellationToken ct)
    {
        await connection.ExecuteAsync(_db.Command(
            "DELETE FROM auth.user_role WHERE user_id = @userId",
            new { userId }, ct, transaction)).ConfigureAwait(false);

        if (roleCodes.Count == 0)
        {
            return;
        }

        // An unknown role code produces no row rather than an error, so the count is compared
        // afterwards: silently granting three of four requested roles is the kind of partial
        // success that looks like it worked.
        const string sql = """
            INSERT INTO auth.user_role (user_id, role_id, granted_by)
            SELECT @userId, r.id, @actor
            FROM auth.role r
            WHERE r.code = ANY(@codes)
            """;

        var inserted = await connection.ExecuteAsync(_db.Command(
            sql, new { userId, codes = roleCodes.ToArray(), actor }, ct, transaction))
            .ConfigureAwait(false);

        if (inserted != roleCodes.Distinct(StringComparer.Ordinal).Count())
        {
            throw new RoleChangeRefusedException(
                "One or more of those role codes does not exist.");
        }
    }

    private async Task<string[]> ReadRoleCodesAsync(
        NpgsqlConnection connection, IDbTransaction transaction, long userId, CancellationToken ct)
    {
        var rows = await connection.QueryAsync<string>(_db.Command(
            "SELECT r.code FROM auth.user_role ur JOIN auth.role r ON r.id = ur.role_id "
            + "WHERE ur.user_id = @userId ORDER BY r.code",
            new { userId }, ct, transaction)).ConfigureAwait(false);

        return [.. rows];
    }

    private Task AuditAsync(
        NpgsqlConnection connection,
        IDbTransaction transaction,
        string actor,
        string action,
        long targetUserId,
        string targetUsername,
        AuthenticationContext context,
        IReadOnlyDictionary<string, object?>? detail,
        CancellationToken ct) =>
        IdentitySql.WriteAuditAsync(connection, transaction, new AuditEntry(
            ActorName: actor,
            Action: action,
            Category: AuditCategory.User,
            Outcome: AuditOutcome.Success,
            TargetType: "user",
            TargetId: targetUserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            TargetName: targetUsername,
            Ip: context.Ip,
            UserAgent: context.UserAgent,
            CorrelationId: context.CorrelationId,
            Detail: detail), _commandTimeout, ct);

    /// <summary>Turns an empty or whitespace string into null, so the column holds one absence.</summary>
    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record NameRow(string Username, string DisplayName);

    private sealed record PasswordRow(string Username, string DisplayName, string? PasswordHash);
}
