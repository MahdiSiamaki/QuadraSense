using Microsoft.Extensions.Options;
using Sqm.Api.Auth;
using Sqm.Application.Identity;
using Sqm.Application.Quality;
using Sqm.Contracts.Quality;

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

    private static readonly IReadOnlyList<(string Code, string Label)> Bases =
    [
        ("all", "Bindings in current state, held or removed"),
        ("active", "Bindings held now"),
        ("history_all", "Bindings in the history"),
    ];

    private static async Task<IResult> GetSignalsAsync(IQualityReader reader, CancellationToken ct)
    {
        var snapshot = await reader.GetLatestAsync(ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            return Results.Ok(new QualitySignalsResponse(
                false,
                "No measures run with data-quality categories has been published yet. The worker builds one "
                + "once the import queue has been quiet for five minutes; it takes about two hours.",
                null, null, null, [], []));
        }

        var counts = snapshot.Categories.ToDictionary(c => c.Category, StringComparer.Ordinal);
        QualityCategoryCount Count(string code) =>
            counts.TryGetValue(code, out var c) ? c : new QualityCategoryCount(code, 0, 0, 0, 0, 0);

        return Results.Ok(new QualitySignalsResponse(
            true,
            null,
            snapshot.RunId,
            snapshot.AsOf,
            snapshot.PublishedAt,
            [.. Bases.Select(b =>
            {
                var c = Count(b.Code);
                return new QualityBaseInfo(b.Code, b.Label, c.Bindings, c.Numbers, c.Sims, c.Imeis);
            })],
            [.. QualityCategories.All.Select(q =>
            {
                var c = Count(q.Code);
                var baseBindings = Count(q.Base).Bindings;
                return new QualitySignalInfo(
                    q.Code, q.Group, q.Label, q.Meaning, q.Base, c.Bindings, c.Numbers, c.Sims, c.Imeis, c.Periods,
                    baseBindings > 0 ? (double)c.Bindings / baseBindings : null);
            })]));
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
