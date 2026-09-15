using Sqm.Domain.Identifiers;

namespace Sqm.Application.Abstractions;

/// <summary>What to return from the device catalogue.</summary>
/// <param name="Text">
/// Free-text match against TAC, manufacturer, brand, model and marketing name. Null for no
/// text filter.
/// </param>
/// <param name="Manufacturer">Exact manufacturer, as stored.</param>
/// <param name="Brand">Exact brand name.</param>
/// <param name="DeviceType">Exact GSMA device type.</param>
/// <param name="OperatingSystem">Exact operating-system string.</param>
/// <param name="Tacs">
/// Restrict to these models. Used when an identifier search has already resolved to a set.
/// </param>
/// <param name="MinBindings">Drop models below this population. Zero keeps everything.</param>
/// <param name="Sort">Which column orders the result.</param>
/// <param name="Descending">Direction.</param>
/// <param name="Offset">Rows to skip.</param>
/// <param name="Limit">Rows to return, already capped by the caller.</param>
public sealed record DeviceListCriteria(
    string? Text,
    string? Manufacturer,
    string? Brand,
    string? DeviceType,
    string? OperatingSystem,
    IReadOnlyList<string>? Tacs,
    long MinBindings,
    DeviceListSort Sort,
    bool Descending,
    int Offset,
    int Limit);

/// <summary>
/// Sortable columns of the device catalogue.
/// </summary>
/// <remarks>
/// Mirrors the contract enum rather than reusing it, so the application layer does not depend on
/// the wire format. The mapping is one switch in the endpoint, and it is the point at which an
/// unknown value is rejected.
/// </remarks>
public enum DeviceListSort
{
    /// <summary>Active bindings.</summary>
    Bindings,

    /// <summary>Distinct handsets.</summary>
    Handsets,

    /// <summary>Distinct SIMs.</summary>
    Sims,

    /// <summary>Distinct subscriber numbers.</summary>
    Subscribers,

    /// <summary>Marketing name.</summary>
    Model,

    /// <summary>Manufacturer.</summary>
    Manufacturer,

    /// <summary>The code.</summary>
    Tac,

    /// <summary>Most recently touched by a daily file.</summary>
    LastSeen,
}

/// <summary>One device model as the catalogue query returns it.</summary>
/// <param name="Tac">The code.</param>
/// <param name="Manufacturer">GSMA manufacturer string.</param>
/// <param name="Vendor">Curated vendor name.</param>
/// <param name="Brand">GSMA brand name.</param>
/// <param name="Model">GSMA model name.</param>
/// <param name="MarketingName">Retail name.</param>
/// <param name="DeviceType">GSMA device type.</param>
/// <param name="OperatingSystem">GSMA operating-system string.</param>
/// <param name="Bindings">Active bindings.</param>
/// <param name="Handsets">Distinct handsets, estimated.</param>
/// <param name="Sims">Distinct SIMs, estimated.</param>
/// <param name="Subscribers">Distinct subscriber numbers, estimated.</param>
/// <param name="FirstSeen">First day a daily file named a binding of this model.</param>
/// <param name="LastSeen">Last such day.</param>
public sealed record DeviceRow(
    string Tac,
    string? Manufacturer,
    string? Vendor,
    string? Brand,
    string? Model,
    string? MarketingName,
    string? DeviceType,
    string? OperatingSystem,
    long Bindings,
    long Handsets,
    long Sims,
    long Subscribers,
    DateOnly? FirstSeen,
    DateOnly? LastSeen);

/// <summary>A page of the catalogue, with what it cost.</summary>
/// <param name="Rows">The page.</param>
/// <param name="Total">Matching models before paging.</param>
/// <param name="ElapsedMs">Server-side duration.</param>
/// <param name="RowsExamined">Rows the engine read. The number that tells an index from a scan.</param>
public sealed record DeviceListOutcome(
    IReadOnlyList<DeviceRow> Rows, int Total, long ElapsedMs, long RowsExamined);

/// <summary>Everything the analytics store knows about one device model.</summary>
/// <param name="Summary">The same fields the list returns.</param>
/// <param name="Oem">Original equipment manufacturer.</param>
/// <param name="OrganisationId">GSMA organisation holding the allocation.</param>
/// <param name="AllocationDate">When the TAC was allocated, as the source spells it.</param>
/// <param name="LastUpdatedDate">When the GSMA record last changed.</param>
/// <param name="Bluetooth">Bluetooth support as stated.</param>
/// <param name="Nfc">NFC support as stated.</param>
/// <param name="Wlan">WLAN support as stated.</param>
/// <param name="SimSlots">Physical SIM slots.</param>
/// <param name="ImeiQuantity">How many IMEIs the model carries.</param>
/// <param name="Bands">The raw band list.</param>
/// <param name="HasLte">LTE support, or null when the record does not say.</param>
/// <param name="Has5g">5G support, or null.</param>
/// <param name="HasEsim">eSIM support, or null.</param>
/// <param name="ImsEmergency">IMS emergency calling, or null.</param>
/// <param name="BindingsAtStart">Active bindings at the first delivery.</param>
/// <param name="NetworkNow">Network-wide active bindings now, for the relative figure.</param>
/// <param name="NetworkAtStart">Network-wide active bindings at the first delivery.</param>
/// <param name="KnownToGsma">False when the TAC is on the network but absent from the snapshot.</param>
/// <param name="TacVersionId">Which GSMA snapshot these fields came from.</param>
public sealed record DeviceDetailRow(
    DeviceRow Summary,
    string? Oem,
    string? OrganisationId,
    string? AllocationDate,
    string? LastUpdatedDate,
    string? Bluetooth,
    string? Nfc,
    string? Wlan,
    string? SimSlots,
    string? ImeiQuantity,
    string? Bands,
    bool? HasLte,
    bool? Has5g,
    bool? HasEsim,
    bool? ImsEmergency,
    long BindingsAtStart,
    long NetworkNow,
    long NetworkAtStart,
    bool KnownToGsma,
    int TacVersionId);

/// <summary>One day of movement for a device model.</summary>
/// <param name="Date">The business date.</param>
/// <param name="Added">Bindings added.</param>
/// <param name="Removed">Bindings removed.</param>
public sealed record DeviceTimelineRow(DateOnly Date, long Added, long Removed);

/// <summary>A device model's daily movement, with what it cost.</summary>
/// <param name="Points">One entry per day that had movement.</param>
/// <param name="Earliest">Earliest day the change log covers.</param>
/// <param name="Latest">Latest day it covers.</param>
/// <param name="ElapsedMs">Server-side duration.</param>
/// <param name="RowsExamined">Rows the engine read.</param>
public sealed record DeviceTimelineOutcome(
    IReadOnlyList<DeviceTimelineRow> Points,
    DateOnly? Earliest,
    DateOnly? Latest,
    long ElapsedMs,
    long RowsExamined);

/// <summary>Which of a device model's identifiers to return.</summary>
/// <param name="Tac">The model.</param>
/// <param name="ActiveOnly">Exclude bindings that are no longer in force.</param>
/// <param name="From">Earliest last-change date, inclusive.</param>
/// <param name="To">Latest last-change date, inclusive.</param>
/// <param name="Offset">Rows to skip.</param>
/// <param name="Limit">Rows to return, already capped by the caller.</param>
/// <remarks>
/// A date filter excludes every binding the daily feed has never mentioned, because their
/// last-change date is null. That is the right answer to "changed in this window" and a
/// surprising one to anybody not told, so the UI says so beside the control.
/// </remarks>
public sealed record DeviceIdentifierCriteria(
    string Tac,
    bool ActiveOnly,
    DateOnly? From,
    DateOnly? To,
    int Offset,
    int Limit);

/// <summary>One binding of a device model, as stored.</summary>
/// <param name="Imei">The handset.</param>
/// <param name="Imsi">The SIM.</param>
/// <param name="Msisdn">The subscriber number.</param>
/// <param name="IsActive">Whether the binding is currently in force.</param>
/// <param name="LastChangeDate">Day a daily file last named it; null means none ever has.</param>
public sealed record DeviceIdentifierRowData(
    string Imei, ulong Imsi, ulong Msisdn, bool IsActive, DateOnly? LastChangeDate);

/// <summary>A page of a device model's identifiers, with what it cost.</summary>
/// <param name="Rows">The page.</param>
/// <param name="Total">Matching bindings before paging.</param>
/// <param name="ElapsedMs">Server-side duration.</param>
/// <param name="RowsExamined">Rows the engine read.</param>
public sealed record DeviceIdentifierOutcome(
    IReadOnlyList<DeviceIdentifierRowData> Rows, int Total, long ElapsedMs, long RowsExamined);

/// <summary>What an identifier search resolved to.</summary>
/// <param name="Kind">How the term was classified.</param>
/// <param name="Tacs">The device models it reaches, most populous first.</param>
/// <param name="ElapsedMs">Server-side duration.</param>
/// <param name="RowsExamined">Rows the engine read.</param>
/// <remarks>
/// A SIM or a number can reach several models - a person who changed handset - so this is a list
/// even when the common case is one entry.
/// </remarks>
public sealed record DeviceResolution(
    DeviceSearchKind Kind, IReadOnlyList<string> Tacs, long ElapsedMs, long RowsExamined);

/// <summary>One filter value and how many device models carry it.</summary>
/// <param name="Value">The value, as stored.</param>
/// <param name="Models">How many models carry it.</param>
public sealed record DeviceFacet(string Value, long Models);

/// <summary>Values available to filter the catalogue by, counted over models.</summary>
/// <param name="DeviceTypes">GSMA device types present.</param>
/// <param name="Manufacturers">The most common manufacturers.</param>
/// <param name="OperatingSystems">The most common operating systems.</param>
/// <remarks>
/// Counted over models rather than bindings because these fill filter controls: a reader choosing
/// "Tablet" wants to know how many rows that leaves, not how many subscribers it covers.
/// </remarks>
public sealed record DeviceFacetsData(
    IReadOnlyList<DeviceFacet> DeviceTypes,
    IReadOnlyList<DeviceFacet> Manufacturers,
    IReadOnlyList<DeviceFacet> OperatingSystems);
