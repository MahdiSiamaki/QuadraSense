using Microsoft.Extensions.Options;
using Sqm.Api.Auth;
using Sqm.Application.Identity;
using Sqm.Application.Quality;

namespace Sqm.Api.Endpoints;

/// <summary>The feed's quality, day by day, and the data-quality categories of the whole data.</summary>
/// <remarks>
/// <para>
/// Behind <c>import.view</c>: this describes the files the operator sent, which is what the Import
/// Center is for, and it names nobody - counts and rates only.
/// </para>
/// <para>
/// A GET, because nothing in it identifies anybody: a date range is not personal data, unlike the
/// identifiers the lookup routes keep out of URLs.
/// </para>
/// <para>
/// The categories (<c>/signals</c>) are facts about identifiers and the feed's sequences, built with the
/// measures snapshot (analytics migration 026). They are not risk and are never evidence about anyone.
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

        group.MapGet("/signals", GetSignalsAsync)
            .WithName("GetQualitySignals")
            .WithSummary("Data-quality categories of current state and of the history, from the newest published run.");

        return app;
    }

    // Both payloads are composed in QualityQueries, which the dashboard export shares.

    private static async Task<IResult> GetSignalsAsync(IQualityReader reader, CancellationToken ct) =>
        Results.Ok(await QualityQueries.SignalsAsync(reader, ct).ConfigureAwait(false));

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

        return Results.Ok(await QualityQueries.FeedQualityAsync(reader, options.Value, from, to, ct).ConfigureAwait(false));
    }
}
