using System.Globalization;
using Sqm.Api.Auth;
using Sqm.Application.Abstractions;
using Sqm.Application.Identity;
using Sqm.Contracts.Devices;

namespace Sqm.Api.Endpoints;

/// <summary>
/// Reviewing images the sourcing pipeline has proposed.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only route by which an automatically sourced image reaches the product.</b> The
/// pipeline writes candidates and cannot write a live image; promotion happens here, when a
/// person holding <c>device.image.manage</c> approves one. Until then whatever is on screen stays
/// on screen - including nothing, which is a better answer than a confident picture of the wrong
/// phone.
/// </para>
/// <para>
/// The same permission as manual upload, deliberately. Approving a sourced image and uploading
/// one are the same act with the same consequence: a picture appears on every device page for
/// that model. Two permissions would imply a difference in risk that does not exist.
/// </para>
/// </remarks>
public static class DeviceImageReviewEndpoints
{
    /// <summary>Largest page of live images the server will return.</summary>
    private const int MaxPageSize = 60;

    /// <summary>
    /// Largest page of candidates. Larger than the live list because a reviewer selects and
    /// approves a page at a time, and a package brings a couple of thousand candidates at once.
    /// </summary>
    private const int MaxCandidatePageSize = 96;

    /// <summary>The candidate page when the client does not ask for a size.</summary>
    private const int DefaultCandidatePageSize = 48;

    /// <summary>
    /// Most candidates one batch approval will take. Each is its own transaction, so the bound is
    /// on how long one request holds a reviewer's browser waiting, not on correctness.
    /// </summary>
    private const int MaxBatch = 100;

    /// <summary>Registers the review routes.</summary>
    public static IEndpointRouteBuilder MapDeviceImageReviewEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/devices/image-candidates")
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.DeviceImageManage)
            .WithTags("Devices");

        group.MapGet("/", ListAsync)
            .WithName("ListDeviceImageCandidates")
            .WithSummary("Proposed device images, filtered, most-carried models first.");

        group.MapGet("/facets", FacetsAsync)
            .WithName("GetDeviceImageCandidateFacets")
            .WithSummary("How many candidates in a status each review filter would show.");

        // The LIVE image for a model, so the review screen can put current and candidate side by
        // side. The ordinary device image route is keyed by TAC, which a reviewer looking at a
        // model does not have - and picking an arbitrary TAC of the model to ask with would be a
        // detour through data this page has no other use for.
        group.MapGet("/current/{modelKey}", CurrentAsync)
            .WithName("GetLiveImageByModel")
            .WithSummary("The live image for one model, for comparison against a candidate.");

        group.MapGet("/{id:long}/image", ImageAsync)
            .WithName("GetDeviceImageCandidate")
            .WithSummary("The candidate's own bytes, for comparison against the live image.");

        group.MapPost("/{id:long}/approve", ApproveAsync)
            .WithName("ApproveDeviceImageCandidate")
            .WithSummary("Promote a candidate to the live image for its model, marked verified.");

        // A literal segment, so it cannot be mistaken for /{id:long}/approve: that route has two
        // segments and this one has one.
        group.MapPost("/approve", ApproveManyAsync)
            .WithName("ApproveDeviceImageCandidates")
            .WithSummary("Promote several candidates, each in its own transaction, one per model.");

        group.MapPost("/{id:long}/reject", RejectAsync)
            .WithName("RejectDeviceImageCandidate")
            .WithSummary("Turn a candidate down. It will not be proposed again.");

        // ------------------------------------------------------------------ live images
        //
        // The other half of the same job. Flagging the 84 automatically sourced images as
        // unreviewed was not the same as giving anybody a way to act on them: the queue above
        // only ever shows PROPOSALS, and for an image with no proposal there was nothing to do
        // but visit each device page in turn.
        var live = app.MapGroup("/api/v1/devices/images")
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.DeviceImageManage)
            .WithTags("Devices");

        live.MapGet("/", ListLiveAsync)
            .WithName("ListDeviceImages")
            .WithSummary("Images currently being served, unverified first.");

        live.MapPost("/{modelKey}/verify", VerifyAsync)
            .WithName("VerifyDeviceImage")
            .WithSummary("Record that a person has checked this image. The image is unchanged.");

        live.MapDelete("/{modelKey}", RemoveAsync)
            .WithName("RemoveDeviceImage")
            .WithSummary("Remove the image for one model. The device falls back to the placeholder.");

        return app;
    }

    private static async Task<IResult> ListAsync(
        IDeviceImageCandidateStore store,
        CancellationToken ct,
        string? status = null,
        string? brand = null,
        string? sourceType = null,
        string? onNetwork = null,
        string? warnings = null,
        string? sort = null,
        int page = 1,
        int pageSize = DefaultCandidatePageSize)
    {
        ArgumentNullException.ThrowIfNull(store);

        var size = Math.Clamp(pageSize, 1, MaxCandidatePageSize);
        // In long arithmetic and clamped: page=44739245 overflowed int to a negative OFFSET and a 500.
        var offset = (int)Math.Min((Math.Max(1L, page) - 1) * size, int.MaxValue);

        var query = new DeviceImageCandidateQuery(
            Status: CandidateStatus(status),
            Brand: string.IsNullOrWhiteSpace(brand) ? null : brand.Trim(),
            SourceType: string.IsNullOrWhiteSpace(sourceType) ? null : sourceType.Trim(),
            OnNetwork: onNetwork?.Trim().ToLowerInvariant() switch
            {
                "true" => true,
                "false" => false,
                _ => null,
            },
            Warnings: warnings?.Trim().ToLowerInvariant() switch
            {
                "with" => "with",
                "without" => "without",
                "high" => "high",
                _ => "any",
            },
            Sort: string.Equals(sort?.Trim(), "score", StringComparison.OrdinalIgnoreCase)
                ? "score"
                : "bindings",
            Limit: size,
            Offset: offset);

        var result = await store.ListAsync(query, ct).ConfigureAwait(false);
        return Results.Ok(result);
    }

    private static async Task<IResult> FacetsAsync(
        IDeviceImageCandidateStore store, CancellationToken ct, string? status = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        var facets = await store.GetFacetsAsync(CandidateStatus(status), ct).ConfigureAwait(false);
        return Results.Ok(facets);
    }

    /// <summary>A status from the query string, reduced to its closed set; anything else is the queue.</summary>
    private static string CandidateStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "approved" => "approved",
        "rejected" => "rejected",
        "failed" => "failed",
        "all" => "all",
        _ => "needs_review",
    };

    /// <remarks>
    /// Served from our own origin like every other image here, and never as a link to the source.
    /// Hotlinking would put an unreviewed third-party URL in front of a reviewer and make the
    /// decision depend on whatever that host served at that moment.
    /// </remarks>
    private static async Task<IResult> ImageAsync(
        long id, HttpContext http, IDeviceImageCandidateStore store, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(http);

        var image = await store.GetImageAsync(id, ct).ConfigureAwait(false);

        if (image is null)
        {
            return Results.NotFound();
        }

        if (http.Request.Headers.IfNoneMatch.Count > 0
            && http.Request.Headers.IfNoneMatch.ToString()
                .Contains(image.ETag, StringComparison.Ordinal))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        http.Response.Headers.ETag = image.ETag;

        // Short and private: a candidate is short-lived by design and may be approved or rejected
        // at any moment, so a long cache would show a reviewer something that no longer exists.
        http.Response.Headers.CacheControl = "private, max-age=60, must-revalidate";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";

        return Results.Bytes(image.Bytes, image.ContentType);
    }

    /// <summary>A model key as it arrived in the path, with its slashes restored.</summary>
    /// <remarks>
    /// GSMA marketing names carry slashes - "redmi note 8/8t" - and so do the keys built from them.
    /// The SPA escapes the key, and routing decodes every escape in a route value except %2F,
    /// which it leaves as it is so that it cannot be mistaken for a path separator. The key then
    /// matched no image, and Verify, Remove and Current answered 404 for every such model. Only
    /// %2F is decoded here: routing has already handled the rest, and decoding again could turn
    /// a literal "%" in a name into something else.
    /// </remarks>
    private static string FromRoute(string modelKey) =>
        modelKey.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);

    private static async Task<IResult> CurrentAsync(
        string modelKey, HttpContext http, IDeviceImageStore images, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(http);

        modelKey = FromRoute(modelKey);

        var image = await images.GetAsync(modelKey, ct).ConfigureAwait(false);

        if (image is null)
        {
            return Results.NotFound();
        }

        http.Response.Headers.ETag = image.ETag;
        http.Response.Headers.CacheControl = "private, max-age=60, must-revalidate";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";

        return Results.Bytes(image.Bytes, image.ContentType);
    }

    private static async Task<IResult> ApproveAsync(
        long id, HttpContext http, IDeviceImageCandidateStore store, IAuditLog audit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);

        var user = CurrentUser.Require(http);
        var promoted = await store.ApproveAsync(id, user.UserId, ct).ConfigureAwait(false);

        // Every audit entry after a change is written with no cancellation: the change has already
        // committed, and a reviewer closing the tab at that moment must not leave a live image (or a
        // removal, or a rejection) that the audit log never heard of. The request token still stops
        // the work that has not started - the next candidate of a batch.
        await audit.WriteAsync(Entry(user, http, AuditOutcome.Success, new Dictionary<string, object?>
        {
            ["candidate"] = id,
            ["action"] = "approve",
            ["result"] = promoted ? "promoted" : "not awaiting review",
        }), CancellationToken.None).ConfigureAwait(false);

        return promoted
            ? Results.Ok(new { id, status = "approved" })
            : Results.Conflict(new
            {
                id,
                message = "That candidate is no longer awaiting review - somebody has already "
                          + "decided it.",
            });
    }

    /// <remarks>
    /// <para>
    /// Each candidate goes through the same single-candidate transaction as the one-at-a-time
    /// route, in the order given. A batch is a convenience for the reviewer, not a new kind of
    /// decision: a candidate somebody else decided in the meantime is reported and skipped, the
    /// rest are approved, exactly as separate clicks would have done, and the answer says which
    /// were which. One audit entry per candidate, so the trail reads the same either way.
    /// </para>
    /// <para>
    /// Two candidates for one model are refused before anything is approved. Both would promote,
    /// and the live image would be whichever ran second - a choice made by the order of a list,
    /// not by the reviewer.
    /// </para>
    /// </remarks>
    private static async Task<IResult> ApproveManyAsync(
        ApproveCandidatesRequest? request, HttpContext http, IDeviceImageCandidateStore store,
        IAuditLog audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(audit);

        var ids = request?.Ids ?? [];

        if (BatchProblem(ids) is { } problem)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["ids"] = [problem] });
        }

        var models = await store.GetModelsAsync(ids, ct).ConfigureAwait(false);
        var clashes = models
            .GroupBy(m => m.ModelKey, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToList();

        if (clashes.Count > 0)
        {
            var named = string.Join("; ", clashes.Select(g =>
                g.First().Brand + " " + g.First().MarketingName + " (candidates "
                + string.Join(", ", g.Select(m => m.Id.ToString(CultureInfo.InvariantCulture)))
                + ")"));

            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Two images for one model",
                detail: "Only one image can be approved for a model, and some of these candidates "
                        + "are for the same model: " + named + ". Nothing was approved.");
        }

        var user = CurrentUser.Require(http);
        var approved = new List<long>(ids.Count);
        var notAwaitingReview = new List<long>();

        foreach (var id in ids)
        {
            var promoted = await store.ApproveAsync(id, user.UserId, ct).ConfigureAwait(false);
            (promoted ? approved : notAwaitingReview).Add(id);

            await audit.WriteAsync(Entry(user, http, AuditOutcome.Success, new Dictionary<string, object?>
            {
                ["candidate"] = id,
                ["action"] = "approve",
                ["result"] = promoted ? "promoted" : "not awaiting review",
                ["batch"] = ids.Count,
            }), CancellationToken.None).ConfigureAwait(false);
        }

        return Results.Ok(new ApproveCandidatesResult(approved, notAwaitingReview));
    }

    /// <summary>Why a batch cannot be taken as it is, or null when it can.</summary>
    private static string? BatchProblem(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0)
        {
            return "Name at least one candidate to approve.";
        }

        if (ids.Count > MaxBatch)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"At most {MaxBatch} candidates can be approved at once; {ids.Count} were sent.");
        }

        return ids.Distinct().Count() != ids.Count
            ? "A candidate is named more than once."
            : null;
    }

    private static async Task<IResult> RejectAsync(
        long id, RejectCandidateRequest? request, HttpContext http,
        IDeviceImageCandidateStore store, IAuditLog audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);

        var user = CurrentUser.Require(http);
        var reason = request?.Reason?.Trim();

        var rejected = await store.RejectAsync(id, user.UserId, reason, ct).ConfigureAwait(false);

        await audit.WriteAsync(Entry(user, http, AuditOutcome.Success, new Dictionary<string, object?>
        {
            ["candidate"] = id,
            ["action"] = "reject",
            ["reason"] = reason,
            ["result"] = rejected ? "rejected" : "not awaiting review",
        }), CancellationToken.None).ConfigureAwait(false);

        return rejected
            ? Results.Ok(new { id, status = "rejected" })
            : Results.Conflict(new { id, message = "That candidate is no longer awaiting review." });
    }

    private static async Task<IResult> ListLiveAsync(
        IDeviceImageStore images,
        CancellationToken ct,
        string? status = null,
        int page = 1,
        int pageSize = 24)
    {
        ArgumentNullException.ThrowIfNull(images);

        var wanted = status?.ToLowerInvariant() switch
        {
            "verified" => "verified",
            "all" => "all",
            _ => "needs_review",
        };

        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var result = await images
            .ListAsync(wanted, size, (int)Math.Min(Math.Max(0L, page - 1L) * size, int.MaxValue), ct).ConfigureAwait(false);

        return Results.Ok(result);
    }

    private static async Task<IResult> VerifyAsync(
        string modelKey, HttpContext http, IDeviceImageStore images, IAuditLog audit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(images);

        modelKey = FromRoute(modelKey);

        var user = CurrentUser.Require(http);
        var verified = await images.VerifyAsync(modelKey, user.UserId, ct).ConfigureAwait(false);

        await audit.WriteAsync(Entry(user, http, AuditOutcome.Success, new Dictionary<string, object?>
        {
            ["model"] = modelKey,
            ["action"] = "verify",
            ["result"] = verified ? "verified" : "no image for that model",
        }), CancellationToken.None).ConfigureAwait(false);

        return verified
            ? Results.Ok(new { modelKey, status = "verified" })
            : Results.NotFound(new { modelKey, message = "No image is stored for that model." });
    }

    private static async Task<IResult> RemoveAsync(
        string modelKey, HttpContext http, IDeviceImageStore images, IAuditLog audit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(images);

        modelKey = FromRoute(modelKey);

        var user = CurrentUser.Require(http);
        var removed = await images.DeleteAsync(modelKey, ct).ConfigureAwait(false);

        await audit.WriteAsync(Entry(user, http, AuditOutcome.Success, new Dictionary<string, object?>
        {
            ["model"] = modelKey,
            ["action"] = "remove",
            ["result"] = removed ? "removed" : "no image for that model",
        }), CancellationToken.None).ConfigureAwait(false);

        return removed ? Results.NoContent() : Results.NotFound();
    }

    private static AuditEntry Entry(
        AuthenticatedUser user, HttpContext http, AuditOutcome outcome,
        Dictionary<string, object?> detail) =>
        new(
            ActorName: user.Username,
            Action: Permissions.DeviceImageManage,
            Category: AuditCategory.Data,
            Outcome: outcome,
            ActorUserId: user.UserId,
            TargetType: "device-image-candidate",
            Ip: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            CorrelationId: http.TraceIdentifier,
            Detail: detail);
}
