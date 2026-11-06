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
    /// Left out of the counts since 2026-11-05 (ADR-020); the marts keep the bucket, so showing it again
    /// is a read and not a rebuild.
    /// </remarks>
    public const string UnknownDeviceLabel = "(unknown device)";

    /// <summary>A label for a well-formed TAC that is absent from the GSMA database. Measured at 0.20%.</summary>
    public const string UnknownTacLabel = "(unknown TAC)";

    /// <summary>The reporting bodies of GSMA TS.06 v27, Annex A, current and former: a TAC's first two digits.</summary>
    private const string ReportingBodies =
        "'00', '01', '10', '30', '33', '35', '44', '45', '49', '50', '51', '52', '53', '54', '86', '91', '98', '99'";

    /// <summary>The condition that a row carries no handset (ADR-020), for a read of the rows themselves.</summary>
    /// <remarks>
    /// <c>000000</c>, or anything not 14 digits. Or 14 digits no handset could carry: a TAC the catalogue
    /// does not hold that is all zeros, is a shifted IMEI, or names a reporting body GSMA never had. A
    /// TAC merely missing from the catalogue is none of these, and is a handset.
    /// <para>
    /// The marts have this already: section 1b of <c>refresh_marts.sql</c> moves such a row to the empty
    /// TAC, and <c>MartRefreshScriptTests</c> holds the two to each other.
    /// </para>
    /// </remarks>
    /// <param name="imei">The IMEI column.</param>
    /// <param name="tac">The TAC column.</param>
    /// <param name="catalogue">The catalogue: <c>sqm.tac</c>, or <c>tac</c> for the connection's own database.</param>
    public static string NoHandset(string imei, string tac, string catalogue) =>
        $"(length({imei}) != 14 OR ({tac} NOT IN (SELECT tac FROM {catalogue}) AND ({tac} = '00000000'"
        + $" OR substring({tac}, 1, 2) NOT IN ({ReportingBodies})"
        + $" OR (endsWith({imei}, '0') AND substring({imei}, 1, 7) IN (SELECT substring(tac, 2, 7) FROM {catalogue})))))";

    /// <summary><see cref="NoHandset"/> for the current-state reads: <c>b</c>, against the catalogue.</summary>
    public static readonly string RowHasNoHandset = NoHandset("b.imei", "b.tac", "sqm.tac");

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
    /// Aggregate expression over <c>agg_tac_population</c> (alias <c>b</c>), used when a filter is
    /// applied: the raw fallback's measure, read from per-model rows.
    /// </summary>
    /// <remarks>
    /// Bindings add up across models. Subscribers and handsets are kept as uniq() states, and
    /// merging them is uniq() over the union - the raw fallback's estimator, so a person who holds
    /// two models is still one subscriber.
    /// </remarks>
    public static string TacMartMeasureFor(CountBy countBy) => countBy switch
    {
        CountBy.Bindings => "sum(b.bindings)",
        CountBy.Subscribers => "uniqMerge(b.subscribers)",
        CountBy.Handsets => "uniqMerge(b.handsets)",
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
