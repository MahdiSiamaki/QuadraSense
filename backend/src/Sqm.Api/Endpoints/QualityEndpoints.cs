using Microsoft.Extensions.Options;
using Sqm.Api.Auth;
using Sqm.Application.Identity;
using Sqm.Application.Quality;
using Sqm.Contracts.Quality;

namespace Sqm.Api.Endpoints;

/// <summary>The feed's quality, day by day.</summary>
/// <remarks>
/// <para>
/// Behind <c>import.view</c>: this describes the files the operator sent, which is what the Import
/// Center is for, and it names nobody - counts and rates only.
/// </para>
/// <para>
/// A GET, because nothing in it identifies anybody: a date range is not personal data, unlike the
/// identifiers the lookup routes keep out of URLs.
/// </para>
/// </remarks>
public static class QualityEndpoints
{
    /// <summary>Registers the feed-quality routes.</summary>
    public static IEndpointRouteBuilder MapQualityEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/quality").WithTags("Quality")
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.ImportView);

        group.MapGet("/days", GetDaysAsync)
            .WithName("GetFeedQualityDays")
            .WithSummary("Each day's file measured and judged against the ordinary days.");

        return app;
    }

    private static async Task<IResult> GetDaysAsync(
        DateOnly? from,
        DateOnly? to,
        IFeedQualityReader reader,
        IOptions<FeedQualityOptions> options,
        CancellationToken ct)
    {
        if (from is { } f && to is { } t && f > t)
        {
            return Results.Problem(
                title: "The range is backwards.",
                detail: $"from ({f:yyyy-MM-dd}) is after to ({t:yyyy-MM-dd}).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var settings = options.Value;
        var report = await FeedQualityReport.BuildAsync(reader, settings, from, to, ct).ConfigureAwait(false);

        return Results.Ok(new FeedQualityResponse(
            new FeedQualityReferenceInfo(
                settings.ReferenceFrom, settings.ReferenceTo, report.Reference.Days,
                settings.Multiplier, settings.MinimumRate),
            [.. report.Days.Select(d => new FeedQualityDayInfo(
                d.Day.Date, d.Day.Rows, d.Day.Sims, d.Day.UnknownDeviceRows, d.Day.MalformedImeiRows,
                d.Day.UnknownTacRows, d.Day.ShiftedImeiRows, d.Day.MultiNumberSims, d.Day.MultiNumberSimRows,
                d.Flagged,
                [.. d.Findings.Select(x => new FeedQualityFindingInfo(
                    x.Check.ToString(), x.Rate, x.Threshold, x.ReferenceMedian, x.Flagged, x.Explanation))]))]));
    }
}
