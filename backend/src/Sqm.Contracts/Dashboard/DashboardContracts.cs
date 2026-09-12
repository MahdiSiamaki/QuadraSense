namespace Sqm.Contracts.Dashboard;

/// <summary>Standard envelope for a page of results.</summary>
/// <typeparam name="T">Row type.</typeparam>
/// <param name="Items">The rows for this page.</param>
/// <param name="Total">Total rows matching the filter, or <see langword="null"/> when not counted.</param>
/// <param name="PageNumber">1-based page number.</param>
/// <param name="PageSize">Rows per page.</param>
/// <remarks>
/// <paramref name="Total"/> is nullable on purpose. Counting matching rows over a 108M-row table costs
/// as much as the page query itself, so for exploratory endpoints we skip it and let the UI show
/// "load more" instead of a page count.
/// </remarks>
public sealed record Page<T>(IReadOnlyList<T> Items, long? Total, int PageNumber, int PageSize);

/// <summary>Filters accepted by dashboard endpoints. All fields are optional and combine with AND.</summary>
/// <param name="Manufacturer">Exact match on the raw GSMA manufacturer string.</param>
/// <param name="VendorCanonical">Match on the curated vendor name (collapses the 6 Samsung spellings).</param>
/// <param name="DeviceType">Exact match on GSMA device type, e.g. <c>Smartphone</c>.</param>
/// <param name="OperatingSystem">Exact match on GSMA operating system.</param>
/// <param name="Tac">Exact 8-character TAC.</param>
/// <param name="MsisdnPrefix">Leading digits of the subscriber number, e.g. <c>9149</c>.</param>
/// <param name="SequenceFrom">Inclusive lower bound on delivery sequence.</param>
/// <param name="SequenceTo">Inclusive upper bound on delivery sequence.</param>
/// <param name="IncludeUnknownDevice">
/// Whether to include the <c>000000</c> unknown-device population. Defaults to true: it is ~7% of rows
/// and excluding it silently would understate every total.
/// </param>
public sealed record DashboardFilter(
    string? Manufacturer = null,
    string? VendorCanonical = null,
    string? DeviceType = null,
    string? OperatingSystem = null,
    string? Tac = null,
    string? MsisdnPrefix = null,
    int? SequenceFrom = null,
    int? SequenceTo = null,
    bool IncludeUnknownDevice = true);

/// <summary>Headline counters for the dashboard.</summary>
/// <param name="ActiveBindings">Bindings currently active.</param>
/// <param name="DistinctSubscribers">Distinct MSISDNs with at least one active binding.</param>
/// <param name="DistinctDevices">Distinct IMEIs currently active.</param>
/// <param name="UnknownDeviceBindings">Active bindings whose IMEI is the <c>000000</c> sentinel.</param>
/// <param name="MalformedImeiBindings">
/// Active bindings whose IMEI is numeric but not 14 digits, so no TAC can be derived.
/// <para>
/// Reported separately from <paramref name="UnknownDeviceBindings"/> because the two mean different
/// things: the sentinel is the source telling us it does not know the device, while a malformed value
/// is a defect in the data itself. Measured at 31,209 versus 8,776,237 - reporting the small one inside
/// the large one makes a real, fixable problem invisible.
/// </para>
/// </param>
/// <param name="TacMatchedBindings">
/// Active bindings that matched a TAC in the GSMA database.
/// <para>
/// Returned as an exact count, not left to be derived from <paramref name="TacCoveragePercent"/>.
/// Deriving it (total − percent×total − unknown) compounds the percentage's rounding across 126M rows
/// and lands tens of thousands of rows away from the truth — which is unacceptable in a figure the
/// data-quality screen presents as a count.
/// </para>
/// </param>
/// <param name="TacCoveragePercent">Share of active bindings that enrich against the TAC database.</param>
public sealed record KpiSummary(
    long ActiveBindings,
    long DistinctSubscribers,
    long DistinctDevices,
    long UnknownDeviceBindings,
    long MalformedImeiBindings,
    long TacMatchedBindings,
    double TacCoveragePercent);

/// <summary>One row of a "top N by dimension" result.</summary>
/// <param name="Key">The dimension value, e.g. a manufacturer name.</param>
/// <param name="Count">Active bindings for that value.</param>
/// <param name="Percent">Share of the filtered total.</param>
public sealed record DimensionCount(string Key, long Count, double Percent);

/// <summary>A device-type or OS distribution slice.</summary>
/// <param name="Key">Category name.</param>
/// <param name="Count">Active bindings.</param>
/// <param name="Percent">Share of total.</param>
public sealed record DistributionSlice(string Key, long Count, double Percent);

/// <summary>
/// A point on a change time series, keyed by delivery sequence.
/// </summary>
/// <param name="Sequence">Delivery sequence number.</param>
/// <param name="DataDate">
/// The real calendar date, when the source has supplied one. <see langword="null"/> today, because the
/// delta files contain no date. The UI must label the axis as sequence while this is null rather than
/// inventing a date.
/// </param>
/// <param name="Added">Bindings activated.</param>
/// <param name="Removed">Bindings deactivated.</param>
/// <param name="Net">Added minus removed.</param>
public sealed record ChangePoint(int Sequence, DateOnly? DataDate, long Added, long Removed, long Net);

/// <summary>One active binding, as returned by subscriber lookup.</summary>
/// <param name="Msisdn">Subscriber number.</param>
/// <param name="Imsi">SIM identity.</param>
/// <param name="Imei">Device identity.</param>
/// <param name="Tac">Derived TAC, null when the IMEI is not 14 digits.</param>
/// <param name="Manufacturer">GSMA manufacturer, null when unenriched.</param>
/// <param name="MarketingName">GSMA marketing name, null when unenriched.</param>
/// <param name="DeviceType">GSMA device type, null when unenriched.</param>
/// <param name="IsActive">Whether the binding is currently active.</param>
public sealed record BindingRow(
    string Msisdn,
    string Imsi,
    string Imei,
    string? Tac,
    string? Manufacturer,
    string? MarketingName,
    string? DeviceType,
    bool IsActive);

/// <summary>Data-quality counters for one delivery.</summary>
/// <param name="Sequence">Delivery sequence.</param>
/// <param name="RowsReceived">Rows read from the file.</param>
/// <param name="RowsLoaded">Rows written to the event log.</param>
/// <param name="RowsQuarantined">Rows held for review.</param>
/// <param name="RedundantAdds">Adds for already-active bindings. Baseline 19.18%.</param>
/// <param name="OrphanRemoves">Removes for inactive bindings. Baseline 1.77%.</param>
/// <param name="DoubleAdds">
/// Adds following an add for the same binding. <b>Baseline is zero</b> across 8,062,257 measured
/// transitions; any non-zero value means the feed's semantics changed or ordering broke.
/// </param>
/// <param name="UnknownDeviceRows">Rows with the <c>000000</c> sentinel. Baseline 2.96% for deltas.</param>
public sealed record QualitySnapshot(
    int Sequence,
    long RowsReceived,
    long RowsLoaded,
    long RowsQuarantined,
    long RedundantAdds,
    long OrphanRemoves,
    long DoubleAdds,
    long UnknownDeviceRows);

/// <summary>Network or SIM capability of the active device population.</summary>
/// <param name="Capability">Capability name, e.g. <c>LTE</c>, <c>5G</c>, <c>eSIM</c>.</param>
/// <param name="Supported">Bindings whose device is known to have it.</param>
/// <param name="Unsupported">Bindings whose device is known not to have it.</param>
/// <param name="Unknown">
/// Bindings that cannot be assessed — unknown device, unregistered TAC, or the GSMA record is silent.
/// Carried separately rather than folded into <paramref name="Unsupported"/>: claiming a device lacks
/// 5G when we do not know what the device is would be a different, and wrong, statement.
/// </param>
/// <param name="PercentOfAssessable">
/// Share of the devices we can actually assess. The headline figure.
/// </param>
/// <param name="PercentOfAll">Share of every active binding, including the unassessable ones.</param>
/// <param name="CoveragePercent">
/// How much of the population this capability can be assessed for. Essential context: IMS emergency
/// calling reads 76.5% supported, but only across 0.34% of the base, which makes the headline
/// meaningless without it.
/// </param>
/// <remarks>
/// Capabilities <b>overlap</b> — a device can be LTE and 5G and eSIM at once — so these must never be
/// rendered as slices of a single whole. Each is an independent proportion.
/// </remarks>
public sealed record CapabilitySupport(
    string Capability,
    long Supported,
    long Unsupported,
    long Unknown,
    double PercentOfAssessable,
    double PercentOfAll,
    double CoveragePercent);
