namespace Sqm.Application.Identity;

/// <summary>Writes and reads the append-only audit trail.</summary>
/// <remarks>
/// <para>
/// There is no update and no delete, here or in the database: the application's PostgreSQL role
/// holds INSERT and SELECT on <c>audit.event</c> and nothing more
/// (<c>db/operational/grants/003_least_privilege.sql</c>). An interface without a delete method is
/// a convention; a role without the DELETE privilege is a guarantee, and only the second one
/// survives a bug or a compromised process.
/// </para>
/// <para>
/// Identifiers are never written into an audit detail. A subscriber lookup is audited as "who,
/// when, how many results" - not which number was searched. That rule exists because masking is
/// off by product-owner decision, which makes the audit log the primary record of who saw whom,
/// and an audit log full of MSISDNs is a second copy of the data it is meant to protect.
/// </para>
/// </remarks>
public interface IAuditLog
{
    /// <summary>Appends one entry.</summary>
    /// <remarks>
    /// Failures are logged and swallowed for read-only actions, and propagate for mutations that
    /// write the audit entry in the same transaction as the change. A dashboard that will not
    /// load because the audit table is unavailable is an outage; a role change that succeeds
    /// without a trace is a hole.
    /// </remarks>
    Task WriteAsync(AuditEntry entry, CancellationToken ct);

    /// <summary>A page of the merged audit stream, newest first.</summary>
    Task<AuditPage> QueryAsync(AuditQuery query, CancellationToken ct);

    /// <summary>Distinct action codes present in the log, for the filter dropdown.</summary>
    Task<IReadOnlyList<string>> GetActionsAsync(CancellationToken ct);
}

/// <summary>One thing that happened.</summary>
/// <remarks>
/// <c>Action</c> is a dotted code, matching a permission where one governs the action.
/// <c>TargetName</c> is the target's name at the time, denormalised so the entry still reads
/// correctly after the target is renamed or deleted.
/// </remarks>
public sealed record AuditEntry(
    string ActorName,
    string Action,
    AuditCategory Category,
    AuditOutcome Outcome,
    long? ActorUserId = null,
    string? TargetType = null,
    string? TargetId = null,
    string? TargetName = null,
    string? Ip = null,
    string? UserAgent = null,
    string? CorrelationId = null,
    IReadOnlyDictionary<string, object?>? Detail = null);

/// <summary>Which part of the system an audit entry belongs to.</summary>
public enum AuditCategory
{
    /// <summary>Sign-in, sign-out, password change, session revocation.</summary>
    Authentication,

    /// <summary>User accounts and their grants.</summary>
    User,

    /// <summary>Roles and their permissions.</summary>
    Role,

    /// <summary>Reading or exporting data, including subscriber lookup.</summary>
    Data,

    /// <summary>The import pipeline.</summary>
    Import,

    /// <summary>Everything else an administrator does.</summary>
    System,
}

/// <summary>How an audited action ended.</summary>
public enum AuditOutcome
{
    /// <summary>It happened.</summary>
    Success,

    /// <summary>It was attempted and did not work - a wrong password, a failed job.</summary>
    Failure,

    /// <summary>It was refused by authorisation. The most interesting kind.</summary>
    Denied,
}

/// <summary>Filters for the audit log page.</summary>
public sealed record AuditQuery(
    string? Search = null,
    string? Action = null,
    AuditCategory? Category = null,
    AuditOutcome? Outcome = null,
    long? ActorUserId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Page = 1,
    int PageSize = 50);

/// <summary>A page of audit entries.</summary>
public sealed record AuditPage(IReadOnlyList<AuditRecord> Items, int Total, int Page, int PageSize);

/// <summary>An audit entry as read back, from either underlying table.</summary>
public sealed record AuditRecord(
    string EntryId,
    DateTimeOffset OccurredAt,
    long? ActorUserId,
    string ActorName,
    string Action,
    string Category,
    string Outcome,
    string? TargetType,
    string? TargetId,
    string? TargetName,
    string? SourceIp,
    string? CorrelationId,
    string? Detail);
