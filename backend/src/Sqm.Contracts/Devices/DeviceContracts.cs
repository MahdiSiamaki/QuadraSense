using Sqm.Contracts.Lookup;

namespace Sqm.Contracts.Devices;

/// <summary>
/// How the device list is ordered.
/// </summary>
/// <remarks>
/// An enum, not a column name. This is the allow-list that keeps the sort clause injection-proof:
/// a caller selects a member, and the SQL fragment for each is a literal written at compile time.
/// The same rule the dashboard's dimension allow-list follows.
/// </remarks>
public enum DeviceSort
{
    /// <summary>Active bindings. The default, because it is what "biggest device" means here.</summary>
    Bindings,

    /// <summary>Distinct handsets.</summary>
    Handsets,

    /// <summary>Distinct SIMs.</summary>
    Sims,

    /// <summary>Distinct subscriber numbers.</summary>
    Subscribers,

    /// <summary>Marketing name, alphabetically.</summary>
    Model,

    /// <summary>Manufacturer, alphabetically.</summary>
    Manufacturer,

    /// <summary>The code itself, which is also allocation order.</summary>
    Tac,

    /// <summary>Most recently touched by a daily file.</summary>
    LastSeen,
}

/// <summary>One device model in a list.</summary>
/// <param name="Tac">The 8-digit type allocation code.</param>
/// <param name="Manufacturer">GSMA manufacturer string, unmapped.</param>
/// <param name="Vendor">Curated vendor name, which collapses the six spellings of Samsung.</param>
/// <param name="Brand">GSMA brand name.</param>
/// <param name="Model">GSMA model name.</param>
/// <param name="MarketingName">The name the device is sold under, e.g. "Galaxy A54 5g".</param>
/// <param name="DeviceType">One of the 19 GSMA device types.</param>
/// <param name="OperatingSystem">GSMA operating-system string.</param>
/// <param name="Bindings">Active number + SIM + handset triples.</param>
/// <param name="Handsets">Distinct 14-digit IMEIs.</param>
/// <param name="Sims">Distinct IMSIs.</param>
/// <param name="Subscribers">Distinct MSISDNs.</param>
/// <param name="FirstSeen">First day a daily file named a binding of this model.</param>
/// <param name="LastSeen">Last such day.</param>
/// <param name="HasImage">Whether a curated photograph exists, so the list can show one.</param>
/// <remarks>
/// <b>The three distinct counts are estimates, and the difference between them is the point.</b>
/// They come from HyperLogLog at roughly 0.5% error - the same estimator every other mart here
/// uses, deliberately, so this table cannot disagree with the dashboard about one population.
/// <para>
/// <c>Handsets</c> is normally below <c>Bindings</c>, and that is not an error: a handset holding
/// two SIMs is one handset and two bindings. The most populous model on this network shows
/// 296,686 bindings across 208,895 handsets.
/// </para>
/// <para>
/// <c>FirstSeen</c> and <c>LastSeen</c> are null when no daily file has ever mentioned this model.
/// That means it is present only because the initial dump listed it - true of a large share of the
/// tail - and not that it is new.
/// </para>
/// </remarks>
public sealed record DeviceSummary(
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
    DateOnly? LastSeen,
    bool HasImage);

/// <summary>A page of the device catalogue.</summary>
/// <param name="Total">Matching models, before paging.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Rows per page, after the server's cap.</param>
/// <param name="Items">The page.</param>
/// <param name="Resolution">
/// Present when the search term was an identifier rather than a name, saying what it was taken to
/// be and what happened. Null for an ordinary catalogue search.
/// </param>
/// <param name="Timing">What the query cost, so a regression is visible rather than felt.</param>
public sealed record DeviceListResponse(
    int Total,
    int Page,
    int PageSize,
    IReadOnlyList<DeviceSummary> Items,
    DeviceSearchResolution? Resolution,
    SearchTiming Timing);

/// <summary>
/// What the GSMA record says this model can do.
/// </summary>
/// <param name="Lte">LTE support, derived from the band list.</param>
/// <param name="FiveG">5G support, derived from the band list.</param>
/// <param name="Esim">eSIM support, derived from the eUICC counts.</param>
/// <param name="ImsEmergency">IMS emergency calling.</param>
/// <remarks>
/// Every field is nullable and null means <b>the GSMA record does not say</b> - which for IMS
/// emergency covers 94% of TACs. Folding "not known" into "no" would assert something the source
/// never claimed, on a page whose whole job is to report what the source says.
/// <para>
/// VoLTE is absent because the dataset does not contain it: <c>bandDetails</c> mentions it in 2
/// rows out of 270,166, and the IMS columns describe emergency calling rather than VoLTE.
/// </para>
/// </remarks>
public sealed record DeviceCapabilities(bool? Lte, bool? FiveG, bool? Esim, bool? ImsEmergency);

/// <summary>How many of this model are on the network, and how that changed.</summary>
/// <param name="Bindings">Active bindings at the latest delivery.</param>
/// <param name="Handsets">Distinct handsets.</param>
/// <param name="Sims">Distinct SIMs.</param>
/// <param name="Subscribers">Distinct subscriber numbers.</param>
/// <param name="BindingsAtStart">Active bindings at the first delivery.</param>
/// <param name="ChangePercent">Change against that start, as a percentage.</param>
/// <param name="VsNetworkPoints">
/// That change minus the network's own over the same span, in percentage points.
/// </param>
/// <param name="FirstSeen">First day a daily file named a binding of this model.</param>
/// <param name="LastSeen">Last such day.</param>
/// <remarks>
/// <c>VsNetworkPoints</c> is the figure that makes growth readable, for the reason the vendor
/// widget records: the whole active population fell 9.29% over the measured span, so every
/// model's absolute change is negative and the ones that gained are those that fell by less.
/// </remarks>
public sealed record DevicePopulation(
    long Bindings,
    long Handsets,
    long Sims,
    long Subscribers,
    long BindingsAtStart,
    double ChangePercent,
    double VsNetworkPoints,
    DateOnly? FirstSeen,
    DateOnly? LastSeen);

/// <summary>Everything known about one device model.</summary>
/// <param name="Tac">The code.</param>
/// <param name="Manufacturer">GSMA manufacturer string.</param>
/// <param name="Vendor">Curated vendor name.</param>
/// <param name="Brand">GSMA brand name.</param>
/// <param name="Model">GSMA model name.</param>
/// <param name="MarketingName">Retail name.</param>
/// <param name="DeviceType">One of 19 GSMA types.</param>
/// <param name="OperatingSystem">GSMA operating-system string.</param>
/// <param name="Oem">Original equipment manufacturer, where it differs from the brand.</param>
/// <param name="OrganisationId">GSMA organisation that holds the allocation.</param>
/// <param name="AllocationDate">When the TAC was allocated.</param>
/// <param name="LastUpdatedDate">When the GSMA record last changed.</param>
/// <param name="Bluetooth">Bluetooth support as stated.</param>
/// <param name="Nfc">NFC support as stated.</param>
/// <param name="Wlan">WLAN support as stated.</param>
/// <param name="SimSlots">Physical SIM slots, as stated.</param>
/// <param name="ImeiQuantity">How many IMEIs the model carries.</param>
/// <param name="Bands">The raw band list. Long, and shown as a disclosure rather than inline.</param>
/// <param name="Capabilities">Derived capability flags.</param>
/// <param name="Population">Network population and its change.</param>
/// <param name="KnownToGsma">
/// False when the TAC appears in subscriber data but not in the active GSMA snapshot.
/// </param>
/// <param name="HasImage">Whether a curated photograph exists.</param>
/// <param name="ImageSourceNote">Where the uploader said the photograph came from.</param>
/// <param name="ImageUpdatedAt">When it was last replaced.</param>
/// <param name="TacVersionId">Which GSMA snapshot these fields were read from.</param>
/// <remarks>
/// Most fields are nullable because the GSMA export leaves them blank far more often than its
/// column list suggests, and because a TAC can be present on the network and absent from the
/// snapshot entirely - measured at 0.20% of bindings. <c>KnownToGsma</c> says which of those two
/// situations produced a page full of nulls, so the reader is not left guessing.
/// </remarks>
public sealed record DeviceDetail(
    string Tac,
    string? Manufacturer,
    string? Vendor,
    string? Brand,
    string? Model,
    string? MarketingName,
    string? DeviceType,
    string? OperatingSystem,
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
    DeviceCapabilities Capabilities,
    DevicePopulation Population,
    bool KnownToGsma,
    bool HasImage,
    string? ImageSourceNote,
    DateTimeOffset? ImageUpdatedAt,
    int TacVersionId);

/// <summary>One day of movement for a device model.</summary>
/// <param name="Date">The business date.</param>
/// <param name="Added">Bindings added that day.</param>
/// <param name="Removed">Bindings removed that day.</param>
public sealed record DeviceTimelinePoint(DateOnly Date, long Added, long Removed);

/// <summary>Daily movement for one device model over a range.</summary>
/// <param name="Tac">The model.</param>
/// <param name="From">Start of the range, inclusive.</param>
/// <param name="To">End of the range, inclusive.</param>
/// <param name="EarliestAvailable">Earliest day the change log covers.</param>
/// <param name="LatestAvailable">Latest day it covers.</param>
/// <param name="Points">One entry per day that had movement.</param>
/// <param name="Timing">What the query cost.</param>
/// <remarks>
/// These are <b>events, not devices</b>. A SIM moved between two handsets of the same model
/// contributes one add and one remove and changes the population by nothing. The population
/// figures live on <see cref="DevicePopulation"/>, and the page says which is which - conflating
/// the two is the mistake the vendor widget was rebuilt to stop making.
/// <para>
/// Days with no movement are absent rather than zero-filled; the chart fills the gaps, because
/// sending 133 zeroes to describe nothing happening is not the server's job.
/// </para>
/// </remarks>
public sealed record DeviceTimelineResponse(
    string Tac,
    DateOnly? From,
    DateOnly? To,
    DateOnly? EarliestAvailable,
    DateOnly? LatestAvailable,
    IReadOnlyList<DeviceTimelinePoint> Points,
    SearchTiming Timing);

/// <summary>One binding of a device model.</summary>
/// <param name="Imei">The handset, masked unless the caller holds <c>identifier.reveal</c>.</param>
/// <param name="Imsi">The SIM, same rule.</param>
/// <param name="Msisdn">The subscriber number, same rule.</param>
/// <param name="IsActive">Whether the binding is currently in force.</param>
/// <param name="LastChangeDate">
/// The day a daily file last said anything about it; null means none ever has.
/// </param>
public sealed record DeviceIdentifierRow(
    string Imei,
    string Imsi,
    string Msisdn,
    bool IsActive,
    DateOnly? LastChangeDate);

/// <summary>A page of the identifiers bound to one device model.</summary>
/// <param name="Tac">The model.</param>
/// <param name="Total">Matching bindings, before paging.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Rows per page, after the server's cap.</param>
/// <param name="Identifiers">Whether the server sent complete identifiers or redacted ones.</param>
/// <param name="Items">The page.</param>
/// <param name="Timing">What the query cost.</param>
/// <remarks>
/// Behind <c>device.identifiers</c>, which is separate from the per-handset lookup on purpose:
/// this is bulk. The most populous model covers 208,895 handsets, and every request here is
/// audited with the count it returned.
/// </remarks>
public sealed record DeviceIdentifiersResponse(
    string Tac,
    int Total,
    int Page,
    int PageSize,
    IdentifierVisibility Identifiers,
    IReadOnlyList<DeviceIdentifierRow> Items,
    SearchTiming Timing);

/// <summary>What a typed search term turned out to be, and where it led.</summary>
/// <param name="Kind">
/// Which identifier the term was taken to be: <c>Tac</c>, <c>Msisdn</c>, <c>Imei</c>, <c>Imsi</c>
/// or <c>UnknownDeviceSentinel</c>.
/// </param>
/// <param name="Models">How many device models it reached.</param>
/// <param name="Permitted">
/// False when the caller may not resolve identifiers of this kind. The search then returns nothing
/// rather than falling back to a text match, because a silent fallback would look like "no such
/// handset" when the truth is "you may not ask".
/// </param>
/// <param name="Note">Why the answer is what it is, in the reader's own terms.</param>
/// <remarks>
/// Returned alongside the results rather than instead of them, so a single request both classifies
/// the term and returns the models. The classification rule is length-based and is documented on
/// <c>DeviceSearchTerm</c> - it works only because TAC, MSISDN, IMEI and IMSI have four distinct
/// lengths in this feed, which was measured rather than assumed.
/// </remarks>
public sealed record DeviceSearchResolution(
    string Kind,
    int Models,
    bool Permitted,
    string? Note);

/// <summary>Values available to filter the catalogue by.</summary>
/// <param name="DeviceTypes">All 19 GSMA device types present, with how many models carry each.</param>
/// <param name="Manufacturers">The most common manufacturers, by model count.</param>
/// <param name="OperatingSystems">The most common operating systems.</param>
/// <remarks>
/// Counted over device <i>models</i>, not bindings, because these fill filter controls: a reader
/// choosing "Tablet" wants to know how many rows that leaves, not how many subscribers it covers.
/// </remarks>
public sealed record DeviceFacets(
    IReadOnlyList<DeviceFacetValue> DeviceTypes,
    IReadOnlyList<DeviceFacetValue> Manufacturers,
    IReadOnlyList<DeviceFacetValue> OperatingSystems);

/// <summary>One filter value and how many models carry it.</summary>
/// <param name="Value">The value, as stored.</param>
/// <param name="Models">How many device models carry it.</param>
public sealed record DeviceFacetValue(string Value, long Models);
