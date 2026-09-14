using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sqm.Application.Identity;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Infrastructure.Identity;

/// <summary>The audit trail, read through the unified view and written to <c>audit.event</c>.</summary>
/// <remarks>
/// Reads go through <c>audit.event_log</c>, which unions the identity events with the import
/// platform's own audit table so the Audit Log page is one query rather than two lists a reader
/// has to merge by eye.
/// </remarks>
public sealed partial class PostgresAuditLog : IAuditLog
{
    [LoggerMessage(EventId = 4300, Level = LogLevel.Error,
        Message = "Failed to write audit entry {Action}")]
    private partial void LogWriteFailed(string action, Exception exception);

    private readonly IdentityDataSource _db;
    private readonly int _commandTimeout;
    private readonly ILogger<PostgresAuditLog> _logger;

    /// <summary>Creates the log.</summary>
    public PostgresAuditLog(
        IdentityDataSource db, IOptions<PostgresOptions> postgres, ILogger<PostgresAuditLog> logger)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        _db = db;
        _commandTimeout = postgres.Value.CommandTimeoutSeconds;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task WriteAsync(AuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        try
        {
            await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
            await IdentitySql.WriteAuditAsync(connection, null, entry, _commandTimeout, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // This overload is for actions whose audit entry is not part of a transaction -
            // a subscriber lookup, an export, a denied request. Failing the user's request
            // because the audit table is momentarily unavailable would turn a logging problem
            // into an outage, so it is logged loudly and swallowed.
            //
            // Mutations do NOT come through here: they write their audit entry inside the same
            // transaction as the change, where a failure correctly rolls both back.
            LogWriteFailed(entry.Action, ex);
        }
    }

    /// <inheritdoc />
    public async Task<AuditPage> QueryAsync(AuditQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        const string sql = """
            SELECT count(*) OVER()::int AS Total,
                   entry_id         AS EntryId,
                   occurred_at      AS OccurredAt,
                   actor_user_id    AS ActorUserId,
                   actor_name       AS ActorName,
                   action           AS Action,
                   category         AS Category,
                   outcome          AS Outcome,
                   target_type      AS TargetType,
                   target_id        AS TargetId,
                   target_name      AS TargetName,
                   host(source_ip)  AS SourceIp,
                   correlation_id   AS CorrelationId,
                   detail::text     AS Detail
            FROM audit.event_log
            WHERE (@search IS NULL
                   OR actor_name ILIKE @pattern
                   OR action ILIKE @pattern
                   OR target_name ILIKE @pattern)
              AND (@action   IS NULL OR action = @action)
              AND (@category IS NULL OR category = @category)
              AND (@outcome  IS NULL OR outcome = @outcome)
              AND (@actorUserId::bigint IS NULL OR actor_user_id = @actorUserId::bigint)
              AND (@from::timestamptz IS NULL OR occurred_at >= @from::timestamptz)
              AND (@to::timestamptz   IS NULL OR occurred_at <  @to::timestamptz)
            ORDER BY occurred_at DESC, entry_id DESC
            LIMIT @limit OFFSET @offset
            """;

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var rows = (await connection.QueryAsync<AuditRow>(_db.Command(sql, new
        {
            search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search,
            pattern = LikePattern(query.Search),
            action = query.Action,
            category = query.Category is { } c ? IdentitySql.ToDatabase(c) : null,
            outcome = query.Outcome is { } o ? IdentitySql.ToDatabase(o) : null,
            actorUserId = query.ActorUserId,
            from = query.From,
            to = query.To,
            limit = pageSize,
            offset = (page - 1) * pageSize,
        }, ct)).ConfigureAwait(false)).AsList();

        var items = rows.Select(r => new AuditRecord(
            r.EntryId, r.OccurredAt, r.ActorUserId, r.ActorName, r.Action, r.Category, r.Outcome,
            r.TargetType, r.TargetId, r.TargetName, r.SourceIp, r.CorrelationId, r.Detail))
            .ToList();

        return new AuditPage(items, rows.Count == 0 ? 0 : rows[0].Total, page, pageSize);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetActionsAsync(CancellationToken ct)
    {
        // Distinct over the view, which is a sequential scan of both tables. Acceptable because
        // the filter dropdown is loaded once per page visit against a table measured in tens of
        // thousands of rows; if it stops being acceptable, this becomes a lookup table
        // maintained by the writer.
        const string sql = "SELECT DISTINCT action FROM audit.event_log ORDER BY action";

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var rows = await connection.QueryAsync<string>(_db.Command(sql, null, ct))
            .ConfigureAwait(false);

        return rows.AsList();
    }

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

    private sealed record AuditRow(
        int Total, string EntryId, DateTimeOffset OccurredAt, long? ActorUserId, string ActorName,
        string Action, string Category, string Outcome, string? TargetType, string? TargetId,
        string? TargetName, string? SourceIp, string? CorrelationId, string? Detail);
}
