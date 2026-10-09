using System.Text.Json;

namespace Sqm.Application.Dashboards;

/// <summary>
/// The widget allow-list, loaded from the embedded copy of
/// <c>frontend/src/features/dashboard/registry/widget-contract.json</c> (ADR-016 §3).
/// </summary>
/// <remarks>
/// <para>
/// One file for both sides on purpose. The frontend registry is typed and tested against it; this
/// assembly embeds it (see Sqm.Application.csproj) and the validator refuses anything it does not
/// name. A widget type or a setting therefore cannot exist on one side only, and adding one is
/// visible in review as a change to this file.
/// </para>
/// <para>
/// Read once and immutable. A malformed contract fails at startup, by name, rather than at the first
/// save.
/// </para>
/// </remarks>
public sealed class WidgetContract : IWidgetContract
{
    /// <summary>The embedded resource's name.</summary>
    public const string ResourceName = "Sqm.Application.Dashboards.widget-contract.json";

    private static readonly Lazy<WidgetContract> Embedded = new(() => Parse(ReadEmbedded()));

    private WidgetContract(
        int contractVersion, IReadOnlyDictionary<string, WidgetTypeSpec> types, LensSpec lens, ContractLimits limits)
    {
        ContractVersion = contractVersion;
        Types = types;
        Lens = lens;
        Limits = limits;
    }

    /// <inheritdoc />
    public int ContractVersion { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, WidgetTypeSpec> Types { get; }

    /// <inheritdoc />
    public LensSpec Lens { get; }

    /// <inheritdoc />
    public ContractLimits Limits { get; }

    /// <summary>The contract embedded in this assembly.</summary>
    public static WidgetContract Load() => Embedded.Value;

    /// <summary>A contract from JSON text - for tests that need a contract of their own.</summary>
    public static WidgetContract Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var limits = root.GetProperty("limits");
        var lens = root.GetProperty("lens");

        var types = new Dictionary<string, WidgetTypeSpec>(StringComparer.Ordinal);
        foreach (var type in root.GetProperty("types").EnumerateObject())
        {
            types.Add(type.Name, ParseType(type.Name, type.Value));
        }

        return new WidgetContract(
            root.GetProperty("contractVersion").GetInt32(),
            types,
            new LensSpec(
                Strings(lens.GetProperty("countBy")),
                Strings(lens.GetProperty("ranges")),
                lens.GetProperty("rangePattern").GetString()!,
                lens.GetProperty("textFilterMaxLength").GetInt32(),
                lens.GetProperty("tacPattern").GetString()!,
                [.. lens.GetProperty("refreshMinutes").EnumerateArray().Select(e => e.GetInt32())]),
            new ContractLimits(
                limits.GetProperty("titleMaxLength").GetInt32(),
                limits.GetProperty("subtitleMaxLength").GetInt32(),
                limits.GetProperty("gridColumns").GetInt32(),
                limits.GetProperty("maxRows").GetInt32(),
                limits.GetProperty("maxWidgetHeight").GetInt32()));
    }

    private static string ReadEmbedded()
    {
        using var stream = typeof(WidgetContract).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"The widget contract is not embedded ({ResourceName}). Sqm.Application.csproj links "
                + "frontend/src/features/dashboard/registry/widget-contract.json; build from the whole repository.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static WidgetTypeSpec ParseType(string name, JsonElement type)
    {
        PermissionsByConfigSpec? byConfig = null;
        if (type.TryGetProperty("permissionsByConfig", out var by))
        {
            byConfig = new PermissionsByConfigSpec(
                by.GetProperty("field").GetString()!,
                by.GetProperty("map").EnumerateObject()
                    .ToDictionary(p => p.Name, p => (IReadOnlyList<string>)Strings(p.Value), StringComparer.Ordinal));
        }

        var size = type.GetProperty("size");
        var config = new Dictionary<string, ConfigFieldSpec>(StringComparer.Ordinal);
        foreach (var field in type.GetProperty("config").EnumerateObject())
        {
            config.Add(field.Name, ParseField(field.Value));
        }

        return new WidgetTypeSpec(
            name,
            type.GetProperty("configVersion").GetInt32(),
            type.GetProperty("group").GetString()!,
            Strings(type.GetProperty("permissions")),
            byConfig,
            Strings(type.GetProperty("filters")),
            Strings(type.GetProperty("measures")),
            type.GetProperty("time").GetString()!,
            type.GetProperty("execution").GetString()!,
            new WidgetSizeSpec(
                size.GetProperty("w").GetInt32(), size.GetProperty("h").GetInt32(),
                size.GetProperty("minW").GetInt32(), size.GetProperty("minH").GetInt32()),
            config);
    }

    private static ConfigFieldSpec ParseField(JsonElement field) => new(
        field.GetProperty("kind").GetString()!,
        Bool(field, "required"),
        Bool(field, "nullable"),
        field.TryGetProperty("default", out var d) ? d.Clone() : default,
        field.TryGetProperty("values", out var values) ? Strings(values) : null,
        field.TryGetProperty("min", out var min) ? min.GetInt64() : null,
        field.TryGetProperty("max", out var max) ? max.GetInt64() : null,
        field.TryGetProperty("maxLength", out var maxLength) ? maxLength.GetInt32() : null,
        field.TryGetProperty("pattern", out var pattern) ? pattern.GetString() : null,
        field.TryGetProperty("minItems", out var minItems) ? minItems.GetInt32() : null,
        field.TryGetProperty("maxItems", out var maxItems) ? maxItems.GetInt32() : null);

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string[] Strings(JsonElement array) =>
        [.. array.EnumerateArray().Select(e => e.GetString()!)];
}
