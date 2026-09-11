using Sqm.Application.Abstractions;
using Sqm.Domain.Identifiers;

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

        var group = app.MapGroup("/api/v1/lookup").WithTags("Lookup");

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

    private static async Task<IResult> LookupByMsisdnAsync(
        LookupRequest request,
        IDeviceAnalyticsStore store,
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

        return Results.Ok(new
        {
            msisdn = msisdn.Value.ToString(),
            wellFormed = msisdn.Value.IsWellFormed,
            count = rows.Count,
            bindings = rows,
        });
    }
}
