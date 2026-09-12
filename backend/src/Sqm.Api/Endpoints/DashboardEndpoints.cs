using Sqm.Application.Abstractions;
using Sqm.Contracts.Dashboard;

namespace Sqm.Api.Endpoints;

/// <summary>Dashboard read endpoints.</summary>
/// <remarks>
/// Route handlers stay thin on purpose: bind, validate, delegate, return. No business logic here —
/// query construction lives behind <see cref="IDeviceAnalyticsStore"/> so it can be tested without HTTP.
/// </remarks>
public static class DashboardEndpoints
{
    /// <summary>Registers the dashboard routes.</summary>
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/dashboard")
            .WithTags("Dashboard");

        group.MapGet("/kpi", GetKpiAsync)
            .WithName("GetKpiSummary")
            .WithSummary("Headline counters: active bindings, subscribers, devices, TAC coverage.");

        group.MapGet("/top/{dimension}", GetTopAsync)
            .WithName("GetTopDimension")
            .WithSummary("Top values of a dimension by active binding count.");

        group.MapGet("/distribution/{dimension}", GetDistributionAsync)
            .WithName("GetDistribution")
            .WithSummary("Distribution across a low-cardinality dimension such as device type.");

        group.MapGet("/changes", GetChangesAsync)
            .WithName("GetChangeSeries")
            .WithSummary("Adds, removes and net change per delivery sequence.");

        group.MapGet("/device-class-mix", GetDeviceClassMixAsync)
            .WithName("GetDeviceClassMix")
            .WithSummary("Handset versus machine mix: smartphone, feature phone, tablet, IoT/M2M.");

        group.MapGet("/capabilities", GetCapabilitiesAsync)
            .WithName("GetCapabilitySupport")
            .WithSummary("Network and SIM capability of the active device population: LTE, 5G, eSIM.");

        return app;
    }

    private static async Task<IResult> GetCapabilitiesAsync(
        IDeviceAnalyticsStore store, CancellationToken ct, string? countBy = null)
    {
        if (!TryParseCountBy(countBy, out var measure))
        {
            return InvalidCountBy(countBy!);
        }

        var rows = await store.GetCapabilitySupportAsync(measure, ct).ConfigureAwait(false);
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetDeviceClassMixAsync(
        IDeviceAnalyticsStore store, CancellationToken ct, string? countBy = null)
    {
        if (!TryParseCountBy(countBy, out var measure))
        {
            return InvalidCountBy(countBy!);
        }

        var rows = await store.GetDeviceClassMixAsync(measure, ct).ConfigureAwait(false);
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetKpiAsync(
        IDeviceAnalyticsStore store,
        CancellationToken ct,
        string? manufacturer = null,
        string? vendor = null,
        string? deviceType = null,
        string? operatingSystem = null,
        string? tac = null,
        string? msisdnPrefix = null,
        bool includeUnknownDevice = true)
    {
        var filter = BuildFilter(
            manufacturer, vendor, deviceType, operatingSystem, tac, msisdnPrefix,
            null, null, includeUnknownDevice);

        var result = await store.GetKpiSummaryAsync(filter, ct).ConfigureAwait(false);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetTopAsync(
        string dimension,
        IDeviceAnalyticsStore store,
        CancellationToken ct,
        int limit = 20,
        string? countBy = null,
        string? manufacturer = null,
        string? vendor = null,
        string? deviceType = null,
        string? operatingSystem = null,
        string? tac = null,
        string? msisdnPrefix = null,
        bool includeUnknownDevice = true)
    {
        if (!TryParseDimension(dimension, out var parsed))
        {
            return InvalidDimension(dimension);
        }

        var filter = BuildFilter(
            manufacturer, vendor, deviceType, operatingSystem, tac, msisdnPrefix,
            null, null, includeUnknownDevice);

        if (!TryParseCountBy(countBy, out var measure))
        {
            return InvalidCountBy(countBy!);
        }

        var rows = await store.GetTopDimensionAsync(parsed, filter, limit, measure, ct)
            .ConfigureAwait(false);
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetDistributionAsync(
        string dimension,
        IDeviceAnalyticsStore store,
        CancellationToken ct,
        string? countBy = null,
        string? manufacturer = null,
        string? vendor = null,
        bool includeUnknownDevice = true)
    {
        if (!TryParseDimension(dimension, out var parsed))
        {
            return InvalidDimension(dimension);
        }

        if (!TryParseCountBy(countBy, out var measure))
        {
            return InvalidCountBy(countBy!);
        }

        var filter = BuildFilter(
            manufacturer, vendor, null, null, null, null, null, null, includeUnknownDevice);

        var rows = await store.GetDistributionAsync(parsed, filter, measure, ct).ConfigureAwait(false);
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetChangesAsync(
        IDeviceAnalyticsStore store,
        CancellationToken ct,
        int? sequenceFrom = null,
        int? sequenceTo = null)
    {
        var filter = new DashboardFilter(SequenceFrom: sequenceFrom, SequenceTo: sequenceTo);
        var rows = await store.GetChangeSeriesAsync(filter, ct).ConfigureAwait(false);
        return Results.Ok(rows);
    }

    private static DashboardFilter BuildFilter(
        string? manufacturer, string? vendor, string? deviceType, string? operatingSystem,
        string? tac, string? msisdnPrefix, int? seqFrom, int? seqTo, bool includeUnknownDevice)
        => new(
            Manufacturer: manufacturer,
            VendorCanonical: vendor,
            DeviceType: deviceType,
            OperatingSystem: operatingSystem,
            Tac: tac,
            MsisdnPrefix: msisdnPrefix,
            SequenceFrom: seqFrom,
            SequenceTo: seqTo,
            IncludeUnknownDevice: includeUnknownDevice);

    /// <summary>
    /// Parses the dimension from the route.
    /// </summary>
    /// <remarks>
    /// The enum is the allow-list: an unrecognised value is refused here and never reaches SQL, so there
    /// is no path from a URL segment to a column name.
    /// </remarks>
    private static bool TryParseDimension(string value, out AnalyticsDimension dimension) =>
        Enum.TryParse(value, ignoreCase: true, out dimension)
        && Enum.IsDefined(dimension);

    /// <summary>
    /// Parses the measure. Absent means bindings — the historical default, kept so existing links
    /// and bookmarks keep returning what they always did.
    /// </summary>
    private static bool TryParseCountBy(string? value, out CountBy countBy)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            countBy = CountBy.Bindings;
            return true;
        }
        return Enum.TryParse(value, ignoreCase: true, out countBy) && Enum.IsDefined(countBy);
    }

    private static IResult InvalidCountBy(string value) =>
        Results.ValidationProblem(
            new Dictionary<string, string[]>
            {
                ["countBy"] =
                [
                    $"'{value}' is not a supported measure. Valid values: " +
                    string.Join(", ", Enum.GetNames<CountBy>()),
                ],
            },
            title: "Unsupported measure");

    private static IResult InvalidDimension(string value) =>
        Results.ValidationProblem(
            new Dictionary<string, string[]>
            {
                ["dimension"] =
                [
                    $"'{value}' is not a supported dimension. Valid values: " +
                    string.Join(", ", Enum.GetNames<AnalyticsDimension>()),
                ],
            },
            title: "Unsupported dimension");
}
