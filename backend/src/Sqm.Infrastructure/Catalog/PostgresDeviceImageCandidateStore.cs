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

    /// <summary>
    /// The review-queue filter, shared by the page and by its fallback count so they cannot
    /// disagree about which candidates match.
    /// </summary>
    /// <remarks>
    /// A null parameter means "no filter". <c>@&gt;</c> rather than a JSON path query because
    /// containment reads as what it means - some warning has severity high - and needs no
    /// knowledge of jsonpath to check.
    /// </remarks>
    private const string Filter = """
        (@status = 'all' OR c.status = @status)
          AND (CAST(@brand AS text) IS NULL OR lower(c.brand) = lower(CAST(@brand AS text)))
          AND (CAST(@sourceType AS text) IS NULL OR c.source_type = CAST(@sourceType AS text))
          AND (CAST(@onNetwork AS boolean) IS NULL OR (c.bindings > 0) = CAST(@onNetwork AS boolean))
          AND (@warnings = 'any'
               OR (@warnings = 'with'    AND c.warnings <> '[]'::jsonb)
               OR (@warnings = 'without' AND c.warnings =  '[]'::jsonb)
               OR (@warnings = 'high'    AND c.warnings @> '[{"severity":"high"}]'::jsonb))
        """;

    /// <summary>Most-carried models first; the id makes the order total, so pages cannot overlap.</summary>
    private const string OrderByBindings = "c.bindings DESC, c.quality_score DESC, c.id";

    /// <summary>Highest score first, oldest first among equals, then the id for a total order.</summary>
    private const string OrderByScore = "c.quality_score DESC, c.created_at, c.id";

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
        DeviceImageCandidateQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        // The live image's status is joined in because a reviewer's first question is whether
        // they are filling a gap or overruling somebody. count() OVER () so the total and the
        // page come from one pass and cannot disagree.
        //
        // The ORDER BY is one of two constants, never text from the request: the endpoint has
        // already reduced the sort to its closed set, and a parameter cannot name a column.
        var order = query.Sort == "score" ? OrderByScore : OrderByBindings;

        var sql = $"""
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
                   c.bindings                        AS Bindings,
                   c.warnings::text                  AS WarningsJson,
                   c.evidence::text                  AS EvidenceJson,
                   c.package                         AS Package,
                   count(*) OVER ()                  AS Total
            FROM catalog.device_image_candidate AS c
            LEFT JOIN catalog.device_model_image AS i ON i.model_key = c.model_key
            WHERE {Filter}
            ORDER BY {order}
            LIMIT @limit OFFSET @offset
            """;

        var args = new
        {
            status = query.Status,
            brand = string.IsNullOrWhiteSpace(query.Brand) ? null : query.Brand.Trim(),
            sourceType = string.IsNullOrWhiteSpace(query.SourceType) ? null : query.SourceType.Trim(),
            onNetwork = query.OnNetwork,
            warnings = query.Warnings,
            limit = query.Limit,
            offset = query.Offset,
        };

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var rows = await connection.QueryAsync<CandidateRow>(
            new CommandDefinition(sql, args, commandTimeout: _commandTimeout,
                cancellationToken: ct)).ConfigureAwait(false);

        var list = rows.ToList();
        var total = list.Count > 0 ? list[0].Total : 0;

        // A page past the end carries no row to read the total from. That is the ordinary state
        // after a reviewer approves the whole of the last page, and answering "0" there would tell
        // them the queue is empty when it is not.
        if (list.Count == 0 && query.Offset > 0)
        {
            total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                $"SELECT count(*) FROM catalog.device_image_candidate AS c WHERE {Filter}", args,
                commandTimeout: _commandTimeout, cancellationToken: ct)).ConfigureAwait(false);
        }

        return new DeviceImageCandidatePage(total, [.. list.Select(Materialise)]);
    }

    /// <inheritdoc />
    public async Task<DeviceImageCandidateFacets> GetFacetsAsync(string status, CancellationToken ct)
    {
        // Brands are grouped without regard to case because the filter compares without regard
        // to case: a facet that listed "Samsung" and "SAMSUNG" separately would promise two
        // different answers and then give the same one for both.
        const string Sql = """
            SELECT count(*)                                                        AS Total,
                   count(*) FILTER (WHERE c.bindings > 0)                          AS OnNetwork,
                   count(*) FILTER (WHERE c.warnings <> '[]'::jsonb)               AS WithWarnings,
                   count(*) FILTER (WHERE c.warnings @> '[{"severity":"high"}]'::jsonb) AS HighWarnings
            FROM catalog.device_image_candidate AS c
            WHERE (@status = 'all' OR c.status = @status);

            SELECT min(c.brand) AS Value, count(*) AS Count
            FROM catalog.device_image_candidate AS c
            WHERE (@status = 'all' OR c.status = @status)
            GROUP BY lower(c.brand)
            ORDER BY count(*) DESC, min(c.brand);

            SELECT c.source_type AS Value, count(*) AS Count
            FROM catalog.device_image_candidate AS c
            WHERE (@status = 'all' OR c.status = @status)
            GROUP BY c.source_type
            ORDER BY count(*) DESC, c.source_type;
            """;

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        await using var reader = await connection.QueryMultipleAsync(new CommandDefinition(
            Sql, new { status }, commandTimeout: _commandTimeout, cancellationToken: ct))
            .ConfigureAwait(false);

        var counts = await reader.ReadSingleAsync<FacetTotalsRow>().ConfigureAwait(false);
        var brands = await reader.ReadAsync<FacetValueRow>().ConfigureAwait(false);
        var sources = await reader.ReadAsync<FacetValueRow>().ConfigureAwait(false);

        return new DeviceImageCandidateFacets(
            counts.Total, counts.OnNetwork, counts.WithWarnings, counts.HighWarnings,
            [.. brands.Select(b => new CandidateFacetValue(b.Value, b.Count))],
            [.. sources.Select(s => new CandidateFacetValue(s.Value, s.Count))]);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CandidateModel>> GetModelsAsync(
        IReadOnlyCollection<long> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return [];
        }

        const string Sql = """
            SELECT id AS Id, model_key AS ModelKey, brand AS Brand, marketing_name AS MarketingName
            FROM catalog.device_image_candidate
            WHERE id = ANY(@ids)
            ORDER BY id
            """;

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var rows = await connection.QueryAsync<ModelRow>(new CommandDefinition(
            Sql, new { ids = ids.Distinct().ToArray() }, commandTimeout: _commandTimeout,
            cancellationToken: ct)).ConfigureAwait(false);

        return [.. rows.Select(r => new CandidateModel(r.Id, r.ModelKey, r.Brand, r.MarketingName))];
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

        // Mark first, then promote. The conditional UPDATE locks the candidate row, so a reject
        // arriving at the same moment waits for this transaction and then matches nothing - and
        // a mark that matched nothing means someone else decided first, so nothing is promoted.
        //
        // The other order let a rejection win the race and still lose the image: the promotion
        // read the candidate without locking it, a reject committed in between, the mark then
        // updated no row, and the transaction committed anyway - a rejected candidate live in the
        // catalogue, both reviewers told they had succeeded. ADR-011's one rule, broken.
        const string Mark = """
            UPDATE catalog.device_image_candidate
               SET status = 'approved', reviewed_by = @userId, reviewed_at = now()
             WHERE id = @id AND status = 'needs_review'
            """;

        var marked = await connection.ExecuteAsync(new CommandDefinition(
            Mark, new { id, userId }, transaction, _commandTimeout, cancellationToken: ct))
            .ConfigureAwait(false);

        if (marked == 0)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }

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
            WHERE c.id = @id
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

        await connection.ExecuteAsync(new CommandDefinition(
            Promote, new { id, userId }, transaction, _commandTimeout, cancellationToken: ct))
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
            row.RejectionReason, row.CreatedAt, row.CurrentStatus,
            row.Bindings, ReadWarnings(row.WarningsJson), ReadEvidence(row.EvidenceJson),
            row.Package);
    }

    /// <summary>
    /// The candidate's warnings, element by element.
    /// </summary>
    /// <remarks>
    /// Read by hand rather than deserialised, so that one malformed element costs that element and
    /// not the list - or, worse, the candidate. A warning without a code says nothing a reviewer
    /// can act on and is dropped; a severity other than <c>high</c> is shown as <c>info</c>, so
    /// an unknown value can never make a warning look more alarming than the importer meant.
    /// </remarks>
    private static List<CandidateWarning> ReadWarnings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var warnings = new List<CandidateWarning>();

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object
                    || Text(element, "code") is not { Length: > 0 } code)
                {
                    continue;
                }

                var severity = string.Equals(Text(element, "severity"), "high", StringComparison.Ordinal)
                    ? "high"
                    : "info";

                warnings.Add(new CandidateWarning(code, severity, Text(element, "detail") ?? string.Empty));
            }

            return warnings;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The package evidence, member by member, or null when there is none to show.
    /// </summary>
    /// <remarks>
    /// Each member is read on its own, and one of the wrong type is null rather than an exception:
    /// a reviewer missing the product name because the TAC count was written as a string would be
    /// worse off than one seeing everything but the count.
    /// </remarks>
    private static CandidateEvidence? ReadEvidence(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var o = document.RootElement;

            if (o.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var evidence = new CandidateEvidence(
                Package: Text(o, "package"),
                ProductName: Text(o, "productName"),
                ProductId: Text(o, "productId"),
                MatchMethods: Texts(o, "matchMethods"),
                MappedTacs: Whole(o, "mappedTacs"),
                ModelTacs: Whole(o, "modelTacs"),
                MappedBindings: Whole(o, "mappedBindings"),
                ModelBindings: Whole(o, "modelBindings"),
                SourceKind: Text(o, "sourceKind"),
                SourcePage: Text(o, "sourcePage"),
                ImageUrl: Text(o, "imageUrl"),
                IdentitySource: Text(o, "identitySource"),
                MatchScope: Text(o, "matchScope"),
                QaStatus: Text(o, "qaStatus"),
                PackageFile: Text(o, "packageFile"),
                Upscale: Number(o, "upscale"),
                OriginalSha256Ok: Flag(o, "originalSha256Ok"));

            // An object with nothing readable in it is no evidence, and the contract says so with
            // null rather than with a record of nulls the client would have to inspect.
            return IsEmpty(evidence) ? null : evidence;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsEmpty(CandidateEvidence e) =>
        e is
        {
            Package: null, ProductName: null, ProductId: null, MatchMethods: null,
            MappedTacs: null, ModelTacs: null, MappedBindings: null, ModelBindings: null,
            SourceKind: null, SourcePage: null, ImageUrl: null, IdentitySource: null,
            MatchScope: null, QaStatus: null, PackageFile: null, Upscale: null,
            OriginalSha256Ok: null,
        };

    /// <summary>A string member; a number is given as written, anything else is null.</summary>
    private static string? Text(JsonElement o, string name) =>
        !o.TryGetProperty(name, out var v) ? null : v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };

    /// <summary>A list of strings; a lone string is a list of one, other elements are skipped.</summary>
    private static List<string>? Texts(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.String => [v.GetString()!],
            JsonValueKind.Array =>
            [
                .. v.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!),
            ],
            _ => null,
        };
    }

    /// <summary>A whole number; 12.0 counts, 12.5 and "12" do not.</summary>
    private static long? Whole(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        if (v.TryGetInt64(out var n))
        {
            return n;
        }

        return v.TryGetDouble(out var d) && d == Math.Floor(d) && Math.Abs(d) < 9e15 ? (long)d : null;
    }

    private static double? Number(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetDouble(out var d)
            ? d
            : null;

    private static bool? Flag(JsonElement o, string name) =>
        !o.TryGetProperty(name, out var v) ? null : v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };

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

        public long Bindings { get; set; }

        public string? WarningsJson { get; set; }

        public string? EvidenceJson { get; set; }

        public string? Package { get; set; }

        public int Total { get; set; }
    }

    private sealed class FacetTotalsRow
    {
        public int Total { get; set; }

        public int OnNetwork { get; set; }

        public int WithWarnings { get; set; }

        public int HighWarnings { get; set; }
    }

    private sealed class FacetValueRow
    {
        public string Value { get; set; } = string.Empty;

        public int Count { get; set; }
    }

    private sealed class ModelRow
    {
        public long Id { get; set; }

        public string ModelKey { get; set; } = string.Empty;

        public string Brand { get; set; } = string.Empty;

        public string MarketingName { get; set; } = string.Empty;
    }

    private sealed class ImageBytes
    {
        public string ContentType { get; set; } = string.Empty;

        public byte[] Bytes { get; set; } = [];

        public byte[] Sha256 { get; set; } = [];

        public DateTimeOffset UpdatedAt { get; set; }
    }
}
