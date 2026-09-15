using Sqm.Contracts.Dashboard;
using Sqm.Domain.Identifiers;

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

    /// <summary>Bindings matching a complete IMSI or a prefix, filtered and paged.</summary>
    /// <remarks>
    /// <para>
    /// Served from <c>sqm.binding_by_imsi</c>, a second copy of the current state ordered by IMSI.
    /// The primary table is ordered by <c>(msisdn, imsi, imei)</c>, where a filter on the second
    /// key column prunes nothing: measured over five real IMSIs, four read all 36,013 granules -
    /// the entire 295-million-row table, 2.2 GiB, ~1.3 s each. See ADR-008.
    /// </para>
    /// <para>
    /// A prefix is a numeric RANGE, not a string match, which is why it is fast: the term
    /// <c>4321139917</c> is <c>[432113991700000, 432113991799999]</c> and the primary index seeks
    /// straight to it. <c>LIKE '4321139917%'</c> would read the same 2.2 GiB.
    /// </para>
    /// </remarks>
    Task<ImsiSearchOutcome> SearchByImsiAsync(
        ImsiSearchCriteria criteria, CancellationToken ct);

    /// <summary>Dated add/remove events for one IMSI over a date range.</summary>
    /// <remarks>
    /// Reads the event log, which is partitioned by day, so a date range prunes partitions before
    /// anything is decompressed. Within the surviving partitions a bloom-filter skip index on
    /// <c>imsi</c> does the rest - the log is ordered by MSISDN and a second copy of 25 GiB was
    /// not worth what it would buy.
    /// </remarks>
    Task<ImsiHistoryOutcome> GetImsiHistoryAsync(
        ulong imsi, DateOnly? fromDate, DateOnly? toDate, int limit, CancellationToken ct);

    // ---------------------------------------------------------------- devices

    /// <summary>A page of the device-model catalogue, filtered, sorted and counted.</summary>
    /// <remarks>
    /// Served from <c>sqm.agg_device_model</c>, which holds one row per device model per delivery
    /// - about 98,000 rows. The same answer from <c>binding_current</c> is a 2.35 s aggregate over
    /// 295 million rows, which is a mart-build cost, not a keystroke cost.
    /// </remarks>
    Task<DeviceListOutcome> SearchDevicesAsync(DeviceListCriteria criteria, CancellationToken ct);

    /// <summary>Everything known about one device model, or null if the TAC is unknown entirely.</summary>
    /// <remarks>
    /// Returns a row even for a TAC that the GSMA snapshot does not list, as long as the network
    /// has seen it - measured at 0.20% of bindings. <c>KnownToGsma</c> distinguishes the two, so a
    /// page of nulls explains itself.
    /// </remarks>
    Task<DeviceDetailRow?> GetDeviceAsync(string tac, CancellationToken ct);

    /// <summary>Daily add and remove counts for one device model over a date range.</summary>
    /// <remarks>
    /// Reads <c>agg_change_daily</c>, which is partitioned by day and ordered by
    /// <c>(data_date, tac, label)</c>. The date range prunes partitions, and within a partition
    /// <c>data_date</c> is constant - so the second key column really does prune, which is the
    /// one place in this system where a non-leading key column does.
    /// </remarks>
    Task<DeviceTimelineOutcome> GetDeviceTimelineAsync(
        string tac, DateOnly? fromDate, DateOnly? toDate, CancellationToken ct);

    /// <summary>A page of the identifiers bound to one device model.</summary>
    /// <remarks>
    /// Served from <c>sqm.binding_by_imei</c>. A TAC is the first eight digits of an IMEI, so a
    /// model is the contiguous range <c>['&lt;tac&gt;000000', '&lt;tac&gt;999999']</c> of a table
    /// ordered by IMEI - a primary-index seek. The same filter on <c>binding_current</c> reads all
    /// 295 million rows: measured at 6.5-8.9 s across three models, every one a full scan.
    /// </remarks>
    Task<DeviceIdentifierOutcome> GetDeviceIdentifiersAsync(
        DeviceIdentifierCriteria criteria, CancellationToken ct);

    /// <summary>Resolves an IMEI, IMSI or MSISDN to the device models it reaches.</summary>
    /// <remarks>
    /// An IMEI needs no query at all - its first eight digits are the model, verified against
    /// every one of the 284,341,927 well-formed rows of current state with zero mismatches - but
    /// it is resolved anyway, so that a handset the network has never seen is reported as such
    /// rather than as a model page with nothing on it.
    /// </remarks>
    Task<DeviceResolution> ResolveDeviceAsync(
        DeviceSearchKind kind, string digits, CancellationToken ct);

    /// <summary>Distinct values available to filter the catalogue by.</summary>
    Task<DeviceFacetsData> GetDeviceFacetsAsync(CancellationToken ct);

    /// <summary>Adds, removes and net change per delivery sequence.</summary>
    Task<IReadOnlyList<ChangePoint>> GetChangeSeriesAsync(DashboardFilter filter, CancellationToken ct);

    /// <summary>
    /// Daily change history with a running active-binding total.
    /// </summary>
    /// <remarks>
    /// Dated from the source filenames, so this is a real calendar series rather than a
    /// delivery-order one. The running total starts from the initial dump's binding count.
    /// </remarks>
    Task<IReadOnlyList<DailyChange>> GetDailyChangesAsync(CancellationToken ct);

    /// <summary>Daily SIM and handset change counts.</summary>
    Task<IReadOnlyList<DailyChurn>> GetDailyChurnAsync(CancellationToken ct);

    /// <summary>Vendors ranked by movement, share or growth, with all three measured.</summary>
    /// <remarks>
    /// <para>
    /// Three different questions, returned together so the widget can switch between them without
    /// a round trip. <b>Movement</b> counts add and remove events in the window - a SIM moved
    /// between handsets thirty times contributes thirty of each and changes the population by
    /// nothing. <b>Share</b> counts the population now. <b>Growth</b> compares the population
    /// against the first delivery, and is only meaningful relative to the network's own change:
    /// the whole active population fell 9.3% over that span, so a vendor down 6.4% gained almost
    /// three points of share.
    /// </para>
    /// <para>
    /// About a second. Movement reads the day-partitioned change mart; both population figures
    /// read the dimension mart, which holds a few thousand rows per delivery.
    /// </para>
    /// </remarks>
    Task<VendorMovementResponse> GetVendorMovementAsync(
        DateOnly? from, DateOnly? toDate, VendorRanking ranking, int limit, CancellationToken ct);

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
