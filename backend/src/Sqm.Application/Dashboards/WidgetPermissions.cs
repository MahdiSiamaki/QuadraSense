using System.Text.Json;
using Sqm.Application.Identity;

namespace Sqm.Application.Dashboards;

/// <summary>Which permissions one widget instance needs, and whether a person holds them.</summary>
public static class WidgetPermissions
{
    /// <summary>
    /// The permissions an instance needs: its type's fixed ones, plus those its config field selects
    /// (a risk list's rule decides between lookup.imei, lookup.imsi and lookup.subscriber).
    /// </summary>
    /// <param name="spec">The widget type.</param>
    /// <param name="config">The instance's config.</param>
    public static IReadOnlyList<string> Required(WidgetTypeSpec spec, JsonElement config)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var required = new List<string>(spec.Permissions);
        if (spec.PermissionsByConfig is { } byConfig
            && config.ValueKind == JsonValueKind.Object
            && config.TryGetProperty(byConfig.Field, out var value)
            && value.ValueKind == JsonValueKind.String
            && byConfig.Map.TryGetValue(value.GetString()!, out var extra))
        {
            required.AddRange(extra);
        }

        return [.. required.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The permissions the person lacks for this instance; empty when they may use it.</summary>
    public static IReadOnlyList<string> Missing(WidgetTypeSpec spec, JsonElement config, AuthenticatedUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return [.. Required(spec, config).Where(p => !user.Can(p))];
    }

    /// <summary>
    /// For a type whose permission follows a config field: the values of that field the person may
    /// choose. Null for other types.
    /// </summary>
    public static IReadOnlyList<string>? AvailableOptions(WidgetTypeSpec spec, AuthenticatedUser user)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(user);

        return spec.PermissionsByConfig is { } byConfig
            ? [.. byConfig.Map.Where(kv => kv.Value.All(user.Can)).Select(kv => kv.Key)]
            : null;
    }
}
