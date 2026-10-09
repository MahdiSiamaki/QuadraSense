using System.Text.Json;
using Sqm.Contracts.Dashboards;

namespace Sqm.Application.Dashboards;

/// <summary>
/// The widget allow-list, read from <c>frontend/src/features/dashboard/registry/widget-contract.json</c>
/// (embedded in this assembly) - the same file the frontend registry is checked against (ADR-016 §3).
/// </summary>
public interface IWidgetContract
{
    /// <summary>The contract's version.</summary>
    int ContractVersion { get; }

    /// <summary>Every widget type, by its stable name.</summary>
    IReadOnlyDictionary<string, WidgetTypeSpec> Types { get; }

    /// <summary>The lens vocabulary: count units, relative ranges, filter limits.</summary>
    LensSpec Lens { get; }

    /// <summary>Grid and text limits that are part of the contract rather than configuration.</summary>
    ContractLimits Limits { get; }
}

/// <summary>Grid and text limits from the contract.</summary>
/// <param name="TitleMaxLength">A widget title, at most.</param>
/// <param name="SubtitleMaxLength">A widget subtitle, at most.</param>
/// <param name="GridColumns">The stored (desktop) grid's columns: 12.</param>
/// <param name="MaxRows">The last row a widget may reach.</param>
/// <param name="MaxWidgetHeight">The tallest a widget may be, in rows.</param>
public sealed record ContractLimits(int TitleMaxLength, int SubtitleMaxLength, int GridColumns, int MaxRows, int MaxWidgetHeight);

/// <summary>The lens vocabulary.</summary>
/// <param name="CountBy">Count units.</param>
/// <param name="Ranges">Relative range tokens: all, last7 … last365.</param>
/// <param name="RangePattern">An explicit range, <c>yyyy-MM-dd..yyyy-MM-dd</c>.</param>
/// <param name="TextFilterMaxLength">Vendor, device type and OS filter values, at most.</param>
/// <param name="TacPattern">A TAC filter: eight digits.</param>
/// <param name="RefreshMinutes">Allowed auto-refresh intervals; 0 is off.</param>
public sealed record LensSpec(
    IReadOnlyList<string> CountBy, IReadOnlyList<string> Ranges, string RangePattern, int TextFilterMaxLength,
    string TacPattern, IReadOnlyList<int> RefreshMinutes);

/// <summary>One widget type.</summary>
/// <param name="Type">Its stable name, e.g. <c>metric.kpi</c>.</param>
/// <param name="ConfigVersion">The config version this build accepts.</param>
/// <param name="Group">Its catalogue group.</param>
/// <param name="Permissions">Permissions it always needs.</param>
/// <param name="PermissionsByConfig">Permissions that follow one config field's value, or null.</param>
/// <param name="Filters">Lens filters it honours: vendor, deviceType, operatingSystem, tac, includeUnknownDevice.</param>
/// <param name="Measures">Count units it can count by; empty when it has a fixed unit.</param>
/// <param name="Time">current, series, range, fixedWindow or none.</param>
/// <param name="Execution">auto (loads on open), explicit (runs on an explicit Run only) or none (fetches nothing).</param>
/// <param name="Size">Default and minimum size, in grid units.</param>
/// <param name="Config">Its config fields, by name.</param>
public sealed record WidgetTypeSpec(
    string Type, int ConfigVersion, string Group, IReadOnlyList<string> Permissions,
    PermissionsByConfigSpec? PermissionsByConfig, IReadOnlyList<string> Filters, IReadOnlyList<string> Measures,
    string Time, string Execution, WidgetSizeSpec Size, IReadOnlyDictionary<string, ConfigFieldSpec> Config)
{
    /// <summary>Whether the type honours the lens's date range.</summary>
    public bool SupportsRange => Time is "series" or "range";

    /// <summary>Whether the type can count by a chosen unit.</summary>
    public bool SupportsCountBy => Measures.Count > 0;
}

/// <summary>Permissions that follow one config field: a risk list's rule, a lookup launcher's target.</summary>
/// <param name="Field">The config field.</param>
/// <param name="Map">For each value of that field, the permissions it needs.</param>
public sealed record PermissionsByConfigSpec(string Field, IReadOnlyDictionary<string, IReadOnlyList<string>> Map);

/// <summary>Default and minimum size in grid units.</summary>
public sealed record WidgetSizeSpec(int W, int H, int MinW, int MinH);

/// <summary>One config field.</summary>
/// <param name="Kind">enum, enumList, int, bool, string, stringList, date, range or explorerQuery.</param>
/// <param name="Required">Whether it must be present and non-empty.</param>
/// <param name="Nullable">Whether null is allowed.</param>
/// <param name="Default">The value when absent.</param>
/// <param name="Values">For enum and enumList: the allowed values.</param>
/// <param name="Min">For int: the least allowed.</param>
/// <param name="Max">For int: the most allowed.</param>
/// <param name="MaxLength">For string and stringList items: the longest allowed.</param>
/// <param name="Pattern">For string: a regular expression the value must match.</param>
/// <param name="MinItems">For lists: the fewest items.</param>
/// <param name="MaxItems">For lists: the most items.</param>
public sealed record ConfigFieldSpec(
    string Kind, bool Required, bool Nullable, JsonElement Default, IReadOnlyList<string>? Values,
    long? Min, long? Max, int? MaxLength, string? Pattern, int? MinItems, int? MaxItems);

/// <summary>A widget's position on the stored 12-column grid.</summary>
public sealed record WidgetLayout(int X, int Y, int W, int H);

/// <summary>A widget instance that passed validation, with every config field present (defaults filled).</summary>
/// <param name="Id">Its instance id.</param>
/// <param name="Type">Its type.</param>
/// <param name="ConfigVersion">Its config version.</param>
/// <param name="Title">Its own title, or null for the type's name.</param>
/// <param name="Subtitle">Its own subtitle, or null.</param>
/// <param name="Config">The normalised config: every field of the type, nothing else.</param>
/// <param name="Overrides">Lens fields the widget overrides, only those its type supports.</param>
/// <param name="Layout">Its desktop position.</param>
public sealed record ValidatedWidget(
    string Id, string Type, int ConfigVersion, string? Title, string? Subtitle, JsonElement Config,
    DashboardLens Overrides, WidgetLayout Layout);

/// <summary>A definition that passed validation.</summary>
/// <param name="SchemaVersion">The definition's schema version.</param>
/// <param name="Defaults">The dashboard's default lens.</param>
/// <param name="RefreshMinutes">Its auto-refresh interval; 0 is off.</param>
/// <param name="Widgets">Its widgets, in stored order.</param>
/// <param name="NormalizedJson">The definition as it should be stored: known fields only, defaults filled.</param>
public sealed record ValidatedDefinition(
    int SchemaVersion, DashboardLens Defaults, int RefreshMinutes, IReadOnlyList<ValidatedWidget> Widgets,
    string NormalizedJson);

/// <summary>One reason a definition was refused.</summary>
/// <param name="Path">Where, as a JSON path: <c>widgets[3].config.limit</c>.</param>
/// <param name="Message">What is wrong, written for a person.</param>
public sealed record DefinitionError(string Path, string Message);

/// <summary>The outcome of checking a definition.</summary>
/// <param name="Definition">The validated definition, or null when refused.</param>
/// <param name="Errors">Why it was refused; empty when valid.</param>
public sealed record DefinitionCheck(ValidatedDefinition? Definition, IReadOnlyList<DefinitionError> Errors)
{
    /// <summary>Whether the definition passed.</summary>
    public bool IsValid => Definition is not null && Errors.Count == 0;
}

/// <summary>Checks a dashboard definition against the widget contract and the configured limits.</summary>
/// <remarks>
/// Refuses unknown types, config versions other than the contract's, unknown or extra config fields,
/// values outside their bounds, layouts outside the grid, duplicate or malformed instance ids, lens
/// overrides the type does not support, and sizes over the limits. An aggregate Explorer query must be
/// shareable: aggregated, and using no field that needs a lookup permission (ADR-016 §10).
/// </remarks>
public interface IDashboardDefinitionValidator
{
    /// <summary>Checks a definition.</summary>
    DefinitionCheck Check(JsonElement definition);
}
