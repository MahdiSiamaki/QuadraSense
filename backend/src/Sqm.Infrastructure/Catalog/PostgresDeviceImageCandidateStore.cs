using System.Data;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Sqm.Application.Abstractions;
using Sqm.Contracts.Devices;
using Sqm.Infrastructure.DataImport;
using Sqm.Infrastructure.Identity;

namespace Sqm.Infrastructure.Catalog;

/// <summary>
/// The review queue for proposed device images.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="PostgresDeviceImageStore"/> on purpose: that one serves the images
/// the product shows, on a hot path, and this one is an administrative queue nobody reads per
/// page view. Keeping them apart keeps the hot path free of the columns and joins the queue
/// needs.
/// </para>
/// <para>
/// <b>Approval is the only way a candidate becomes live</b>, and it happens in one transaction:
/// the bytes move into <c>device_model_image</c> marked <c>verified</c> with the reviewer's name
/// on them, and the candidate is marked approved. A failure anywhere leaves both tables as they
/// were, which matters because the halfway state - a live image nobody approved, or an approval
/// recorded against an image that never moved - is exactly the confusion this whole feature was
/// built to remove.
/// </para>
/// </remarks>
public sealed class PostgresDeviceImageCandidateStore : IDeviceImageCandidateStore
{
    /// <summary>Shared, because constructing options per row is a per-row allocation.</summary>
    private static readonly JsonSerializerOptions BreakdownJson = new(JsonSerializerDefaults.Web);

    private readonly IdentityDataSource _db;
    private readonly int _commandTimeout;

    /// <summary>Creates the store.</summary>
    /// <param name="db">Connection source for the operational database.</param>
    /// <param name="postgres">Options carrying the command timeout.</param>
    public PostgresDeviceImageCandidateStore(IdentityDataSource db, IOptions<PostgresOptions> postgres)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        _db = db;
        _commandTimeout = postgres.Value.CommandTimeoutSeconds;
    }

    /// <inheritdoc />
    public async Task<DeviceImageCandidatePage> ListAsync(
        string status, int limit, int offset, CancellationToken ct)
    {
        // The live image's status is joined in because a reviewer's first question is whether
        // they are filling a gap or overruling somebody. count() OVER () so the total and the
        // page come from one pass and cannot disagree.
        const string Sql = """
            SELECT c.id                              AS Id,
                   c.model_key                       AS ModelKey,
                   c.brand                           AS Brand,
                   c.marketing_name                  AS MarketingName,
                   c.status                          AS Status,
                   c.content_type                    AS ContentType,
                   octet_length(c.bytes)             AS ByteSize,
                   c.source_type                     AS SourceType,
                   c.source_domain                   AS SourceDomain,
                   c.source_url                      AS SourceUrl,
                   c.original_width                  AS OriginalWidth,
                   c.original_height                 AS OriginalHeight,
                   c.quality_score                   AS QualityScore,
                   c.score_breakdown::text           AS ScoreBreakdownJson,
                   c.rejection_reason                AS RejectionReason,
                   c.created_at                      AS CreatedAt,
                   coalesce(i.status, 'missing')     AS CurrentStatus,
                   count(*) OVER ()                  AS Total
            FROM catalog.device_image_candidate AS c
            LEFT JOIN catalog.device_model_image AS i ON i.model_key = c.model_key
            WHERE (@status = 'all' OR c.status = @status)
            ORDER BY c.quality_score DESC, c.created_at
            LIMIT @limit OFFSET @offset
            """;

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var rows = await connection.QueryAsync<CandidateRow>(
            new CommandDefinition(Sql, new { status, limit, offset },
                commandTimeout: _commandTimeout, cancellationToken: ct)).ConfigureAwait(false);

        var list = rows.ToList();
        var total = list.Count > 0 ? list[0].Total : 0;

        return new DeviceImageCandidatePage(total, [.. list.Select(Materialise)]);
    }

    /// <inheritdoc />
    public async Task<DeviceImage?> GetImageAsync(long id, CancellationToken ct)
    {
        const string Sql = """
            SELECT content_type AS ContentType, bytes AS Bytes, sha256 AS Sha256,
                   created_at   AS UpdatedAt
            FROM catalog.device_image_candidate
            WHERE id = @id
            """;

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var row = await connection.QuerySingleOrDefaultAsync<ImageBytes>(
            new CommandDefinition(Sql, new { id }, commandTimeout: _commandTimeout,
                cancellationToken: ct)).ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        var tag = "\"c" + Convert.ToHexString(row.Sha256.AsSpan(0, 8))
            .ToLower(CultureInfo.InvariantCulture) + "\"";

        return new DeviceImage(id.ToString(CultureInfo.InvariantCulture),
            row.ContentType, row.Bytes, tag, row.UpdatedAt);
    }

    /// <inheritdoc />
    public async Task<bool> ApproveAsync(long id, long userId, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        // Promote and mark in one transaction. Only a candidate still awaiting review can be
        // approved, so a second click cannot re-run the promotion.
        const string Promote = """
            INSERT INTO catalog.device_model_image
                (model_key, brand, marketing_name, content_type, bytes, sha256, source_note,
                 uploaded_by, uploaded_at, updated_at,
                 status, source_type, source_domain, source_url,
                 original_width, original_height, quality_score, verified_by, verified_at)
            SELECT c.model_key, c.brand, c.marketing_name, c.content_type, c.bytes, c.sha256,
                   c.source_type || ': ' || c.source_url,
                   @userId, now(), now(),
                   'verified', c.source_type, c.source_domain, c.source_url,
                   c.original_width, c.original_height, c.quality_score, @userId, now()
            FROM catalog.device_image_candidate AS c
            WHERE c.id = @id AND c.status = 'needs_review'
            ON CONFLICT (model_key) DO UPDATE SET
                brand          = EXCLUDED.brand,
                marketing_name = EXCLUDED.marketing_name,
                content_type   = EXCLUDED.content_type,
                bytes          = EXCLUDED.bytes,
                sha256         = EXCLUDED.sha256,
                source_note    = EXCLUDED.source_note,
                uploaded_by    = EXCLUDED.uploaded_by,
                updated_at     = now(),
                status         = 'verified',
                source_type    = EXCLUDED.source_type,
                source_domain  = EXCLUDED.source_domain,
                source_url     = EXCLUDED.source_url,
                original_width = EXCLUDED.original_width,
                original_height= EXCLUDED.original_height,
                quality_score  = EXCLUDED.quality_score,
                verified_by    = EXCLUDED.verified_by,
                verified_at    = now()
            """;

        var promoted = await connection.ExecuteAsync(new CommandDefinition(
            Promote, new { id, userId }, transaction, _commandTimeout, cancellationToken: ct))
            .ConfigureAwait(false);

        if (promoted == 0)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }

        const string Mark = """
            UPDATE catalog.device_image_candidate
               SET status = 'approved', reviewed_by = @userId, reviewed_at = now()
             WHERE id = @id AND status = 'needs_review'
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            Mark, new { id, userId }, transaction, _commandTimeout, cancellationToken: ct))
            .ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RejectAsync(long id, long userId, string? reason, CancellationToken ct)
    {
        // The row is kept rather than deleted. Its (model_key, sha256) is what stops the sourcing
        // tool proposing the same bytes again, so deleting a rejection would guarantee it comes
        // straight back on the next run.
        const string Sql = """
            UPDATE catalog.device_image_candidate
               SET status = 'rejected', reviewed_by = @userId, reviewed_at = now(),
                   rejection_reason = nullif(trim(coalesce(@reason, '')), '')
             WHERE id = @id AND status = 'needs_review'
            """;

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            Sql, new { id, userId, reason }, commandTimeout: _commandTimeout,
            cancellationToken: ct)).ConfigureAwait(false);

        return affected > 0;
    }

    private static DeviceImageCandidate Materialise(CandidateRow row)
    {
        IReadOnlyList<ScoreTerm> breakdown = [];

        if (!string.IsNullOrWhiteSpace(row.ScoreBreakdownJson))
        {
            try
            {
                breakdown = JsonSerializer.Deserialize<List<ScoreTerm>>(
                    row.ScoreBreakdownJson, BreakdownJson) ?? [];
            }
            catch (JsonException)
            {
                // A breakdown that cannot be read is not a reason to hide the candidate: the
                // reviewer can still see the image, the source and the total.
                breakdown = [];
            }
        }

        return new DeviceImageCandidate(
            row.Id, row.ModelKey, row.Brand, row.MarketingName, row.Status, row.ContentType,
            row.ByteSize, row.SourceType, row.SourceDomain, row.SourceUrl,
            row.OriginalWidth, row.OriginalHeight, row.QualityScore, breakdown,
            row.RejectionReason, row.CreatedAt, row.CurrentStatus);
    }

    /// <summary>
    /// Settable properties, not a positional record.
    /// </summary>
    /// <remarks>
    /// Dapper strips underscores when mapping to properties and does not when matching a
    /// constructor, and it matches a record's constructor by the reader's field ORDER - so a
    /// column added mid-SELECT breaks materialisation with a message about a missing constructor.
    /// This project has lost an hour to that twice.
    /// </remarks>
    private sealed class CandidateRow
    {
        public long Id { get; set; }

        public string ModelKey { get; set; } = string.Empty;

        public string Brand { get; set; } = string.Empty;

        public string MarketingName { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string ContentType { get; set; } = string.Empty;

        public int ByteSize { get; set; }

        public string SourceType { get; set; } = string.Empty;

        public string SourceDomain { get; set; } = string.Empty;

        public string SourceUrl { get; set; } = string.Empty;

        public int OriginalWidth { get; set; }

        public int OriginalHeight { get; set; }

        public int QualityScore { get; set; }

        public string? ScoreBreakdownJson { get; set; }

        public string? RejectionReason { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public string CurrentStatus { get; set; } = string.Empty;

        public int Total { get; set; }
    }

    private sealed class ImageBytes
    {
        public string ContentType { get; set; } = string.Empty;

        public byte[] Bytes { get; set; } = [];

        public byte[] Sha256 { get; set; } = [];

        public DateTimeOffset UpdatedAt { get; set; }
    }
}
