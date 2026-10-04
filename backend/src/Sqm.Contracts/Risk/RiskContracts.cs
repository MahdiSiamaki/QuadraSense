namespace Sqm.Contracts.Risk;

/// <summary>One rule, as configured and as it applies to the published measures.</summary>
/// <param name="Rule">The rule's name, e.g. HighDeviceCount30.</param>
/// <param name="Family">Imei, Sim or Number: the unit it counts on.</param>
/// <param name="List">The list it is read from: Sims, ImeiWindow, ImeiLifetime or Numbers.</param>
/// <param name="Unit">What is counted, named as the UI names it.</param>
/// <param name="What">The rest of the sentence: what was counted, relative to the entity.</param>
/// <param name="WindowDays">The window; null for an all-time count.</param>
/// <param name="WindowFrom">yyyy-MM-dd, first day of the window the published measures cover.</param>
/// <param name="WindowTo">yyyy-MM-dd, its last day.</param>
/// <param name="DaysWithData">How many of the window's days had a file.</param>
/// <param name="Threshold">More than this is out of line; null until calibrated.</param>
/// <param name="TacsThreshold">For Randomisation20: the TAC count that must also be crossed.</param>
/// <param name="Floor">The smallest value stored; a threshold below <c>Floor - 1</c> is refused.</param>
/// <param name="Ceiling">The highest level the rule can give: Anomaly for all-time counts, RiskSignal otherwise.</param>
/// <param name="CappingChecks">The feed-quality checks whose flagged days hold the rule at Anomaly.</param>
/// <param name="FlaggedDays">yyyy-MM-dd days in the window flagged for one of those checks.</param>
/// <param name="HasDataQualityView">Whether the list has a data-quality view.</param>
public sealed record RiskRuleInfo(
    string Rule, string Family, string List, string Unit, string What, int? WindowDays,
    string? WindowFrom, string? WindowTo, int? DaysWithData,
    long? Threshold, long? TacsThreshold, int Floor, string Ceiling,
    IReadOnlyList<string> CappingChecks, IReadOnlyList<string> FlaggedDays, bool HasDataQualityView);

/// <summary>The published measures.</summary>
/// <param name="AsOf">yyyy-MM-dd: the day every window ends on.</param>
/// <param name="PublishedAt">When they were published, UTC.</param>
/// <param name="Stale">Why they no longer match the data; null when they do.</param>
public sealed record RiskRunInfo(string AsOf, DateTimeOffset PublishedAt, string? Stale);

/// <summary>What the risk pages can show, and on what basis.</summary>
/// <param name="Ready">True when lists and entities can be served.</param>
/// <param name="NotReady">Why not, when not.</param>
/// <param name="Calibrated">True when at least one rule has a threshold.</param>
/// <param name="RuleSetVersion">The rule set's version, quoted in every reason.</param>
/// <param name="MaxDefectShare">Above this share of adds set aside, an entity is not assessable.</param>
/// <param name="DataThrough">yyyy-MM-dd, the latest day of data.</param>
/// <param name="Run">The published measures, or null.</param>
/// <param name="Rules">Every rule.</param>
/// <param name="DeviceTypes">The GSMA device types IMEI and SIM lists can be narrowed to.</param>
public sealed record RiskStatusResponse(
    bool Ready, string? NotReady, bool Calibrated, string RuleSetVersion, double? MaxDefectShare,
    string? DataThrough, RiskRunInfo? Run, IReadOnlyList<RiskRuleInfo> Rules, IReadOnlyList<string> DeviceTypes);

/// <summary>How many entities one rule lists, and at which level. Names nobody.</summary>
/// <param name="Rule">The rule.</param>
/// <param name="Level">The level the listed entities get from this rule: Anomaly or RiskSignal.</param>
/// <param name="Capped">True when flagged feed days hold the rule at Anomaly in this window.</param>
/// <param name="Listed">Entities over the threshold on clean, assessable evidence.</param>
/// <param name="DataQuality">Entities in the data-quality view: over it only through feed defects.</param>
public sealed record RiskOverviewRule(string Rule, string Level, bool Capped, long Listed, long DataQuality);

/// <summary>Per rule, how many entities are listed.</summary>
public sealed record RiskOverviewResponse(string RuleSetVersion, string AsOf, IReadOnlyList<RiskOverviewRule> Rules);

/// <summary>A page of a rule's list. Thresholds may be overridden for this request only; they are audited, never saved.</summary>
/// <param name="Rule">The rule.</param>
/// <param name="View">"risk" (default) or "dataQuality".</param>
/// <param name="Threshold">Override: more than this; at least the floor minus one.</param>
/// <param name="TacsThreshold">Override for Randomisation20's TAC count.</param>
/// <param name="Page">From 1.</param>
/// <param name="PageSize">Up to 500.</param>
/// <param name="DeviceTypes">
/// GSMA device types to keep; empty or null for all. An IMEI list filters on the handset's own type, a SIM
/// list on the type of the SIM's most frequent TAC in 20 days. Not for number lists.
/// </param>
public sealed record RiskListRequest(
    string Rule, string? View = null, long? Threshold = null, long? TacsThreshold = null, int Page = 1, int PageSize = 50,
    IReadOnlyList<string>? DeviceTypes = null);

/// <summary>One rule's verdict on one entity.</summary>
/// <param name="Rule">The rule.</param>
/// <param name="Level">Observation, Anomaly or RiskSignal.</param>
/// <param name="Value">The measured value.</param>
/// <param name="Threshold">What it was compared with; null when the rule has none.</param>
/// <param name="Capped">True when held at Anomaly.</param>
/// <param name="Text">The server's explanation.</param>
public sealed record RiskReasonInfo(string Rule, string Level, long Value, long? Threshold, bool Capped, string Text);

/// <summary>A column of a list.</summary>
/// <param name="Name">The key in each row's values.</param>
/// <param name="Label">The heading, naming its unit.</param>
/// <param name="Type">Number, Date, Text or Tags.</param>
public sealed record RiskColumnInfo(string Name, string Label, string Type);

/// <summary>One entity in a list.</summary>
/// <param name="Key">The identifier; masked without identifier.reveal.</param>
/// <param name="Drillable">The key is complete and can be opened.</param>
/// <param name="Level">The family's level from the measures on the row.</param>
/// <param name="Assessable">False when most of its evidence was set aside as feed defects.</param>
/// <param name="Reasons">Every measured rule, highest level first.</param>
/// <param name="Values">Column values by name.</param>
public sealed record RiskListRow(
    string Key, bool Drillable, string Level, bool Assessable,
    IReadOnlyList<RiskReasonInfo> Reasons, IReadOnlyDictionary<string, object?> Values);

/// <summary>A page of a rule's list.</summary>
/// <param name="Rule">The rule.</param>
/// <param name="View">risk or dataQuality.</param>
/// <param name="Kind">msisdn, imsi or imei: what the keys are.</param>
/// <param name="Threshold">The threshold applied.</param>
/// <param name="TacsThreshold">For Randomisation20, the TAC threshold applied.</param>
/// <param name="Overridden">True when the request's own threshold was applied.</param>
/// <param name="Columns">The columns.</param>
/// <param name="Rows">The page.</param>
/// <param name="Total">Entities in the list.</param>
/// <param name="Reachable">How many paging can reach.</param>
/// <param name="Page">This page.</param>
/// <param name="PageSize">Rows per page.</param>
/// <param name="Masked">Keys are masked.</param>
/// <param name="RuleSetVersion">The rule set's version.</param>
/// <param name="AsOf">yyyy-MM-dd the measures are as of.</param>
/// <param name="ElapsedMs">Server time.</param>
public sealed record RiskListResponse(
    string Rule, string View, string Kind, long Threshold, long? TacsThreshold, bool Overridden,
    IReadOnlyList<RiskColumnInfo> Columns, IReadOnlyList<RiskListRow> Rows,
    long Total, long Reachable, int Page, int PageSize, bool Masked, string RuleSetVersion, string AsOf, long ElapsedMs);

/// <summary>Whose risk measures. In the body, never the URL.</summary>
/// <param name="Identifier">A phone number (10 digits), a SIM (15) or a handset (14).</param>
public sealed record RiskEntityRequest(string Identifier);

/// <summary>One entity's risk measures and verdicts.</summary>
/// <param name="Kind">msisdn, imsi or imei.</param>
/// <param name="Family">Number, Sim or Imei.</param>
/// <param name="Stored">False when it has no stored row: every measure is below its storage floor.</param>
/// <param name="Level">The family's level.</param>
/// <param name="Assessable">False when most of its evidence was set aside as feed defects.</param>
/// <param name="Reasons">Every measured rule, highest level first.</param>
/// <param name="Values">Its stored measures, by column name.</param>
/// <param name="Columns">Labels for those measures.</param>
/// <param name="RuleSetVersion">The rule set's version.</param>
/// <param name="AsOf">yyyy-MM-dd the measures are as of.</param>
/// <param name="Pattern">
/// The level of this entity together with those bound to it in the 30 days: SuspiciousPattern when
/// risk signals of two different families hold among them, otherwise the highest of their levels up
/// to RiskSignal.
/// </param>
/// <param name="Linked">Per family, how many bound entities have stored measures, and at which levels. Names nobody.</param>
public sealed record RiskEntityResponse(
    string Kind, string Family, bool Stored, string Level, bool Assessable,
    IReadOnlyList<RiskReasonInfo> Reasons, IReadOnlyDictionary<string, object?> Values,
    IReadOnlyList<RiskColumnInfo> Columns, string RuleSetVersion, string AsOf,
    string Pattern, IReadOnlyList<RiskLinkedInfo> Linked);

/// <summary>The entities of one family bound to an entity in the 30 days, counted by level.</summary>
/// <param name="Family">Imei, Sim or Number.</param>
/// <param name="Stored">How many have stored measures; the rest are below every floor.</param>
/// <param name="RiskSignals">Of those, at Risk signal.</param>
/// <param name="Anomalies">Of those, at Anomaly.</param>
public sealed record RiskLinkedInfo(string Family, int Stored, int RiskSignals, int Anomalies);
