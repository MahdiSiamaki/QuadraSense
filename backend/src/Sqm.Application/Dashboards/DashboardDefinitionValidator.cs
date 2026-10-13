using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Sqm.Application.Explorer;
using Sqm.Contracts.Dashboards;
using Sqm.Contracts.Explorer;

namespace Sqm.Application.Dashboards;

/// <summary>
/// Checks a dashboard definition against the widget contract and the configured limits, and writes
/// the version that is stored (ADR-016 §3-§4).
/// </summary>
/// <remarks>
/// <para>
/// <b>An allow-list, applied whole.</b> Every key at every level must be one the contract names:
/// unknown types, unknown config fields, lens overrides a type does not support and layouts outside
/// the grid are refused, not dropped. Dropping would store something other than what the person
/// built, and they would not know.
/// </para>
/// <para>
/// <b>The stored form is normalised</b>: known fields only, every config field present with its
/// default, keys in a fixed order. Two equal dashboards are byte-for-byte equal, and a widget never
/// depends on a default that a later contract might change.
/// </para>
/// <para>
/// <b>A shared definition never holds an identifier.</b> The aggregate Explorer widget's query must
/// pass the Explorer's own validator, be aggregated, and need no permission beyond explorer.query -
/// so no MSISDN, IMSI or IMEI can be filtered, grouped or shown, and no value of one can be stored.
/// </para>
/// </remarks>
public sealed class DashboardDefinitionValidator : IDashboardDefinitionValidator
{
    /// <summary>The definition schema version this build reads and writes.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Errors reported at most; past that, the first fifty are enough to fix.</summary>
    public const int MaxErrors = 50;

    private static readonly Regex InstanceId = new(@"^w_[a-z0-9]{6,32}\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static readonly string[] RootKeys = ["schemaVersion", "defaults", "refreshMinutes", "widgets"];
    private static readonly string[] WidgetKeys = ["id", "type", "configVersion", "title", "subtitle", "config", "overrides", "layout"];
    private static readonly string[] LensKeys = ["countBy", "includeUnknownDevice", "range", "vendor", "deviceType", "operatingSystem", "tac"];
    private static readonly string[] LayoutKeys = ["x", "y", "w", "h"];

    /// <summary>The API's JSON for an Explorer query: camelCase, enums by name, unknown members refused.</summary>
    private static readonly JsonSerializerOptions ExplorerJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IWidgetContract _contract;
    private readonly DashboardOptions _options;
    private readonly ExplorerOptions _explorer;
    private readonly TimeProvider _clock;

    /// <summary>Creates the validator.</summary>
    /// <remarks>Plain option values rather than IOptions: the application layer takes no hosting dependency.</remarks>
    public DashboardDefinitionValidator(
        IWidgetContract contract, DashboardOptions options, ExplorerOptions explorer, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(explorer);
        ArgumentNullException.ThrowIfNull(clock);

        _contract = contract;
        _options = options;
        _explorer = explorer;
        _clock = clock;
    }

    /// <inheritdoc />
    public DefinitionCheck Check(JsonElement definition)
    {
        var errors = new Errors();

        if (definition.ValueKind != JsonValueKind.Object)
        {
            errors.Add("$", "A dashboard definition is a JSON object.");
            return Refused(errors);
        }

        UnknownKeys(definition, RootKeys, string.Empty, errors);

        if (!definition.TryGetProperty("schemaVersion", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var schemaVersion)
            || schemaVersion != SchemaVersion)
        {
            errors.Add("schemaVersion", $"The schema version must be {SchemaVersion}.");
        }

        var refresh = 0;
        if (definition.TryGetProperty("refreshMinutes", out var refreshElement) && refreshElement.ValueKind != JsonValueKind.Null)
        {
            if (refreshElement.ValueKind != JsonValueKind.Number
                || !refreshElement.TryGetInt32(out refresh)
                || !_contract.Lens.RefreshMinutes.Contains(refresh))
            {
                errors.Add("refreshMinutes", $"Refresh is one of {string.Join(", ", _contract.Lens.RefreshMinutes)} minutes (0 is off).");
            }
        }

        var defaults = new DashboardLens();
        if (definition.TryGetProperty("defaults", out var defaultsElement) && defaultsElement.ValueKind != JsonValueKind.Null)
        {
            defaults = Lens(defaultsElement, "defaults", spec: null, errors);
        }

        var widgets = new List<ValidatedWidget>();
        var configs = new List<JsonObject>();
        if (!definition.TryGetProperty("widgets", out var widgetsElement) || widgetsElement.ValueKind != JsonValueKind.Array)
        {
            errors.Add("widgets", "A definition lists its widgets in an array, which may be empty.");
        }
        else
        {
            if (widgetsElement.GetArrayLength() > _options.MaxWidgets)
            {
                errors.Add("widgets", $"A dashboard holds at most {_options.MaxWidgets} widgets.");
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var widget in widgetsElement.EnumerateArray())
            {
                if (Widget(widget, $"widgets[{index}]", ids, errors) is { } checkedWidget)
                {
                    widgets.Add(checkedWidget.Widget);
                    configs.Add(checkedWidget.Config);
                }

                index++;
            }
        }

        if (errors.Count > 0)
        {
            return Refused(errors);
        }

        var normalised = Normalise(defaults, refresh, widgets, configs);
        if (Encoding.UTF8.GetByteCount(normalised) > _options.MaxDefinitionBytes)
        {
            errors.Add("$", $"The dashboard is larger than {_options.MaxDefinitionBytes:N0} bytes. Remove widgets or shorten their settings.");
            return Refused(errors);
        }

        return new DefinitionCheck(new ValidatedDefinition(SchemaVersion, defaults, refresh, widgets, normalised), []);
    }

    // ------------------------------------------------------------------ widgets

    private (ValidatedWidget Widget, JsonObject Config)? Widget(JsonElement widget, string path, HashSet<string> ids, Errors errors)
    {
        if (widget.ValueKind != JsonValueKind.Object)
        {
            errors.Add(path, "A widget is a JSON object.");
            return null;
        }

        UnknownKeys(widget, WidgetKeys, path, errors);

        string? id = null;
        if (!widget.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String
            || !InstanceId.IsMatch(id = idElement.GetString()!))
        {
            errors.Add($"{path}.id", "A widget id is w_ followed by 6 to 32 lower-case letters or digits.");
            id = null;
        }
        else if (!ids.Add(id))
        {
            errors.Add($"{path}.id", $"Two widgets share the id {id}.");
        }

        if (!widget.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String
            || !_contract.Types.TryGetValue(typeElement.GetString()!, out var spec))
        {
            errors.Add($"{path}.type", "Unknown widget type.");
            return null;
        }

        if (!widget.TryGetProperty("configVersion", out var versionElement) || versionElement.ValueKind != JsonValueKind.Number
            || !versionElement.TryGetInt32(out var configVersion) || configVersion != spec.ConfigVersion)
        {
            errors.Add($"{path}.configVersion", $"This build reads {spec.Type} settings version {spec.ConfigVersion}: upgrade this widget before saving.");
            return null;
        }

        var title = Text(widget, "title", path, _contract.Limits.TitleMaxLength, errors);
        var subtitle = Text(widget, "subtitle", path, _contract.Limits.SubtitleMaxLength, errors);

        var config = Config(widget, spec, path, errors);
        var overrides = widget.TryGetProperty("overrides", out var overridesElement) && overridesElement.ValueKind != JsonValueKind.Null
            ? Lens(overridesElement, $"{path}.overrides", spec, errors)
            : new DashboardLens();
        var layout = Layout(widget, spec, path, errors);

        if (id is null || config is null || layout is null)
        {
            return null;
        }

        return (new ValidatedWidget(id, spec.Type, spec.ConfigVersion, title, subtitle,
            JsonSerializer.SerializeToElement(config), overrides, layout), config);
    }

    private static string? Text(JsonElement widget, string name, string path, int maxLength, Errors errors)
    {
        if (!widget.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{path}.{name}", "Text or null.");
            return null;
        }

        var text = element.GetString()!.Trim();
        if (text.Length > maxLength)
        {
            errors.Add($"{path}.{name}", $"At most {maxLength} characters.");
            return null;
        }

        if (!ContractText.Storable(text))
        {
            errors.Add($"{path}.{name}", "Contains a character that cannot be stored.");
            return null;
        }

        return text.Length == 0 ? null : text;
    }

    private WidgetLayout? Layout(JsonElement widget, WidgetTypeSpec spec, string path, Errors errors)
    {
        if (!widget.TryGetProperty("layout", out var layout) || layout.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}.layout", "A widget needs a position: { x, y, w, h }.");
            return null;
        }

        UnknownKeys(layout, LayoutKeys, $"{path}.layout", errors);

        int? Coordinate(string name)
        {
            if (layout.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number;
            }

            errors.Add($"{path}.layout.{name}", "A whole number.");
            return null;
        }

        var (x, y, w, h) = (Coordinate("x"), Coordinate("y"), Coordinate("w"), Coordinate("h"));
        if (x is null || y is null || w is null || h is null)
        {
            return null;
        }

        var columns = _contract.Limits.GridColumns;
        var fits = true;
        void Refuse(string field, string message)
        {
            errors.Add($"{path}.layout.{field}", message);
            fits = false;
        }

        if (x < 0) Refuse("x", "Not left of the grid.");
        if (y < 0 || y > _contract.Limits.MaxRows) Refuse("y", $"Between row 0 and row {_contract.Limits.MaxRows}.");
        if (w < spec.Size.MinW || w > columns) Refuse("w", $"Between {spec.Size.MinW} and {columns} columns wide.");
        if (h < spec.Size.MinH || h > _contract.Limits.MaxWidgetHeight) Refuse("h", $"Between {spec.Size.MinH} and {_contract.Limits.MaxWidgetHeight} rows tall.");
        // Never x + w: an x near int.MaxValue wrapped negative and passed. w is within [minW, columns] here.
        if (x > columns - w) Refuse("x", $"The widget runs past column {columns}.");

        return fits ? new WidgetLayout(x.Value, y.Value, w.Value, h.Value) : null;
    }

    // ------------------------------------------------------------------- config

    private JsonObject? Config(JsonElement widget, WidgetTypeSpec spec, string path, Errors errors)
    {
        var given = widget.TryGetProperty("config", out var configElement) ? configElement : default;
        if (given.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null))
        {
            errors.Add($"{path}.config", "Settings are a JSON object.");
            return null;
        }

        var before = errors.Count;
        var result = new JsonObject();

        if (given.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in given.EnumerateObject())
            {
                if (!spec.Config.ContainsKey(property.Name))
                {
                    errors.Add($"{path}.config.{property.Name}", $"{spec.Type} has no setting called {property.Name}.");
                }
            }
        }

        foreach (var (name, field) in spec.Config)
        {
            var fieldPath = $"{path}.config.{name}";
            var present = given.ValueKind == JsonValueKind.Object && given.TryGetProperty(name, out var value)
                ? value
                : default;

            if (present.ValueKind == JsonValueKind.Undefined)
            {
                if (field.Required)
                {
                    errors.Add(fieldPath, "Required.");
                }
                else
                {
                    result[name] = field.Default.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(field.Default.GetRawText());
                }

                continue;
            }

            result[name] = Field(present, field, fieldPath, given, errors);
        }

        if (errors.Count > before)
        {
            return null;
        }

        if (Encoding.UTF8.GetByteCount(result.ToJsonString()) > _options.MaxWidgetConfigBytes)
        {
            errors.Add($"{path}.config", $"Settings are larger than {_options.MaxWidgetConfigBytes:N0} bytes.");
            return null;
        }

        return result;
    }

    private JsonNode? Field(JsonElement value, ConfigFieldSpec field, string path, JsonElement config, Errors errors)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (field.Nullable && !field.Required)
            {
                return null;
            }

            errors.Add(path, field.Required ? "Required." : "A value is needed here.");
            return null;
        }

        switch (field.Kind)
        {
            case "enum":
                if (value.ValueKind == JsonValueKind.String && field.Values!.Contains(value.GetString()!, StringComparer.Ordinal))
                {
                    return JsonValue.Create(value.GetString());
                }

                errors.Add(path, $"One of: {string.Join(", ", field.Values!)}.");
                return null;

            case "enumList":
            {
                if (value.ValueKind != JsonValueKind.Array)
                {
                    errors.Add(path, "A list.");
                    return null;
                }

                var items = new List<string>();
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || !field.Values!.Contains(item.GetString()!, StringComparer.Ordinal))
                    {
                        errors.Add(path, $"Each item one of: {string.Join(", ", field.Values!)}.");
                        return null;
                    }

                    items.Add(item.GetString()!);
                }

                if (items.Distinct(StringComparer.Ordinal).Count() != items.Count)
                {
                    errors.Add(path, "An item appears twice.");
                    return null;
                }

                if (!Count(items.Count, field, path, errors))
                {
                    return null;
                }

                return new JsonArray([.. items.Select(i => (JsonNode?)JsonValue.Create(i))]);
            }

            case "int":
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
                    && (field.Min is null || number >= field.Min) && (field.Max is null || number <= field.Max))
                {
                    return JsonValue.Create(number);
                }

                errors.Add(path, $"A whole number{Bounds(field)}.");
                return null;

            case "bool":
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return JsonValue.Create(value.GetBoolean());
                }

                errors.Add(path, "true or false.");
                return null;

            case "string":
            {
                if (value.ValueKind != JsonValueKind.String)
                {
                    errors.Add(path, "Text.");
                    return null;
                }

                var text = value.GetString()!.Trim();
                if (field.MaxLength is { } max && text.Length > max)
                {
                    errors.Add(path, $"At most {max} characters.");
                    return null;
                }

                if (!ContractText.Storable(text))
                {
                    errors.Add(path, "Contains a character that cannot be stored.");
                    return null;
                }

                if (field.Required && text.Length == 0)
                {
                    errors.Add(path, "Required.");
                    return null;
                }

                if (text.Length > 0 && field.Pattern is { } pattern && !ContractText.Matches(text, pattern))
                {
                    errors.Add(path, "Not in the expected format.");
                    return null;
                }

                return JsonValue.Create(text);
            }

            case "stringList":
            {
                if (value.ValueKind != JsonValueKind.Array)
                {
                    errors.Add(path, "A list.");
                    return null;
                }

                var items = new List<string>();
                foreach (var item in value.EnumerateArray())
                {
                    var text = item.ValueKind == JsonValueKind.String ? item.GetString()!.Trim() : null;
                    if (string.IsNullOrEmpty(text) || (field.MaxLength is { } max && text.Length > max) || !ContractText.Storable(text))
                    {
                        errors.Add(path, $"Each item is text of 1 to {field.MaxLength ?? 100} characters.");
                        return null;
                    }

                    items.Add(text);
                }

                if (!Count(items.Count, field, path, errors))
                {
                    return null;
                }

                return new JsonArray([.. items.Select(i => (JsonNode?)JsonValue.Create(i))]);
            }

            case "date":
                if (value.ValueKind == JsonValueKind.String
                    && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                {
                    return JsonValue.Create(value.GetString());
                }

                errors.Add(path, "A date, yyyy-MM-dd.");
                return null;

            case "range":
                if (value.ValueKind == JsonValueKind.String && DashboardLensResolver.IsValidRange(value.GetString(), _contract.Lens))
                {
                    return JsonValue.Create(value.GetString());
                }

                errors.Add(path, "A date range: all, last7 … last365, or yyyy-MM-dd..yyyy-MM-dd.");
                return null;

            case "explorerQuery":
                return ExplorerQuery(value, config, path, errors);

            default:
                errors.Add(path, $"The contract names an unknown kind, {field.Kind}.");
                return null;
        }
    }

    private static bool Count(int count, ConfigFieldSpec field, string path, Errors errors)
    {
        if ((field.MinItems is { } min && count < min) || (field.MaxItems is { } max && count > max))
        {
            errors.Add(path, $"Between {field.MinItems ?? 0} and {field.MaxItems?.ToString(CultureInfo.InvariantCulture) ?? "any number of"} items.");
            return false;
        }

        return true;
    }

    private static string Bounds(ConfigFieldSpec field) => (field.Min, field.Max) switch
    {
        ({ } min, { } max) => $" from {min} to {max}",
        ({ } min, null) => $" of at least {min}",
        (null, { } max) => $" of at most {max}",
        _ => string.Empty,
    };

    /// <summary>
    /// An aggregate Explorer query that may be stored and shared: valid by the Explorer's own rules,
    /// aggregated, and needing nothing beyond explorer.query (ADR-016 §10).
    /// </summary>
    private JsonNode? ExplorerQuery(JsonElement value, JsonElement config, string path, Errors errors)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            errors.Add(path, "An Explorer query.");
            return null;
        }

        ExplorerQueryRequest? request;
        try
        {
            request = value.Deserialize<ExplorerQueryRequest>(ExplorerJson);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null)
        {
            errors.Add(path, "Not an Explorer query this version understands.");
            return null;
        }

        // Stored without paging; the widget's own row limit applies when it runs.
        request = request with { Page = 1, PageSize = Math.Min(50, _explorer.MaxPageSize) };

        // An Events query needs a date range, and a widget that says "the last N days" stores the N,
        // not the dates: they are filled in when it runs. Check it as it will run.
        var checkedAs = request;
        if (request.Dataset == ExplorerDataset.Events
            && config.ValueKind == JsonValueKind.Object
            && config.TryGetProperty("lastDays", out var lastDays) && lastDays.ValueKind == JsonValueKind.Number
            && lastDays.TryGetInt32(out var days) && days > 0)
        {
            var to = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
            var range = new ExplorerFilter(
                Field: "date", Operator: ExplorerOperator.Between,
                Values: [to.AddDays(-(days - 1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)]);
            checkedAs = request with
            {
                Where = request.Where is null ? range : new ExplorerFilter(Logic: ExplorerLogic.And, Children: [range, request.Where]),
            };
        }

        var (query, problems) = ExplorerValidator.Check(checkedAs, _explorer);
        if (query is null)
        {
            var first = problems.Count > 0 ? problems[0] : null;
            errors.Add(first is null ? path : $"{path}.{first.Path}", first?.Message ?? "Not a valid Explorer query.");
            return null;
        }

        if (query.Measures.Count == 0)
        {
            errors.Add(path, "A dashboard query counts or summarises: add at least one measure.");
            return null;
        }

        if (query.Permissions.Count != 1 || !query.Permissions.Contains(Identity.Permissions.ExplorerQuery))
        {
            errors.Add(path, "A dashboard query may not filter, group or show numbers, SIMs or handsets; count them instead.");
            return null;
        }

        return JsonSerializer.SerializeToNode(request, ExplorerJson);
    }

    // --------------------------------------------------------------------- lens

    private DashboardLens Lens(JsonElement lens, string path, WidgetTypeSpec? spec, Errors errors)
    {
        if (lens.ValueKind != JsonValueKind.Object)
        {
            errors.Add(path, "A JSON object of view settings.");
            return new DashboardLens();
        }

        UnknownKeys(lens, LensKeys, path, errors);

        string? countBy = null, range = null, tac = null;
        bool? includeUnknown = null;
        var text = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var property in lens.EnumerateObject())
        {
            if (!LensKeys.Contains(property.Name, StringComparer.Ordinal) || property.Value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            var fieldPath = $"{path}.{property.Name}";
            if (spec is not null && !Supports(spec, property.Name, property.Value))
            {
                errors.Add(fieldPath, $"{spec.Type} cannot be set to this; it does not apply to it.");
                continue;
            }

            var value = property.Value;
            switch (property.Name)
            {
                case "countBy" when value.ValueKind == JsonValueKind.String && _contract.Lens.CountBy.Contains(value.GetString()!, StringComparer.Ordinal):
                    countBy = value.GetString();
                    break;
                case "includeUnknownDevice" when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    includeUnknown = value.GetBoolean();
                    break;
                case "range" when value.ValueKind == JsonValueKind.String && DashboardLensResolver.IsValidRange(value.GetString(), _contract.Lens):
                    range = value.GetString();
                    break;
                case "tac" when value.ValueKind == JsonValueKind.String && ContractText.Matches(value.GetString()!, _contract.Lens.TacPattern):
                    tac = value.GetString();
                    break;
                case "vendor" or "deviceType" or "operatingSystem"
                    when value.ValueKind == JsonValueKind.String
                         && value.GetString()!.Trim() is { Length: > 0 } trimmed
                         && trimmed.Length <= _contract.Lens.TextFilterMaxLength
                         && ContractText.Storable(trimmed):
                    text[property.Name] = trimmed;
                    break;
                default:
                    errors.Add(fieldPath, "Not a value this view setting takes.");
                    break;
            }
        }

        return new DashboardLens(
            countBy, includeUnknown, range,
            text.GetValueOrDefault("vendor"), text.GetValueOrDefault("deviceType"), text.GetValueOrDefault("operatingSystem"), tac);
    }

    private static bool Supports(WidgetTypeSpec spec, string key, JsonElement value) => key switch
    {
        "countBy" => spec.SupportsCountBy
                     && value.ValueKind == JsonValueKind.String
                     && spec.Measures.Contains(value.GetString()!, StringComparer.Ordinal),
        "range" => spec.SupportsRange,
        _ => spec.Filters.Contains(key, StringComparer.Ordinal),
    };

    // --------------------------------------------------------------- normalised

    private static string Normalise(DashboardLens defaults, int refresh, List<ValidatedWidget> widgets, List<JsonObject> configs)
    {
        var root = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["defaults"] = LensNode(defaults),
            ["refreshMinutes"] = refresh,
        };

        var array = new JsonArray();
        for (var i = 0; i < widgets.Count; i++)
        {
            var w = widgets[i];
            array.Add(new JsonObject
            {
                ["id"] = w.Id,
                ["type"] = w.Type,
                ["configVersion"] = w.ConfigVersion,
                ["title"] = w.Title,
                ["subtitle"] = w.Subtitle,
                ["config"] = configs[i].DeepClone(),
                ["overrides"] = LensNode(w.Overrides),
                ["layout"] = new JsonObject
                {
                    ["x"] = w.Layout.X,
                    ["y"] = w.Layout.Y,
                    ["w"] = w.Layout.W,
                    ["h"] = w.Layout.H,
                },
            });
        }

        root["widgets"] = array;
        return root.ToJsonString();
    }

    /// <summary>The set lens fields only, always in the same order.</summary>
    private static JsonObject LensNode(DashboardLens lens)
    {
        var node = new JsonObject();
        if (lens.CountBy is not null) node["countBy"] = lens.CountBy;
        if (lens.IncludeUnknownDevice is { } include) node["includeUnknownDevice"] = include;
        if (lens.Range is not null) node["range"] = lens.Range;
        if (lens.Vendor is not null) node["vendor"] = lens.Vendor;
        if (lens.DeviceType is not null) node["deviceType"] = lens.DeviceType;
        if (lens.OperatingSystem is not null) node["operatingSystem"] = lens.OperatingSystem;
        if (lens.Tac is not null) node["tac"] = lens.Tac;
        return node;
    }

    private static void UnknownKeys(JsonElement element, string[] allowed, string path, Errors errors)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                errors.Add(path.Length == 0 ? property.Name : $"{path}.{property.Name}", "Not a field a dashboard can hold.");
            }
        }
    }

    private static DefinitionCheck Refused(Errors errors) => new(null, errors.List);

    /// <summary>The errors so far, capped at <see cref="MaxErrors"/>.</summary>
    private sealed class Errors
    {
        public List<DefinitionError> List { get; } = [];

        /// <summary>Every error found, including those past the cap: what decides refusal.</summary>
        public int Count { get; private set; }

        public void Add(string path, string message)
        {
            Count++;
            if (List.Count < MaxErrors)
            {
                List.Add(new DefinitionError(path, message));
            }
        }
    }
}
