using Sqm.Application.Abstractions;
using Sqm.Domain.Identifiers;

using Sqm.Api.Auth;
using Sqm.Application.Identity;

namespace Sqm.Api.Endpoints;

/// <summary>Request body for a subscriber lookup.</summary>
/// <param name="Msisdn">The subscriber number to look up.</param>
public sealed record LookupRequest(string Msisdn);

/// <summary>Subscriber-level lookup endpoints.</summary>
public static class LookupEndpoints
{
    /// <summary>Registers the lookup routes.</summary>
    public static IEndpointRouteBuilder MapLookupEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Withheld from Viewer by design: aggregate analytics needs no ability to identify an
        // individual, and this is the route that resolves a named person to their SIM and handset.
        var group = app.MapGroup("/api/v1/lookup").WithTags("Lookup")
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.LookupSubscriber);

        // POST, not GET, and the number travels in the body.
        //
        // This is not REST pedantry. A GET would put a real subscriber's phone number into the URL,
        // where it lands in web-server access logs, browser history, and any Referer header the page
        // later emits. With UI masking switched off by product decision, keeping identifiers out of
        // URLs and logs is the control that is actually doing the work.
        group.MapPost("/msisdn", LookupByMsisdnAsync)
            .WithName("LookupByMsisdn")
            .WithSummary("Every binding for one subscriber number, active and historical.");

        return app;
    }

    /// <remarks>
    /// Audited, always. With masking off by product decision, this audit entry is the primary
    /// record of who looked at whom - which is why the permission is separate from dashboards and
    /// why the entry is written even though the request only reads.
    /// <para>
    /// The number searched for is NOT in the entry. An audit log full of MSISDNs would be a
    /// second copy of the data it exists to protect, and the useful questions - who is running
    /// lookups, how many, how often - are all answerable without it.
    /// </para>
    /// </remarks>
    private static async Task<IResult> LookupByMsisdnAsync(
        LookupRequest request,
        IDeviceAnalyticsStore store,
        IAuditLog audit,
        HttpContext http,
        CancellationToken ct)
    {
        if (request is null || !Msisdn.TryParse(request.Msisdn, out var msisdn))
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    ["msisdn"] = ["Must be a numeric subscriber number."],
                },
                title: "Invalid MSISDN");
        }

        // A well-formed lookup is 10 digits. Others are accepted so operators can investigate the 24
        // known malformed rows, but the response tells the UI the input was unusual.
        var rows = await store.GetBindingsForMsisdnAsync(msisdn.Value.Value, ct).ConfigureAwait(false);

        var user = CurrentUser.Require(http);

        await audit.WriteAsync(new AuditEntry(
            ActorName: user.Username,
            Action: Permissions.LookupSubscriber,
            Category: AuditCategory.Data,
            Outcome: AuditOutcome.Success,
            ActorUserId: user.UserId,
            TargetType: "subscriber",
            Ip: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            CorrelationId: http.TraceIdentifier,
            Detail: new Dictionary<string, object?>
            {
                ["results"] = rows.Count,
                ["wellFormed"] = msisdn.Value.IsWellFormed,
            }), ct).ConfigureAwait(false);

        return Results.Ok(new
        {
            msisdn = msisdn.Value.ToString(),
            wellFormed = msisdn.Value.IsWellFormed,
            count = rows.Count,
            bindings = rows,
        });
    }
}
