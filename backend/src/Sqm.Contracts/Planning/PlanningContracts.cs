namespace Sqm.Contracts.Planning;

/// <summary>A published planning run: the delivery whose state it describes.</summary>
/// <param name="RunId">The run.</param>
/// <param name="AsOfSequence">The delivery.</param>
/// <param name="AsOfDate">That delivery's business date, ISO; null for the initial dump.</param>
/// <param name="PublishedAt">When it was published, ISO UTC.</param>
/// <param name="HasNumbers">
/// Whether its per-number rows are still held. Only the two newest runs keep them; older runs keep
/// their finished figures, for the trend.
/// </param>
public sealed record PlanningRunInfo(ulong RunId, int AsOfSequence, string? AsOfDate, string PublishedAt, bool HasNumbers);

/// <summary>What the planning views can show now.</summary>
/// <param name="Deployed">False until analytics migrations 029 and 030 are applied.</param>
/// <param name="Runs">Published runs, newest first - the deliveries a view can be shown for.</param>
/// <param name="Running">The run being built, if any: chunks done of chunks planned.</param>
/// <param name="Stale">Why the newest published run no longer matches the data, or null.</param>
/// <param name="Catalogue">The radio profiles the views read.</param>
public sealed record PlanningStatusResponse(
    bool Deployed,
    IReadOnlyList<PlanningRunInfo> Runs,
    PlanningProgress? Running,
    string? Stale,
    PlanningCatalogue? Catalogue);

/// <summary>A run being built.</summary>
public sealed record PlanningProgress(ulong RunId, int AsOfSequence, string? AsOfDate, int ChunksDone, int ChunksTotal);

/// <summary>The active GSMA version's radio profiles: how many, and how many carry an ambiguity.</summary>
/// <param name="TacVersionId">The active GSMA version.</param>
/// <param name="ParserVersion">The parser that read it.</param>
/// <param name="Models">TACs profiled.</param>
/// <param name="UnparsedModels">TACs with a band token no rule recognises - kept, not guessed.</param>
/// <param name="VolteMentions">TACs whose record mentions VoLTE at all. The catalogue has no VoLTE field.</param>
public sealed record PlanningCatalogue(int TacVersionId, int ParserVersion, long Models, long UnparsedModels, long VolteMentions);

/// <summary>One class of numbers, counted three ways.</summary>
/// <param name="Class">The class's key.</param>
/// <param name="Label">What it is called on screen.</param>
/// <param name="Numbers">Numbers (MSISDNs) with at least one active binding in the class. Exact unless the view is filtered.</param>
/// <param name="Bindings">Their active bindings. Exact unless filtered.</param>
/// <param name="Handsets">Their distinct handsets (IMEIs). Estimated.</param>
/// <param name="SharePercent">Of all numbers with an active binding - the stated denominator.</param>
public sealed record PlanningClassRow(string Class, string Label, long Numbers, long Bindings, long Handsets, double SharePercent);

/// <summary>A model, vendor or device type and who holds it within a class.</summary>
/// <param name="Key">The TAC, vendor or device type.</param>
/// <param name="Label">What it is called on screen.</param>
/// <param name="Tac">The TAC when the row is one model, for a link to its device page.</param>
/// <param name="Numbers">Numbers holding it. A number holding two models is counted under each. Estimated.</param>
/// <param name="Bindings">Active bindings on it. Exact.</param>
/// <param name="Handsets">Distinct handsets of it. Estimated.</param>
public sealed record PlanningBreakdownRow(string Key, string Label, string? Tac, long Numbers, long Bindings, long Handsets);

/// <summary>A per-number figure in one published run: the trend, point by point.</summary>
public sealed record PlanningTrendPoint(int AsOfSequence, string? AsOfDate, string Class, long Numbers, long Bindings);

/// <summary>A per-model figure at one delivery, from the per-model mart: binding-level history.</summary>
public sealed record PlanningModelTrendPoint(int Sequence, string? Date, string Class, long Bindings);

/// <summary>A part of a view the data cannot answer, said plainly instead of shown as zero.</summary>
/// <param name="Topic">What was asked.</param>
/// <param name="Reason">Why it cannot be answered from the data held.</param>
public sealed record PlanningNoData(string Topic, string Reason);

/// <summary>N1 - 2G/3G sunset readiness, for one delivery.</summary>
/// <param name="Run">The delivery.</param>
/// <param name="Filtered">A vendor, model or device type is applied: figures are numbers holding such a handset, estimated.</param>
/// <param name="TotalNumbers">Numbers with an active binding: the denominator of every share.</param>
/// <param name="Classes">Ready (LTE/5G), ready (LTE-M / NB-IoT only), exposed (2G/3G only), not assessable.</param>
/// <param name="NotAssessable">The last class by reason, the first that applies to each number.</param>
/// <param name="ExposedGsmOnly">Exposed numbers whose every handset is GSM only - stranded by a 2G switch-off too.</param>
/// <param name="ExposedIot">Exposed numbers holding an IoT / M2M device.</param>
/// <param name="Paired">
/// Handsets of an unassessable model resolved through their dual-SIM partner, in every class; 0 when the
/// view is narrowed, since a ready number keeps no models to narrow by.
/// </param>
/// <param name="Trend">The per-number classes in every published run.</param>
/// <param name="ModelTrend">Bindings on each model class at every delivery the per-model mart holds.</param>
/// <param name="NoData">VoLTE.</param>
public sealed record SunsetResponse(
    PlanningRunInfo Run,
    bool Filtered,
    long TotalNumbers,
    IReadOnlyList<PlanningClassRow> Classes,
    IReadOnlyList<PlanningClassRow> NotAssessable,
    long ExposedGsmOnly,
    long ExposedIot,
    long Paired,
    IReadOnlyList<PlanningTrendPoint> Trend,
    IReadOnlyList<PlanningModelTrendPoint> ModelTrend,
    IReadOnlyList<PlanningNoData> NoData);

/// <summary>N3 - eSIM readiness, for one delivery.</summary>
/// <param name="Run">The delivery.</param>
/// <param name="Filtered">As for <see cref="SunsetResponse"/>.</param>
/// <param name="TotalNumbers">Numbers with an active binding.</param>
/// <param name="Classes">Numbers with an eSIM-capable handset; with none, every handset known; not stated.</param>
/// <param name="Trend">The per-number classes in every published run.</param>
/// <param name="ModelTrend">Bindings on eSIM-capable, not capable and not-stated models at every delivery the mart holds.</param>
/// <param name="NoData">Physical SIM versus eSIM: the feed does not say which a SIM is.</param>
public sealed record EsimResponse(
    PlanningRunInfo Run,
    bool Filtered,
    long TotalNumbers,
    IReadOnlyList<PlanningClassRow> Classes,
    IReadOnlyList<PlanningTrendPoint> Trend,
    IReadOnlyList<PlanningModelTrendPoint> ModelTrend,
    IReadOnlyList<PlanningNoData> NoData);

/// <summary>A breakdown of one class by vendor, model or device type, a page of it.</summary>
public sealed record PlanningBreakdownResponse(
    PlanningRunInfo Run, string View, string Class, string By, IReadOnlyList<PlanningBreakdownRow> Rows, long Total);

/// <summary>A page of numbers in one class.</summary>
/// <param name="Run">The delivery.</param>
/// <param name="Class">The class listed.</param>
/// <param name="Rows">The page.</param>
/// <param name="Total">Numbers in the class under the filters.</param>
/// <param name="Page">This page, from 1.</param>
/// <param name="PageSize">Rows per page.</param>
/// <param name="Masked">Whether identifiers are masked for this caller.</param>
public sealed record PlanningNumberPage(
    PlanningRunInfo Run, string Class, IReadOnlyList<PlanningNumberRow> Rows, long Total, int Page, int PageSize, bool Masked);

/// <summary>One number in a list.</summary>
/// <param name="Msisdn">The number, masked unless the caller may reveal identifiers.</param>
/// <param name="Reason">Why it is not assessable, for that class.</param>
/// <param name="Handsets">Its distinct active handsets.</param>
/// <param name="Bindings">Its active bindings.</param>
/// <param name="Models">The models it holds, by marketing name where the catalogue has one.</param>
public sealed record PlanningNumberRow(string Msisdn, string? Reason, int Handsets, int Bindings, IReadOnlyList<string> Models);

/// <summary>A breakdown to export as CSV.</summary>
public sealed record PlanningBreakdownRequest(
    string? View, string? Class, string? By, ulong? RunId, string? Vendor, string? Model, string? DeviceType);

/// <summary>A request for a page of numbers. Filters travel in the body, as identifiers do elsewhere.</summary>
public sealed record PlanningNumberRequest(
    string? View, string? Class, string? Reason, string? Vendor, string? Model, string? DeviceType, ulong? RunId, int Page = 1, int PageSize = 50);

/// <summary>One radio band and the population of the models whose GSMA record lists it.</summary>
/// <param name="Technology"><c>lte</c> (E-UTRA, broadband) or <c>nr</c> (5G NR).</param>
/// <param name="Band">The band number: 3 for LTE band 3, 78 for n78.</param>
/// <param name="Operator">Whether the band is on the operator's configured list.</param>
/// <param name="Numbers">Numbers holding such a model. Estimated.</param>
/// <param name="Bindings">Active bindings on such models. Exact.</param>
/// <param name="Handsets">Distinct handsets of such models. Estimated.</param>
/// <param name="SharePercent">Of all numbers with an active binding.</param>
public sealed record PlanningBandRow(
    string Technology, int Band, bool Operator, long Numbers, long Bindings, long Handsets, double SharePercent);

/// <summary>N2 - 5G and radio bands, for one delivery, per model.</summary>
/// <param name="Run">The delivery.</param>
/// <param name="Filtered">A vendor, model or device type is applied.</param>
/// <param name="TotalNumbers">Numbers with an active binding: the denominator of every share. Estimated.</param>
/// <param name="OperatorLte">The operator's LTE bands, as configured; empty when not configured.</param>
/// <param name="OperatorNr">The operator's NR bands, as configured; empty when not configured.</param>
/// <param name="LteMatch">
/// LTE models by how they meet the operator's LTE bands - every one, some, none, or no band listed to
/// match. Empty when the list is not configured. A number holding two models is counted under each.
/// </param>
/// <param name="NrMatch">The same for 5G models and the operator's NR bands.</param>
/// <param name="Architecture">5G models by what their record states: SA, NSA, both, or neither.</param>
/// <param name="Ambiguous">Models with a band entry the parser could not read, kept as written.</param>
/// <param name="LteBands">Every LTE band the catalogue lists for the models on the network, largest first.</param>
/// <param name="NrBands">Every NR band likewise.</param>
/// <param name="NoData">What the bands cannot say: actual use of a band.</param>
public sealed record BandsResponse(
    PlanningRunInfo Run,
    bool Filtered,
    long TotalNumbers,
    IReadOnlyList<int> OperatorLte,
    IReadOnlyList<int> OperatorNr,
    IReadOnlyList<PlanningClassRow> LteMatch,
    IReadOnlyList<PlanningClassRow> NrMatch,
    IReadOnlyList<PlanningClassRow> Architecture,
    PlanningClassRow Ambiguous,
    IReadOnlyList<PlanningBandRow> LteBands,
    IReadOnlyList<PlanningBandRow> NrBands,
    IReadOnlyList<PlanningNoData> NoData);

/// <summary>N6 - satellite (NTN) capability, for one delivery, per model.</summary>
/// <param name="Run">The delivery.</param>
/// <param name="Filtered">A vendor, model or device type is applied.</param>
/// <param name="TotalNumbers">Numbers with an active binding. Estimated.</param>
/// <param name="Classes">
/// 3GPP NTN capable, a satellite radio listed (not 3GPP NTN), stated not capable, not stated - per
/// model, so a number holding two models is counted under each.
/// </param>
/// <param name="ByMode">NTN models by whether they also use the terrestrial network.</param>
/// <param name="ByOrbit">NTN models by the orbits their record names: GSO, NGSO or both.</param>
/// <param name="ByBand">NTN models by the NTN band their record lists.</param>
/// <param name="NotClassified">Unknown devices and TACs the catalogue lacks.</param>
/// <param name="ModelTrend">Bindings on each class at every delivery the per-model mart holds.</param>
/// <param name="NoData">Satellite service in use: capability is not a subscription.</param>
public sealed record NtnResponse(
    PlanningRunInfo Run,
    bool Filtered,
    long TotalNumbers,
    IReadOnlyList<PlanningClassRow> Classes,
    IReadOnlyList<PlanningBreakdownRow> ByMode,
    IReadOnlyList<PlanningBreakdownRow> ByOrbit,
    IReadOnlyList<PlanningBreakdownRow> ByBand,
    PlanningClassRow NotClassified,
    IReadOnlyList<PlanningModelTrendPoint> ModelTrend,
    IReadOnlyList<PlanningNoData> NoData);

/// <summary>N5 - the IoT / M2M fleet, for one delivery.</summary>
/// <param name="Run">The delivery.</param>
/// <param name="Devices">IoT / M2M handsets (IMEIs) active now, and the numbers and bindings they carry.</param>
/// <param name="ByDeviceType">Per GSMA device type.</param>
/// <param name="Exposure">2G only, 2G/3G only, LTE-M / NB-IoT only, LTE or 5G, not stated - per device.</param>
/// <param name="SimRotation">Devices by how many SIMs they have carried, all time.</param>
/// <param name="NotClassified">Handsets the classification cannot place: unknown devices and TACs the catalogue lacks.</param>
/// <param name="NoData">Trackers: the catalogue has no such device type.</param>
public sealed record IotResponse(
    PlanningRunInfo Run,
    PlanningClassRow Devices,
    IReadOnlyList<PlanningBreakdownRow> ByDeviceType,
    IReadOnlyList<PlanningBreakdownRow> Exposure,
    IReadOnlyList<PlanningBreakdownRow> SimRotation,
    PlanningClassRow NotClassified,
    IReadOnlyList<PlanningNoData> NoData);
