using Sqm.Contracts.Dashboard;

namespace Sqm.Application.Abstractions;

/// <summary>
/// Read access to the analytics store.
/// </summary>
/// <remarks>
/// Deliberately narrow and query-shaped rather than a generic repository. Each method corresponds to a
/// query whose cost and plan we understand; a generic <c>IQueryable</c> surface would let a caller
/// construct a query that scans 108M rows without anyone noticing at review time.
/// </remarks>
public interface IDeviceAnalyticsStore
{
    /// <summary>Headline counters for the dashboard.</summary>
    Task<KpiSummary> GetKpiSummaryAsync(DashboardFilter filter, CancellationToken ct);

    /// <summary>Top values of a dimension by active binding count.</summary>
    /// <param name="dimension">Which dimension to group by.</param>
    /// <param name="filter">Filters to apply.</param>
    /// <param name="limit">Maximum rows to return (server-capped).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<DimensionCount>> GetTopDimensionAsync(
        AnalyticsDimension dimension, DashboardFilter filter, int limit, CancellationToken ct);

    /// <summary>Distribution across a low-cardinality dimension, e.g. device type.</summary>
    Task<IReadOnlyList<DistributionSlice>> GetDistributionAsync(
        AnalyticsDimension dimension, DashboardFilter filter, CancellationToken ct);

    /// <summary>Every binding for one subscriber number, active and historical.</summary>
    /// <remarks>
    /// The MSISDN is passed as a parameter, never interpolated, and is carried in a request body rather
    /// than a query string so it never reaches an access log or browser history.
    /// </remarks>
    Task<IReadOnlyList<BindingRow>> GetBindingsForMsisdnAsync(ulong msisdn, CancellationToken ct);

    /// <summary>Adds, removes and net change per delivery sequence.</summary>
    Task<IReadOnlyList<ChangePoint>> GetChangeSeriesAsync(DashboardFilter filter, CancellationToken ct);
}

/// <summary>
/// Dimensions that may be grouped by.
/// </summary>
/// <remarks>
/// An enum, not a string. This is the allow-list that makes the query builder injection-proof: a caller
/// cannot name an arbitrary column because there is no way to express one.
/// </remarks>
public enum AnalyticsDimension
{
    /// <summary>Raw GSMA manufacturer string — 10,529 distinct values, including 6 spellings of Samsung.</summary>
    Manufacturer,

    /// <summary>Curated vendor name from the editable vendor map. What dashboards should normally use.</summary>
    VendorCanonical,

    /// <summary>GSMA marketing name, e.g. "Galaxy A54".</summary>
    MarketingName,

    /// <summary>GSMA device type — 19 distinct values.</summary>
    DeviceType,

    /// <summary>GSMA operating system, normalised for case and whitespace.</summary>
    OperatingSystem,

    /// <summary>The 8-digit TAC itself.</summary>
    Tac,
}
