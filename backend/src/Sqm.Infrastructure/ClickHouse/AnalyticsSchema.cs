using Sqm.Application.Abstractions;

namespace Sqm.Infrastructure.ClickHouse;

/// <summary>
/// The allow-list that maps API dimensions to physical columns.
/// </summary>
/// <remarks>
/// This type is the injection boundary. User input selects an <see cref="AnalyticsDimension"/> enum value;
/// it never supplies a column name. Every value below is a literal written here at compile time, so there
/// is no path from a request to an arbitrary SQL identifier.
/// <para>
/// Filter <i>values</i> are handled separately, always as bound parameters.
/// </para>
/// </remarks>
internal static class AnalyticsSchema
{
    /// <summary>Resolves a dimension to its SQL expression.</summary>
    /// <exception cref="ArgumentOutOfRangeException">If a new enum value is added without mapping it.</exception>
    public static string ColumnFor(AnalyticsDimension dimension) => dimension switch
    {
        AnalyticsDimension.Manufacturer => "t.manufacturer",
        AnalyticsDimension.MarketingName => "t.marketingName",
        AnalyticsDimension.DeviceType => "t.deviceType",
        AnalyticsDimension.Tac => "b.tac",

        // Case and whitespace are normalised here because the raw GSMA field has 555 distinct values
        // that collapse to 460 on trimming alone ('Not Known', 'Not Known  ', 'Not known').
        AnalyticsDimension.OperatingSystem => "trim(t.operatingSystem)",

        // The curated vendor map collapses the 6 Samsung and 7 Motorola spellings. Falls back to the raw
        // manufacturer when a string has not been mapped yet, so a new vendor appears rather than vanishing.
        AnalyticsDimension.VendorCanonical => "coalesce(nullIf(v.vendor_canonical, ''), t.manufacturer)",

        _ => throw new ArgumentOutOfRangeException(nameof(dimension), dimension, "Unmapped dimension."),
    };

    /// <summary>A label for the "no device information" bucket.</summary>
    /// <remarks>
    /// Shown explicitly rather than dropped: it is 6.97% of the initial dump. Hiding it would understate
    /// every total on the dashboard by roughly a fifteenth.
    /// </remarks>
    public const string UnknownDeviceLabel = "(unknown device)";

    /// <summary>A label for a well-formed TAC that is absent from the GSMA database. Measured at 0.20%.</summary>
    public const string UnknownTacLabel = "(unknown TAC)";

    /// <summary>The <c>dimension</c> key used in <c>agg_dimension_daily</c>.</summary>
    public static string MartKeyFor(AnalyticsDimension dimension) => dimension switch
    {
        AnalyticsDimension.VendorCanonical => "vendor",
        AnalyticsDimension.Manufacturer => "manufacturer",
        AnalyticsDimension.MarketingName => "model",
        AnalyticsDimension.DeviceType => "deviceType",
        AnalyticsDimension.OperatingSystem => "os",
        AnalyticsDimension.Tac => "tac",
        _ => throw new ArgumentOutOfRangeException(nameof(dimension), dimension, "Unmapped dimension."),
    };

    /// <summary>Column in <c>agg_dimension_daily</c> holding the requested measure.</summary>
    public static string MartColumnFor(CountBy countBy) => countBy switch
    {
        CountBy.Bindings => "bindings",
        CountBy.Subscribers => "subscribers",
        CountBy.Handsets => "handsets",
        _ => throw new ArgumentOutOfRangeException(nameof(countBy), countBy, "Unmapped measure."),
    };

    /// <summary>
    /// Aggregate expression for the raw-table fallback, used when a filter is applied.
    /// </summary>
    /// <remarks>
    /// Handsets exclude non-14-digit IMEIs: the <c>000000</c> sentinel is one literal value shared by
    /// 8.8M bindings and is not a handset.
    /// </remarks>
    public static string RawMeasureFor(CountBy countBy) => countBy switch
    {
        CountBy.Bindings => "count()",
        CountBy.Subscribers => "uniq(b.msisdn)",
        CountBy.Handsets => "uniqIf(b.imei, length(b.imei) = 14)",
        _ => throw new ArgumentOutOfRangeException(nameof(countBy), countBy, "Unmapped measure."),
    };

    /// <summary>
    /// Column in <c>agg_kpi_daily</c> holding the population total for this measure.
    /// </summary>
    /// <remarks>
    /// Percentages are taken against the whole population, never against the returned page. Computing
    /// a share of the top-N subtotal makes the percentage depend on how many rows were requested —
    /// Samsung read 45.27% at limit 10 when its true share of all bindings is 41.85%.
    /// </remarks>
    public static string PopulationTotalFor(CountBy countBy) => countBy switch
    {
        CountBy.Bindings => "active_bindings",
        CountBy.Subscribers => "distinct_subscribers",
        CountBy.Handsets => "distinct_devices",
        _ => throw new ArgumentOutOfRangeException(nameof(countBy), countBy, "Unmapped measure."),
    };
}
