using Sqm.Contracts.Quality;

namespace Sqm.Application.Quality;

/// <summary>
/// The feed-quality days and the data-quality categories as the API returns them, composed once for
/// the quality routes and the dashboard export.
/// </summary>
/// <remarks>
/// A dashboard export draws a card from the same payload the card's own route returns. Mapping the
/// report or the categories in two places is how an export would come to show a different share or
/// a different label from the screen, so both callers use these.
/// </remarks>
public static class QualityQueries
{
    /// <summary>Why no categories are shown before a measures run carries them.</summary>
    public const string SignalsNotPublished =
        "No measures run with data-quality categories has been published yet. The worker builds one "
        + "once the import queue has been quiet for five minutes; it takes about two hours.";

    private static readonly IReadOnlyList<(string Code, string Label)> Bases =
    [
        ("all", "Bindings in current state, held or removed"),
        ("active", "Bindings held now"),
        ("history_all", "Bindings in the history"),
    ];

    /// <summary>Each day's file in a range, judged against the configured reference window.</summary>
    /// <param name="reader">The per-day counts.</param>
    /// <param name="options">The reference window and the rule.</param>
    /// <param name="from">First day, inclusive; null for open.</param>
    /// <param name="to">Last day, inclusive; null for open.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<FeedQualityResponse> FeedQualityAsync(
        IFeedQualityReader reader, FeedQualityOptions options, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(options);

        var report = await FeedQualityReport.BuildAsync(reader, options, from, to, ct).ConfigureAwait(false);

        return new FeedQualityResponse(
            new FeedQualityReferenceInfo(
                options.ReferenceFrom, options.ReferenceTo, report.Reference.Days,
                options.Multiplier, options.MinimumRate),
            [.. report.Days.Select(d => new FeedQualityDayInfo(
                d.Day.Date, d.Day.Rows, d.Day.Sims, d.Day.UnknownDeviceRows, d.Day.MalformedImeiRows,
                d.Day.UnknownTacRows, d.Day.ShiftedImeiRows, d.Day.MultiNumberSims, d.Day.MultiNumberSimRows,
                d.Flagged,
                [.. d.Findings.Select(x => new FeedQualityFindingInfo(
                    x.Check.ToString(), x.Rate, x.Threshold, x.ReferenceMedian, x.Flagged, x.Explanation))]))]);
    }

    /// <summary>The data-quality categories of the newest published run, or why there are none.</summary>
    public static async Task<QualitySignalsResponse> SignalsAsync(IQualityReader reader, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var snapshot = await reader.GetLatestAsync(ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            return new QualitySignalsResponse(false, SignalsNotPublished, null, null, null, [], []);
        }

        var counts = snapshot.Categories.ToDictionary(c => c.Category, StringComparer.Ordinal);
        QualityCategoryCount Count(string code) =>
            counts.TryGetValue(code, out var c) ? c : new QualityCategoryCount(code, 0, 0, 0, 0, 0);

        return new QualitySignalsResponse(
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
            })]);
    }
}
