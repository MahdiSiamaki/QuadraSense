using Sqm.Application.Identity;
using Sqm.Contracts.Dashboards;

namespace Sqm.Application.Dashboards;

/// <summary>What one widget's export needs to know about the request.</summary>
/// <param name="User">The person exporting, with permissions resolved for this request.</param>
/// <param name="DataThrough">The latest day in the change log, for relative ranges; null when unknown.</param>
/// <param name="Today">The server's UTC date, for freshness.</param>
public sealed record WidgetLoadContext(AuthenticatedUser User, DateOnly? DataThrough, DateOnly Today);

/// <summary>A widget's data as rows, for CSV.</summary>
/// <param name="Headers">Column names.</param>
/// <param name="Rows">The rows, cell values unformatted.</param>
/// <param name="Scope">
/// What the rows are: <c>visible</c> (what the card shows), <c>topN</c> (the card's top N of more) or
/// <c>full</c> (everything the source returned). A capped file is never called full.
/// </param>
/// <param name="ScopeNote">The scope in words, written into the export.</param>
public sealed record WidgetTable(
    IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<object?>> Rows, string Scope, string ScopeNote);

/// <summary>One widget's data, loaded by the server for an export.</summary>
/// <param name="Status">ok, forbidden, notReady, error, interactive or noData.</param>
/// <param name="Reason">Why it is not ok, written for a person.</param>
/// <param name="Data">The same shape the widget's own endpoint returns.</param>
/// <param name="AsOf">What the data is as of, in the source's own terms.</param>
/// <param name="Notes">Caveats the export must carry.</param>
/// <param name="Table">The data as rows for CSV, or null when it has none.</param>
public sealed record WidgetDataResult(
    string Status, string? Reason, object? Data, string? AsOf, IReadOnlyList<string> Notes, WidgetTable? Table);

/// <summary>
/// Loads one validated widget's data on the server, through the same stores its own endpoint reads,
/// for the person exporting (ADR-016 §11). Allow-listed: one adapter per widget type, nothing generic.
/// </summary>
/// <remarks>
/// The caller has already checked the widget's permissions; a loader still never widens them. Risk
/// lists and Explorer queries are <c>interactive</c>: they are re-run through their own audited
/// endpoints, never loaded here.
/// </remarks>
public interface IWidgetDataLoader
{
    /// <summary>Loads one widget's data.</summary>
    /// <param name="widget">The validated instance.</param>
    /// <param name="spec">Its type.</param>
    /// <param name="effective">The lens it is computed with.</param>
    /// <param name="context">The request.</param>
    /// <param name="ct">Cancellation.</param>
    Task<WidgetDataResult> LoadAsync(
        ValidatedWidget widget, WidgetTypeSpec spec, DashboardLens effective, WidgetLoadContext context, CancellationToken ct);

    /// <summary>The latest day in the change log, for relative ranges.</summary>
    Task<DateOnly?> DataThroughAsync(CancellationToken ct);
}
