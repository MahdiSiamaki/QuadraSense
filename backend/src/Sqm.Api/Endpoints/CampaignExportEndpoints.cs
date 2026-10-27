using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using Sqm.Api.Auth;
using Sqm.Api.Infrastructure;
using Sqm.Application.Campaigns;
using Sqm.Application.Identity;
using Sqm.Application.Planning;
using Sqm.Contracts.Campaigns;
using Sqm.Domain.Identifiers;
using Sqm.Infrastructure.ClickHouse;

namespace Sqm.Api.Endpoints;

/// <summary>The controlled export of campaign lists: request with a reason, a second person's approval, download.</summary>
/// <remarks>
/// <para>
/// <b>The product owner's decision of 2026-10-21.</b> Subscriber lists for campaigns are allowed, with
/// controls: a permission of their own (<c>campaign.export</c>, with <c>lookup.subscriber</c>), a
/// recorded reason, and a second person's approval (<c>campaign.approve</c>, never one's own request)
/// above a size threshold (<c>CampaignExport:ApprovalThreshold</c>; 0, the default, means every list).
/// </para>
/// <para>
/// <b>What is approved is what leaves.</b> A request records the class and filters of one planning run
/// and the count it found; the download reads that run, not a newer one, and refuses once the run no
/// longer holds its numbers. Every step is audited with the request's id, count and reason - never a
/// number.
/// </para>
/// </remarks>
public static class CampaignExportEndpoints
{
    /// <summary>Registers the campaign export routes.</summary>
    public static IEndpointRouteBuilder MapCampaignExportEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var builder = app.MapGroup("/api/v1/campaign-exports").WithTags("Campaign exports").RequireAuthorization();

        builder.MapGet("/", ListAsync).WithName("ListCampaignExports")
            .WithSummary("One's own requests, or everybody's for an approver, and the threshold in force.");
        builder.MapPost("/", CreateAsync).WithName("RequestCampaignExport")
            .WithSummary("Asks for a campaign list of one planning class, with a reason. Approved at once at or below the threshold.");
        builder.MapPost("/{id:long}/approve", (long id, CampaignExportDecision body, HttpContext http, ICampaignExportStore store,
                IOptionsMonitor<CampaignExportOptions> options, IAuditLog audit, CancellationToken ct) =>
                DecideAsync(id, approve: true, body, http, store, options, audit, ct))
            .WithName("ApproveCampaignExport").WithSummary("Approves someone else's request.");
        builder.MapPost("/{id:long}/reject", (long id, CampaignExportDecision body, HttpContext http, ICampaignExportStore store,
                IOptionsMonitor<CampaignExportOptions> options, IAuditLog audit, CancellationToken ct) =>
                DecideAsync(id, approve: false, body, http, store, options, audit, ct))
            .WithName("RejectCampaignExport").WithSummary("Rejects someone else's request.");
        builder.MapPost("/{id:long}/withdraw", WithdrawAsync).WithName("WithdrawCampaignExport")
            .WithSummary("Withdraws one's own request.");
        builder.MapPost("/{id:long}/download", DownloadAsync).WithName("DownloadCampaignExport")
            .WithSummary("The approved list as CSV, streamed from the run it was counted on.");

        return app;
    }

    // ------------------------------------------------------------------ routes

    private static async Task<IResult> ListAsync(
        HttpContext http, ICampaignExportStore store, IOptionsMonitor<CampaignExportOptions> options, CancellationToken ct)
    {
        var user = CurrentUser.Require(http);
        if (!user.Can(Permissions.CampaignExport) && !user.Can(Permissions.CampaignApprove))
        {
            return Forbidden("Campaign lists need the \"campaign.export\" or \"campaign.approve\" permission.");
        }

        var all = user.Can(Permissions.CampaignApprove);
        var requests = await store.ListAsync(all ? null : user.UserId, 200, ct).ConfigureAwait(false);
        var settings = options.CurrentValue;

        return Results.Ok(new CampaignExportList(
            [.. requests.Select(r => View(r, user, DateTimeOffset.UtcNow))], settings.ApprovalThreshold, settings.ValidHours));
    }

    private static async Task<IResult> CreateAsync(
        CampaignExportCreate request, HttpContext http, ICampaignExportStore store, IPlanningReader reader,
        IOptionsMonitor<CampaignExportOptions> options, IAuditLog audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = CurrentUser.Require(http);

        if (!user.Can(Permissions.CampaignExport) || !user.Can(Permissions.LookupSubscriber))
        {
            await audit.WriteAsync(Entry(user, http, "campaign.export.request", AuditOutcome.Denied, null, new()
            {
                ["view"] = request.View,
                ["class"] = request.Class,
            }), ct).ConfigureAwait(false);
            return Forbidden("A campaign list needs both the \"campaign.export\" and \"lookup.subscriber\" permissions.");
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var view = request.View ?? "sunset";
        if (view is not ("sunset" or "esim"))
        {
            errors["view"] = ["\"sunset\" or \"esim\"."];
        }

        var purpose = request.Purpose?.Trim() ?? string.Empty;
        if (purpose.Length is < 10 or > 1000)
        {
            errors["purpose"] = ["Say why the list is needed: 10 to 1,000 characters. It is shown to the approver and audited."];
        }

        if (string.IsNullOrWhiteSpace(request.Class))
        {
            errors["class"] = ["The class to list."];
        }
        else if (!string.IsNullOrWhiteSpace(request.Vendor) || !string.IsNullOrWhiteSpace(request.Model) || !string.IsNullOrWhiteSpace(request.DeviceType))
        {
            if (!ClickHousePlanningReader.KeepsModels(view, request.Class))
            {
                errors["vendor"] = [PlanningEndpoints.NotNarrowable];
            }
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var status = await reader.GetStatusAsync(ct).ConfigureAwait(false);
        var run = request.RunId is { } id
            ? status.Runs.FirstOrDefault(r => r.RunId == id)
            : status.Runs.Count > 0 ? status.Runs[0] : null;
        if (run is not { HasNumbers: true })
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "No such run",
                detail: "That planning run does not hold its numbers any more: only the two newest do. Ask from a current one.");
        }

        var filter = new PlanningFilter(request.Vendor, request.Model, request.DeviceType);
        var count = await reader.CountNumbersAsync(run.RunId, view, request.Class!, request.Reason, filter, ct).ConfigureAwait(false);
        if (count is not > 0)
        {
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Nothing to list",
                detail: "The class holds no numbers under these filters.");
        }

        var settings = options.CurrentValue;
        var needsApproval = count.Value > settings.ApprovalThreshold;
        var definition = new CampaignListDefinition(
            view, request.Class!, request.Reason, Blank(request.Vendor), Blank(request.Model), Blank(request.DeviceType),
            run.RunId, run.AsOfSequence, run.AsOfDate);

        var created = await store.CreateAsync(
            definition, count.Value, purpose, user.UserId, needsApproval,
            DateTimeOffset.UtcNow.AddHours(settings.ValidHours), ct).ConfigureAwait(false);

        await audit.WriteAsync(Entry(user, http, "campaign.export.request", AuditOutcome.Success, created.Id, new()
        {
            ["view"] = view,
            ["class"] = definition.Class,
            ["reason"] = definition.Reason,
            ["vendor"] = definition.Vendor,
            ["model"] = definition.Model,
            ["deviceType"] = definition.DeviceType,
            ["run"] = run.RunId,
            ["rows"] = count.Value,
            ["threshold"] = settings.ApprovalThreshold,
            ["needsApproval"] = needsApproval,
            ["purpose"] = purpose,
        }), ct).ConfigureAwait(false);

        return Results.Created($"/api/v1/campaign-exports/{created.Id}", View(created, user, DateTimeOffset.UtcNow));
    }

    private static async Task<IResult> DecideAsync(
        long id, bool approve, CampaignExportDecision body, HttpContext http, ICampaignExportStore store,
        IOptionsMonitor<CampaignExportOptions> options, IAuditLog audit, CancellationToken ct)
    {
        var user = CurrentUser.Require(http);
        var action = approve ? "campaign.export.approve" : "campaign.export.reject";

        if (!user.Can(Permissions.CampaignApprove))
        {
            await audit.WriteAsync(Entry(user, http, action, AuditOutcome.Denied, id, new()), ct).ConfigureAwait(false);
            return Forbidden("Deciding a campaign list needs the \"campaign.approve\" permission.");
        }

        var note = body?.Note?.Trim();
        if (note is { Length: > 1000 })
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["note"] = ["At most 1,000 characters."] });
        }

        var (outcome, request) = await store.DecideAsync(
            id, user.UserId, approve, note, DateTimeOffset.UtcNow.AddHours(options.CurrentValue.ValidHours), ct).ConfigureAwait(false);

        switch (outcome)
        {
            case CampaignDecisionOutcome.NotFound:
                return Results.NotFound();
            case CampaignDecisionOutcome.OwnRequest:
                await audit.WriteAsync(Entry(user, http, action, AuditOutcome.Denied, id, new() { ["why"] = "own request" }), ct).ConfigureAwait(false);
                return Forbidden("A second person decides: nobody approves or rejects their own request.");
            case CampaignDecisionOutcome.NotPending:
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Already decided",
                    detail: $"This request is {request?.Status}.");
        }

        await audit.WriteAsync(Entry(user, http, action, AuditOutcome.Success, id, new()
        {
            ["rows"] = request!.RowCount,
            ["requestedBy"] = request.RequestedByName,
            ["note"] = note,
        }), ct).ConfigureAwait(false);

        return Results.Ok(View(request, user, DateTimeOffset.UtcNow));
    }

    private static async Task<IResult> WithdrawAsync(
        long id, HttpContext http, ICampaignExportStore store, IAuditLog audit, CancellationToken ct)
    {
        var user = CurrentUser.Require(http);
        if (!await store.WithdrawAsync(id, user.UserId, ct).ConfigureAwait(false))
        {
            return Results.NotFound();
        }

        await audit.WriteAsync(Entry(user, http, "campaign.export.withdraw", AuditOutcome.Success, id, new()), ct).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> DownloadAsync(
        long id, HttpContext http, ICampaignExportStore store, IPlanningReader reader, IAuditLog audit, CancellationToken ct)
    {
        var user = CurrentUser.Require(http);
        var request = await store.GetAsync(id, ct).ConfigureAwait(false);

        // Only the requester, holding both permissions now, of a list approved and not expired.
        string? refusal = request is null || request.RequestedBy != user.UserId
            ? "not yours"
            : !user.Can(Permissions.CampaignExport) || !user.Can(Permissions.LookupSubscriber)
                ? "permission"
                : request.Status != "approved"
                    ? request.Status
                    : request.ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow
                        ? "expired"
                        : null;

        if (refusal is not null)
        {
            await audit.WriteAsync(Entry(user, http, "campaign.export.download", AuditOutcome.Denied, id, new() { ["why"] = refusal }), ct)
                .ConfigureAwait(false);
            return refusal switch
            {
                "not yours" => Results.NotFound(),
                "permission" => Forbidden("Downloading a campaign list needs both the \"campaign.export\" and \"lookup.subscriber\" permissions."),
                "expired" => Results.Problem(statusCode: StatusCodes.Status410Gone, title: "Expired",
                    detail: "The approval has run out. Ask again: the list is counted afresh."),
                _ => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Not approved", detail: $"This request is {refusal}."),
            };
        }

        var d = request!.Definition;
        var filter = new PlanningFilter(d.Vendor, d.Model, d.DeviceType);
        var held = await reader.CountNumbersAsync(d.RunId, d.View, d.Class, d.Reason, filter, ct).ConfigureAwait(false);
        if (held is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status410Gone, title: "The run is gone",
                detail: "The planning run this list was counted on no longer holds its numbers. Ask again from a current run.");
        }

        var reveal = user.Can(Permissions.IdentifierReveal);
        var reasons = ClickHousePlanningReader.Reasons.ToDictionary(r => r.Code, r => r.Label);
        var name = string.Create(CultureInfo.InvariantCulture,
            $"campaign-{request.Id}-{d.View}-{d.Class}-{d.AsOfDate ?? d.AsOfSequence.ToString(CultureInfo.InvariantCulture)}.csv");

        await store.RecordDownloadAsync(id, ct).ConfigureAwait(false);
        await audit.WriteAsync(Entry(user, http, "campaign.export.download", AuditOutcome.Success, id, new()
        {
            ["rows"] = held.Value,
            ["approvedRows"] = request.RowCount,
            ["masked"] = !reveal,
        }), ct).ConfigureAwait(false);

        // Streamed: millions of numbers are never held in memory, here or in ClickHouse's answer.
        return Results.Stream(async body =>
        {
            await using var writer = new StreamWriter(body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), bufferSize: 1 << 16);
            await writer.WriteAsync("Number,Class,Why not assessable,Handsets,Active bindings,Models held,Delivery,Business date\r\n").ConfigureAwait(false);

            var delivery = d.AsOfSequence.ToString(CultureInfo.InvariantCulture);
            await reader.StreamNumbersAsync(d.RunId, d.View, d.Class, d.Reason, filter, async row =>
            {
                var number = reveal ? row.Msisdn.ToString(CultureInfo.InvariantCulture) : IdentifierMask.Msisdn(row.Msisdn);
                await writer.WriteAsync(string.Join(',',
                    CsvExport.Field(number),
                    CsvExport.Field(d.Class),
                    CsvExport.Field(reasons.GetValueOrDefault(row.Reason) ?? string.Empty),
                    row.Handsets.ToString(CultureInfo.InvariantCulture),
                    row.Bindings.ToString(CultureInfo.InvariantCulture),
                    CsvExport.Field(string.Join("; ", row.Models)),
                    delivery,
                    d.AsOfDate ?? string.Empty) + "\r\n").ConfigureAwait(false);
            }, http.RequestAborted).ConfigureAwait(false);
        }, "text/csv; charset=utf-8", name);
    }

    // ------------------------------------------------------------------ helpers

    private static CampaignExportView View(CampaignExportRecord r, AuthenticatedUser user, DateTimeOffset now) => new(
        r.Id, r.Definition.View, r.Definition.Class, r.Definition.Reason,
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["vendor"] = r.Definition.Vendor,
            ["model"] = r.Definition.Model,
            ["deviceType"] = r.Definition.DeviceType,
        },
        r.Definition.AsOfSequence, r.Definition.AsOfDate, r.RowCount, r.Reason, r.RequestedByName, Iso(r.RequestedAt)!,
        r.NeedsApproval, r.Status, r.DecidedByName, Iso(r.DecidedAt), r.DecisionNote, Iso(r.ExpiresAt), r.Downloads,
        CanDownload: r.RequestedBy == user.UserId && r.Status == "approved" && (r.ExpiresAt is null || r.ExpiresAt > now)
                     && user.Can(Permissions.CampaignExport) && user.Can(Permissions.LookupSubscriber),
        CanDecide: r.Status == "pending" && r.RequestedBy != user.UserId && user.Can(Permissions.CampaignApprove));

    private static string? Iso(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IResult Forbidden(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not permitted", detail: detail);

    private static AuditEntry Entry(
        AuthenticatedUser user, HttpContext http, string action, AuditOutcome outcome, long? id, Dictionary<string, object?> detail) =>
        new(user.Username, action, AuditCategory.Data, outcome, user.UserId, "campaign_export",
            TargetId: id?.ToString(CultureInfo.InvariantCulture),
            Ip: http.Connection.RemoteIpAddress?.ToString(), UserAgent: http.Request.Headers.UserAgent.ToString(),
            CorrelationId: http.TraceIdentifier, Detail: detail);
}
