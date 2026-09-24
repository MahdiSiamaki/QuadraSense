using System.Data;
using System.Text.Json;
using Dapper;
using Npgsql;
using Sqm.Application.Identity;

namespace Sqm.Infrastructure.Identity;

/// <summary>Statements shared by more than one identity repository.</summary>
/// <remarks>
/// Two things live here because both are needed inside other repositories' transactions, and a
/// copy in each would be a copy that drifts: appending an audit entry, and the guard that stops
/// the last administrator from locking everyone out.
/// </remarks>
internal static class IdentitySql
{
    /// <summary>
    /// Resolves a user's effective permissions from roles, direct grants and direct denies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This expression is the authorisation rule, written once. Every read of a user's
    /// permissions - the request pipeline, the user detail page, the last-administrator guard -
    /// uses it, so the permissions shown on screen cannot disagree with the ones enforced.
    /// </para>
    /// <para>
    /// <paramref name="userExpression"/> is SQL naming the user - either a bound parameter
    /// (<c>@userId</c>) or a correlated subquery, depending on whether the caller already knows
    /// the id. It is always a literal chosen by this assembly and never a value from a request;
    /// the same rule the import repository's filter builder follows.
    /// </para>
    /// </remarks>
    public static string GrantedPermissionCodes(string userExpression) => $"""
        SELECT p.code
        FROM auth.permission p
        LEFT JOIN auth.user_permission up
               ON up.user_id = {userExpression} AND up.permission_code = p.code
        WHERE COALESCE(up.effect, '') <> 'deny'
          AND (up.effect = 'grant'
               OR EXISTS (SELECT 1
                          FROM auth.user_role ur
                          JOIN auth.role_permission rp ON rp.role_id = ur.role_id
                          WHERE ur.user_id = {userExpression}
                            AND rp.permission_code = p.code))
        """;

    /// <summary>Appends one audit entry, inside the caller's transaction.</summary>
    /// <remarks>
    /// The change and its audit record commit together or not at all. That is the same guarantee
    /// the import platform gets by making the job queue and its audit trail one write, and it is
    /// why this takes a transaction rather than being a separate service call.
    /// </remarks>
    /// <summary>The id of the account named <paramref name="actor"/>, or null for a non-account actor.</summary>
    /// <remarks>
    /// The administration writes are told who acted by name. Their audit entries went in with the
    /// name only, so the log showed no link to the account behind a user or role change, and a
    /// filter by actor id left out every administrative change. Resolved in the same transaction,
    /// so it names the account as it was when the change was made.
    /// </remarks>
    public static async Task<long?> ResolveActorIdAsync(
        NpgsqlConnection connection, IDbTransaction? transaction, string actor, int commandTimeout,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM auth.user_account WHERE lower(username) = lower(@actor)",
            new { actor }, transaction, commandTimeout, cancellationToken: ct)).ConfigureAwait(false);
    }

    public static async Task WriteAuditAsync(
        NpgsqlConnection connection,
        IDbTransaction? transaction,
        AuditEntry entry,
        int commandTimeout,
        CancellationToken ct)
    {
        const string sql = """
            INSERT INTO audit.event
                (actor_user_id, actor_name, action, category, outcome,
                 target_type, target_id, target_name, source_ip, user_agent,
                 correlation_id, detail)
            VALUES
                (@actorUserId, @actorName, @action, @category, @outcome,
                 @targetType, @targetId, @targetName, @ip::inet, @userAgent,
                 @correlationId, @detail::jsonb)
            """;

        var parameters = new
        {
            actorUserId = entry.ActorUserId,
            actorName = entry.ActorName,
            action = entry.Action,
            category = ToDatabase(entry.Category),
            outcome = ToDatabase(entry.Outcome),
            targetType = entry.TargetType,
            targetId = entry.TargetId,
            targetName = entry.TargetName,
            ip = NormaliseIp(entry.Ip),
            // Bounded: a user agent is client-controlled text, and there is no reason to store a
            // kilobyte of it per request.
            userAgent = Truncate(entry.UserAgent, 512),
            correlationId = entry.CorrelationId,
            detail = entry.Detail is null ? null : JsonSerializer.Serialize(entry.Detail),
        };

        await connection.ExecuteAsync(
            new CommandDefinition(sql, parameters, transaction, commandTimeout, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Throws if no active user would hold every administration-critical permission.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called at the END of every transaction that changes users, roles or grants, so it sees the
    /// state the commit would produce. Checking before the change would be checking the wrong
    /// state; checking after the commit would be reporting a disaster rather than preventing one.
    /// </para>
    /// <para>
    /// The scenario is not hypothetical. An administrator unchecking <c>user.manage</c> on the
    /// Administrator role, or deactivating the only account that holds it, leaves a system whose
    /// only recovery is a database session. It is one mis-click on a grid of checkboxes.
    /// </para>
    /// </remarks>
    public static async Task EnsureAdministrationReachableAsync(
        NpgsqlConnection connection,
        IDbTransaction transaction,
        int commandTimeout,
        CancellationToken ct)
    {
        // For each critical permission, is there at least one ACTIVE user who effectively holds
        // it? Written as a single query so the answer is one round trip whatever the list holds.
        const string sql = """
            SELECT count(*) FROM unnest(@critical::text[]) AS needed(code)
            WHERE NOT EXISTS (
                SELECT 1
                FROM auth.user_account u
                LEFT JOIN auth.user_permission up
                       ON up.user_id = u.id AND up.permission_code = needed.code
                WHERE u.is_active
                  AND COALESCE(up.effect, '') <> 'deny'
                  AND (up.effect = 'grant'
                       OR EXISTS (SELECT 1
                                  FROM auth.user_role ur
                                  JOIN auth.role_permission rp ON rp.role_id = ur.role_id
                                  WHERE ur.user_id = u.id AND rp.permission_code = needed.code))
            )
            """;

        var unreachable = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                sql,
                new { critical = Permissions.AdministrationCritical.ToArray() },
                transaction, commandTimeout, cancellationToken: ct))
            .ConfigureAwait(false);

        if (unreachable > 0)
        {
            throw new LastAdministratorException(
                "This change would leave no active user able to manage users and roles. "
                + "At least one active account must keep both user.manage and role.manage.");
        }
    }

    /// <summary>Maps the enum to the value the CHECK constraint accepts.</summary>
    public static string ToDatabase(AuditCategory category) => category switch
    {
        AuditCategory.Authentication => "authentication",
        AuditCategory.User => "user",
        AuditCategory.Role => "role",
        AuditCategory.Data => "data",
        AuditCategory.Import => "import",
        AuditCategory.System => "system",
        _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };

    /// <summary>Maps the enum to the value the CHECK constraint accepts.</summary>
    public static string ToDatabase(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Success => "success",
        AuditOutcome.Failure => "failure",
        AuditOutcome.Denied => "denied",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    /// <summary>
    /// Turns a client address into something the <c>inet</c> column accepts, or null.
    /// </summary>
    /// <remarks>
    /// Kestrel reports an IPv4 client as <c>::ffff:10.0.0.5</c> when the socket is dual-stack,
    /// which <c>inet</c> stores as an IPv6 address - so the same machine appears under two
    /// different addresses depending on how it connected, and filtering the audit log by IP
    /// quietly misses half the entries.
    /// </remarks>
    public static string? NormaliseIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return null;
        }

        if (!System.Net.IPAddress.TryParse(ip, out var address))
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.ToString();
    }

    /// <summary>Caps client-supplied text before it reaches the database.</summary>
    public static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
