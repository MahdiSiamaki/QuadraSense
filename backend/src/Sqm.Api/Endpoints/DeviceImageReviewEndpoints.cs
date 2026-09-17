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
    /// <summary>Largest page of candidates the server will return.</summary>
    private const int MaxPageSize = 60;

    /// <summary>Registers the review routes.</summary>
    public static IEndpointRouteBuilder MapDeviceImageReviewEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/devices/image-candidates")
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.DeviceImageManage)
            .WithTags("Devices");

        group.MapGet("/", ListAsync)
            .WithName("ListDeviceImageCandidates")
            .WithSummary("Proposed device images awaiting review, highest score first.");

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
        int page = 1,
        int pageSize = 24)
    {
        ArgumentNullException.ThrowIfNull(store);

        var wanted = status?.ToLowerInvariant() switch
        {
            "approved" => "approved",
            "rejected" => "rejected",
            "failed" => "failed",
            "all" => "all",
            _ => "needs_review",
        };

        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var offset = Math.Max(0, page - 1) * size;

        var result = await store.ListAsync(wanted, size, offset, ct).ConfigureAwait(false);
        return Results.Ok(result);
    }

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

    private static async Task<IResult> CurrentAsync(
        string modelKey, HttpContext http, IDeviceImageStore images, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(http);

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

        await audit.WriteAsync(Entry(user, http, AuditOutcome.Success, new Dictionary<string, object?>
        {
            ["candidate"] = id,
            ["action"] = "approve",
            ["result"] = promoted ? "promoted" : "not awaiting review",
        }), ct).ConfigureAwait(false);

        return promoted
            ? Results.Ok(new { id, status = "approved" })
            : Results.Conflict(new
            {
                id,
                message = "That candidate is no longer awaiting review - somebody has already "
                          + "decided it.",
            });
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
        }), ct).ConfigureAwait(false);

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
            .ListAsync(wanted, size, Math.Max(0, page - 1) * size, ct).ConfigureAwait(false);

        return Results.Ok(result);
    }

    private static async Task<IResult> VerifyAsync(
        string modelKey, HttpContext http, IDeviceImageStore images, IAuditLog audit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(images);

        var user = CurrentUser.Require(http);
        var verified = await images.VerifyAsync(modelKey, user.UserId, ct).ConfigureAwait(false);

        await audit.WriteAsync(Entry(user, http, AuditOutcome.Success, new Dictionary<string, object?>
        {
            ["model"] = modelKey,
            ["action"] = "verify",
            ["result"] = verified ? "verified" : "no image for that model",
        }), ct).ConfigureAwait(false);

        return verified
            ? Results.Ok(new { modelKey, status = "verified" })
            : Results.NotFound(new { modelKey, message = "No image is stored for that model." });
    }

    private static async Task<IResult> RemoveAsync(
        string modelKey, HttpContext http, IDeviceImageStore images, IAuditLog audit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(images);

        var user = CurrentUser.Require(http);
        var removed = await images.DeleteAsync(modelKey, ct).ConfigureAwait(false);

        await audit.WriteAsync(Entry(user, http, AuditOutcome.Success, new Dictionary<string, object?>
        {
            ["model"] = modelKey,
            ["action"] = "remove",
            ["result"] = removed ? "removed" : "no image for that model",
        }), ct).ConfigureAwait(false);

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
