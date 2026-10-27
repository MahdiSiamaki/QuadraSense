using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Sqm.Application.Campaigns;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Infrastructure.Identity;

/// <summary>Campaign list requests in PostgreSQL (operational migration 021).</summary>
/// <remarks>
/// A decision is one conditional UPDATE: pending, and not by the requester. A second decider racing the
/// first finds nothing pending; a requester deciding their own request is refused here and, failing
/// that, by the table's own CHECK.
/// </remarks>
public sealed class PostgresCampaignExportStore : ICampaignExportStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IdentityDataSource _db;
    private readonly int _commandTimeout;

    /// <summary>Creates the store over the operational database.</summary>
    public PostgresCampaignExportStore(IdentityDataSource db, IOptions<PostgresOptions> postgres)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        _db = db;
        _commandTimeout = postgres.Value.CommandTimeoutSeconds;
    }

    private const string Select = """
        SELECT r.id AS Id, r.definition::text AS Definition, r.row_count AS RowCount, r.reason AS Reason,
               r.requested_by AS RequestedBy, req.username AS RequestedByName, r.requested_at AS RequestedAt,
               r.needs_approval AS NeedsApproval, r.status AS Status,
               r.decided_by AS DecidedBy, dec.username AS DecidedByName, r.decided_at AS DecidedAt,
               r.decision_note AS DecisionNote, r.expires_at AS ExpiresAt, r.downloads AS Downloads,
               r.last_downloaded_at AS LastDownloadedAt
        FROM campaign.export_request AS r
        JOIN auth.user_account AS req ON req.id = r.requested_by
        LEFT JOIN auth.user_account AS dec ON dec.id = r.decided_by
        """;

    /// <inheritdoc />
    public async Task<CampaignExportRecord> CreateAsync(
        CampaignListDefinition definition, long rowCount, string reason, long requestedBy, bool needsApproval,
        DateTimeOffset? expiresAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(reason);

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO campaign.export_request (definition, row_count, reason, requested_by, needs_approval, status, expires_at)
            VALUES (@definition::jsonb, @rowCount, @reason, @requestedBy, @needsApproval,
                    CASE WHEN @needsApproval THEN 'pending' ELSE 'approved' END,
                    CASE WHEN @needsApproval THEN NULL ELSE @expiresAt END)
            RETURNING id
            """,
            new { definition = JsonSerializer.Serialize(definition, Json), rowCount, reason = reason.Trim(), requestedBy, needsApproval, expiresAt },
            commandTimeout: _commandTimeout, cancellationToken: ct)).ConfigureAwait(false);

        return (await GetAsync(id, ct).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CampaignExportRecord>> ListAsync(long? requestedBy, int limit, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            $"{Select} WHERE (@requestedBy::bigint IS NULL OR r.requested_by = @requestedBy) ORDER BY r.requested_at DESC, r.id DESC LIMIT @limit",
            new { requestedBy, limit }, commandTimeout: _commandTimeout, cancellationToken: ct)).ConfigureAwait(false);
        return [.. rows.Select(Map)];
    }

    /// <inheritdoc />
    public async Task<CampaignExportRecord?> GetAsync(long id, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"{Select} WHERE r.id = @id", new { id }, commandTimeout: _commandTimeout, cancellationToken: ct)).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    /// <inheritdoc />
    public async Task<(CampaignDecisionOutcome Outcome, CampaignExportRecord? Request)> DecideAsync(
        long id, long decidedBy, bool approve, string? note, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        var current = await GetAsync(id, ct).ConfigureAwait(false);
        if (current is null)
        {
            return (CampaignDecisionOutcome.NotFound, null);
        }

        if (current.RequestedBy == decidedBy)
        {
            return (CampaignDecisionOutcome.OwnRequest, current);
        }

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE campaign.export_request
            SET status = CASE WHEN @approve THEN 'approved' ELSE 'rejected' END,
                decided_by = @decidedBy, decided_at = now(), decision_note = @note,
                expires_at = CASE WHEN @approve THEN @expiresAt ELSE NULL END
            WHERE id = @id AND status = 'pending' AND requested_by <> @decidedBy
            """,
            new { id, decidedBy, approve, note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), expiresAt },
            commandTimeout: _commandTimeout, cancellationToken: ct)).ConfigureAwait(false);

        return changed == 0
            ? (CampaignDecisionOutcome.NotPending, current)
            : (CampaignDecisionOutcome.Decided, await GetAsync(id, ct).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<bool> WithdrawAsync(long id, long requestedBy, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE campaign.export_request SET status = 'withdrawn', expires_at = NULL
            WHERE id = @id AND requested_by = @requestedBy AND status IN ('pending', 'approved')
            """,
            new { id, requestedBy }, commandTimeout: _commandTimeout, cancellationToken: ct)).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public async Task RecordDownloadAsync(long id, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE campaign.export_request SET downloads = downloads + 1, last_downloaded_at = now() WHERE id = @id",
            new { id }, commandTimeout: _commandTimeout, cancellationToken: ct)).ConfigureAwait(false);
    }

    private static CampaignExportRecord Map(Row r) => new(
        r.Id, JsonSerializer.Deserialize<CampaignListDefinition>(r.Definition, Json)!, r.RowCount, r.Reason,
        r.RequestedBy, r.RequestedByName, r.RequestedAt, r.NeedsApproval, r.Status,
        r.DecidedBy, r.DecidedByName, r.DecidedAt, r.DecisionNote, r.ExpiresAt, r.Downloads, r.LastDownloadedAt);

    private sealed record Row(
        long Id, string Definition, long RowCount, string Reason, long RequestedBy, string RequestedByName,
        DateTimeOffset RequestedAt, bool NeedsApproval, string Status, long? DecidedBy, string? DecidedByName,
        DateTimeOffset? DecidedAt, string? DecisionNote, DateTimeOffset? ExpiresAt, int Downloads, DateTimeOffset? LastDownloadedAt);
}
