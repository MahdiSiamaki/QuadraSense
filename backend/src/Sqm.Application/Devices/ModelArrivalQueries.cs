using System.Globalization;
using Sqm.Contracts.Devices;

namespace Sqm.Application.Devices;

/// <summary>
/// New models, arrivals and network age as the API returns them, composed once for the routes and the
/// dashboard export.
/// </summary>
/// <remarks>
/// Pure: the routes and the export read the same <see cref="IModelArrivalReader"/> calls in the same
/// order and hand the results here, so readiness, the default window and the mapping cannot differ
/// between a card and its export.
/// </remarks>
public static class ModelArrivalQueries
{
    /// <summary>Days in the default window when no start is given: the 30 days to the end.</summary>
    public const int DefaultWindowDays = 30;

    /// <summary>Why first appearances cannot be shown yet, or null when they can.</summary>
    public static string? NotReady(ModelArrivalState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return !state.Deployed ? "New models are not deployed on this server (analytics migration 025)."
            : !state.Complete ? $"First appearances are still being written ({state.DaysWritten} of {state.DaysInLog} days); "
                                + "run Sqm.Ingestion --refresh-tac-days."
            : null;
    }

    /// <summary>Whether first appearances can be shown, and as of when.</summary>
    public static ModelArrivalStatus Status(ModelArrivalState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var notReady = NotReady(state);
        return new ModelArrivalStatus(notReady is null, notReady, Iso(state.DataThrough), Iso(NetworkAge.DataStart)!);
    }

    /// <summary>The window a new-models list covers: to the given end or the data-through day, from 30 days before it.</summary>
    public static (DateOnly Start, DateOnly End) Window(DateOnly? from, DateOnly? to, DateOnly through)
    {
        var end = to ?? through;
        return (from ?? end.AddDays(-(DefaultWindowDays - 1)), end);
    }

    /// <summary>The reader's query: blank text filters are none, and a brand is matched trimmed.</summary>
    public static NewModelQuery Query(
        DateOnly start, DateOnly end, string? brand, string? deviceType, bool knownOnly, int page, int pageSize) =>
        new(start, end, string.IsNullOrWhiteSpace(brand) ? null : brand.Trim(),
            string.IsNullOrWhiteSpace(deviceType) ? null : deviceType, knownOnly, page, pageSize);

    /// <summary>A page of models first seen in a period.</summary>
    public static NewModelsResponse NewModels(NewModelPage result, NewModelQuery query, DateOnly through)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(query);

        return new NewModelsResponse(
            [.. result.Rows.Select(r => new NewModelInfo(
                r.Tac, r.Brand, r.Model, r.DeviceType, Iso(r.FirstSeen)!, NetworkAge.Days(r.FirstSeen, false, through),
                r.DaysSeen, r.FirstDayImeis, r.Handsets, r.Sims))],
            result.Total, query.Page, query.PageSize, Iso(query.From)!, Iso(query.To)!, Iso(through));
    }

    /// <summary>First appearances per month or week.</summary>
    public static ModelArrivalsResponse Arrivals(string grain, IReadOnlyList<ModelArrivalPeriod> periods)
    {
        ArgumentNullException.ThrowIfNull(periods);

        return new ModelArrivalsResponse(
            grain, [.. periods.Select(p => new ModelArrivalPeriodInfo(Iso(p.PeriodStart)!, p.Models, p.KnownModels))],
            Iso(NetworkAge.DataStart)!);
    }

    /// <summary>One model's first appearance and network age.</summary>
    public static ModelNetworkAgeResponse NetworkAgeOf(string tac, ModelFirstSeen seen, DateOnly? dataThrough)
    {
        ArgumentNullException.ThrowIfNull(seen);

        return new ModelNetworkAgeResponse(
            tac, seen.InDump, Iso(seen.FirstSeen), NetworkAge.Days(seen.FirstSeen, seen.InDump, dataThrough),
            seen.InDump, seen.DaysSeen, Iso(dataThrough));
    }

    private static string? Iso(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
