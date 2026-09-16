using Sqm.Application.Abstractions;
using Sqm.Contracts.Dashboard;

using Sqm.Api.Auth;
using Sqm.Application.Identity;

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

        // One permission for the whole group. Every route here reads the same aggregate marts,
        // so splitting them would produce permissions nobody could explain the difference between.
        var group = app.MapGroup("/api/v1/dashboard")
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.DashboardView)
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

        group.MapGet("/daily-changes", GetDailyChangesAsync)
            .WithName("GetDailyChanges")
            .WithSummary("Daily adds, removes, net change and running active-binding total.");

        group.MapGet("/daily-churn", GetDailyChurnAsync)
            .WithName("GetDailyChurn")
            .WithSummary("Daily SIM changes and handset changes.");

        group.MapGet("/vendor-movement", GetVendorMovementAsync)
            .WithName("GetVendorMovement")
            .WithSummary(
                "Vendors by movement, share or growth, over a date range. All three measures are "
                + "returned whichever the ranking, so the widget switches without a round trip.");

        group.MapGet("/capabilities", GetCapabilitiesAsync)
            .WithName("GetCapabilitySupport")
            .WithSummary("Network and SIM capability of the active device population: LTE, 5G, eSIM.");

        return app;
    }

    private static async Task<IResult> GetDailyChangesAsync(
        IDeviceAnalyticsStore store, CancellationToken ct)
    {
        var rows = await store.GetDailyChangesAsync(ct).ConfigureAwait(false);
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetDailyChurnAsync(
        IDeviceAnalyticsStore store, CancellationToken ct)
    {
        var rows = await store.GetDailyChurnAsync(ct).ConfigureAwait(false);
        return Results.Ok(rows);
    }

    /// <summary>
    /// The vendor widget's data.
    /// </summary>
    /// <remarks>
    /// The dates are nullable, not defaulted, for the reason the user list already learned: a
    /// minimal API treats a non-nullable parameter as REQUIRED from the query string. Absent
    /// means "the whole span the feed covers", resolved in the store from the data rather than
    /// guessed here.
    /// </remarks>
    private static async Task<IResult> GetVendorMovementAsync(
        IDeviceAnalyticsStore store,
        CancellationToken ct,
        DateOnly? from = null,
        DateOnly? to = null,
        string? rank = null,
        int limit = 8)
    {
        // An unrecognised ranking falls back to the default rather than erroring. This arrives
        // from URL state, and a stale bookmark should show the widget rather than a validation
        // message.
        var ranking = rank?.ToLowerInvariant() switch
        {
            "share" => VendorRanking.Share,
            "growth" => VendorRanking.Growth,
            _ => VendorRanking.Movement,
        };

        if (from is { } start && to is { } end && start > end)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["from"] = ["The start of the range is after its end."],
            });
        }

        var result = await store.GetVendorMovementAsync(from, to, ranking, limit, ct)
            .ConfigureAwait(false);

        return Results.Ok(result);
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

    /// <summary>
    /// Distribution across a dimension, under the same filters as everything else on the page.
    /// </summary>
    /// <remarks>
    /// This used to declare only <c>manufacturer</c> and <c>vendor</c> and pass nulls for the
    /// rest. The dashboard sends every active filter to every widget, and a minimal API simply
    /// does not bind a query parameter it has not declared - so drilling into an OS or a device
    /// type left this card answering the unfiltered question while the page above it displayed a
    /// chip saying otherwise. Nothing errored; the card was just answering a different question
    /// from the one on screen. The store has always applied whatever it was given.
    /// </remarks>
    private static async Task<IResult> GetDistributionAsync(
        string dimension,
        IDeviceAnalyticsStore store,
        CancellationToken ct,
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

        if (!TryParseCountBy(countBy, out var measure))
        {
            return InvalidCountBy(countBy!);
        }

        var filter = BuildFilter(
            manufacturer, vendor, deviceType, operatingSystem, tac, msisdnPrefix,
            null, null, includeUnknownDevice);

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
