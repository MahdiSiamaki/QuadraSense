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
    /// <param name="countBy">Whether to count bindings, subscribers or handsets.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<DimensionCount>> GetTopDimensionAsync(
        AnalyticsDimension dimension, DashboardFilter filter, int limit, CountBy countBy,
        CancellationToken ct);

    /// <summary>Distribution across a low-cardinality dimension, e.g. device type.</summary>
    Task<IReadOnlyList<DistributionSlice>> GetDistributionAsync(
        AnalyticsDimension dimension, DashboardFilter filter, CountBy countBy, CancellationToken ct);

    /// <summary>Every binding for one subscriber number, active and historical.</summary>
    /// <remarks>
    /// The MSISDN is passed as a parameter, never interpolated, and is carried in a request body rather
    /// than a query string so it never reaches an access log or browser history.
    /// </remarks>
    Task<IReadOnlyList<BindingRow>> GetBindingsForMsisdnAsync(ulong msisdn, CancellationToken ct);

    /// <summary>Adds, removes and net change per delivery sequence.</summary>
    Task<IReadOnlyList<ChangePoint>> GetChangeSeriesAsync(DashboardFilter filter, CancellationToken ct);

    /// <summary>
    /// The handset-versus-machine mix: smartphone, feature phone, tablet, IoT/M2M, wearable.
    /// </summary>
    /// <remarks>
    /// A coarse grouping of the 19 GSMA device types, on purpose. Charting all 19 is unreadable, and an
    /// arbitrary top-5 would hide the IoT segment — measured at 9.56M bindings (7.6%), which is a real
    /// business segment rather than tail noise.
    /// </remarks>
    Task<IReadOnlyList<DistributionSlice>> GetDeviceClassMixAsync(
        CountBy countBy, CancellationToken ct);

    /// <summary>
    /// Network and SIM capability of the active device population: LTE, 5G, eSIM.
    /// </summary>
    /// <remarks>
    /// Derived from the GSMA TAC record — LTE and 5G from the band list, eSIM from the eUICC counts.
    /// <b>VoLTE is not included because the dataset does not contain it</b>: bandDetails mentions it in
    /// 2 rows out of 270,166, and the IMS columns describe emergency calling rather than VoLTE.
    /// </remarks>
    Task<IReadOnlyList<CapabilitySupport>> GetCapabilitySupportAsync(
        CountBy countBy, CancellationToken ct);
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
