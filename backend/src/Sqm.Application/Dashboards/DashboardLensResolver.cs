using System.Globalization;
using System.Text.RegularExpressions;
using Sqm.Contracts.Dashboards;

namespace Sqm.Application.Dashboards;

/// <summary>
/// The lens a widget is computed with (ADR-016 §8). The same rules as
/// <c>frontend/src/features/dashboard/composables/lens.ts</c>; a test on each side pins them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Precedence:</b> the dashboard's defaults, then the URL lens, then the widget's own override - and
/// only for fields the widget's type supports. A field it does not support is null in the result, so a
/// network-wide card can never be shown as if a vendor filter applied to it.
/// </para>
/// <para>
/// <b>Relative ranges count back from the latest day in the data</b>, not from today: files arrive
/// weeks late, and "the last 30 days" of a feed that ends a month ago would otherwise be empty.
/// </para>
/// </remarks>
public static class DashboardLensResolver
{
    /// <summary>The count unit when nothing chooses one.</summary>
    public const string DefaultCountBy = "bindings";

    /// <summary>The range when nothing chooses one: everything the source has.</summary>
    public const string DefaultRange = "all";

    /// <summary>The effective lens for one widget.</summary>
    /// <param name="spec">The widget's type.</param>
    /// <param name="defaults">The dashboard's defaults.</param>
    /// <param name="url">The URL lens, already sanitised; null fields fall through to the defaults.</param>
    /// <param name="overrides">The widget's override, already validated against the type.</param>
    public static DashboardLens Effective(WidgetTypeSpec spec, DashboardLens defaults, DashboardLens? url, DashboardLens overrides)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(overrides);
        url ??= new DashboardLens();

        string? countBy = null;
        if (spec.SupportsCountBy)
        {
            countBy = First(overrides.CountBy, url.CountBy, defaults.CountBy) ?? DefaultCountBy;
            if (!spec.Measures.Contains(countBy, StringComparer.Ordinal))
            {
                countBy = spec.Measures[0];
            }
        }

        bool? includeUnknown = Supports(spec, "includeUnknownDevice")
            ? overrides.IncludeUnknownDevice ?? url.IncludeUnknownDevice ?? defaults.IncludeUnknownDevice ?? true
            : null;

        return new DashboardLens(
            CountBy: countBy,
            IncludeUnknownDevice: includeUnknown,
            Range: spec.SupportsRange ? First(overrides.Range, url.Range, defaults.Range) ?? DefaultRange : null,
            Vendor: Supports(spec, "vendor") ? First(overrides.Vendor, url.Vendor, defaults.Vendor) : null,
            DeviceType: Supports(spec, "deviceType") ? First(overrides.DeviceType, url.DeviceType, defaults.DeviceType) : null,
            OperatingSystem: Supports(spec, "operatingSystem") ? First(overrides.OperatingSystem, url.OperatingSystem, defaults.OperatingSystem) : null,
            Tac: Supports(spec, "tac") ? First(overrides.Tac, url.Tac, defaults.Tac) : null);
    }

    /// <summary>
    /// The days a range token covers, given the latest day in the data. Null bounds mean open: "all"
    /// is (null, null), and a relative range is open until the data's last day is known.
    /// </summary>
    public static (DateOnly? From, DateOnly? To) ResolveRange(string? range, DateOnly? dataThrough)
    {
        if (string.IsNullOrEmpty(range) || range == "all")
        {
            return (null, null);
        }

        if (TryParseRelative(range, out var days))
        {
            return dataThrough is { } through ? (through.AddDays(-(days - 1)), through) : (null, null);
        }

        return TryParseExplicit(range, out var from, out var to) ? (from, to) : (null, null);
    }

    /// <summary>Whether a range token is one the contract allows.</summary>
    public static bool IsValidRange(string? range, LensSpec lens)
    {
        ArgumentNullException.ThrowIfNull(lens);
        if (range is null)
        {
            return false;
        }

        if (lens.Ranges.Contains(range, StringComparer.Ordinal))
        {
            return true;
        }

        return Regex.IsMatch(range, lens.RangePattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))
               && TryParseExplicit(range, out var from, out var to)
               && from <= to;
    }

    /// <summary>
    /// The URL lens with every value the contract would refuse dropped, the way the dashboard's URL
    /// handling degrades a hand-edited link to "no filter" rather than failing.
    /// </summary>
    public static DashboardLens Sanitize(DashboardLens? lens, LensSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (lens is null)
        {
            return new DashboardLens();
        }

        return new DashboardLens(
            CountBy: lens.CountBy is { } c && spec.CountBy.Contains(c, StringComparer.Ordinal) ? c : null,
            IncludeUnknownDevice: lens.IncludeUnknownDevice,
            Range: IsValidRange(lens.Range, spec) ? lens.Range : null,
            Vendor: Text(lens.Vendor, spec),
            DeviceType: Text(lens.DeviceType, spec),
            OperatingSystem: Text(lens.OperatingSystem, spec),
            Tac: lens.Tac is { } t && Regex.IsMatch(t, spec.TacPattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) ? t : null);
    }

    /// <summary>Whether a token is a relative range, and how many days it covers.</summary>
    public static bool TryParseRelative(string range, out int days)
    {
        ArgumentNullException.ThrowIfNull(range);
        days = 0;
        return range.StartsWith("last", StringComparison.Ordinal)
               && int.TryParse(range.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out days)
               && days > 0;
    }

    /// <summary>Whether a token is an explicit range, <c>yyyy-MM-dd..yyyy-MM-dd</c>.</summary>
    public static bool TryParseExplicit(string range, out DateOnly from, out DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(range);
        from = default;
        to = default;

        var parts = range.Split("..", 2, StringSplitOptions.None);
        return parts.Length == 2
               && DateOnly.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out from)
               && DateOnly.TryParseExact(parts[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out to);
    }

    private static bool Supports(WidgetTypeSpec spec, string filter) => spec.Filters.Contains(filter, StringComparer.Ordinal);

    private static string? First(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string? Text(string? value, LensSpec spec)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > spec.TextFilterMaxLength ? null : trimmed;
    }
}
