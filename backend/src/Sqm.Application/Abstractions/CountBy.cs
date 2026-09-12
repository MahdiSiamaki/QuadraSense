namespace Sqm.Application.Abstractions;

/// <summary>
/// What a breakdown counts.
/// </summary>
/// <remarks>
/// <para>
/// These are three genuinely different questions, and for a large vendor they are far apart.
/// Measured for Samsung on the initial snapshot:
/// </para>
/// <list type="table">
///   <item><term>Bindings</term><description>52,704,438</description></item>
///   <item><term>Subscribers</term><description>40,070,800</description></item>
///   <item><term>Handsets</term><description>39,320,516</description></item>
/// </list>
/// <para>
/// The 13.4M gap between bindings and handsets is real behaviour, not error: a dual-SIM phone
/// serving two numbers is two bindings and one handset, and a number whose SIM was swapped is two
/// bindings on the same handset.
/// </para>
/// </remarks>
public enum CountBy
{
    /// <summary>
    /// Number + SIM + handset combinations. The grain of the data, and the only measure that is
    /// additive across dimension values.
    /// </summary>
    Bindings,

    /// <summary>
    /// Distinct MSISDNs. <b>Not additive across dimension values</b> — one subscriber owning a
    /// Samsung and an Apple handset is counted under both, so the column sums to more than the
    /// population (measured: 103.9M against 79.5M actual subscribers).
    /// </summary>
    Subscribers,

    /// <summary>
    /// Distinct 14-digit IMEIs. Additive across vendors and models, because a TAC is the first 8
    /// digits of the IMEI, so no handset can belong to two of them. Excludes the
    /// <c>000000</c> sentinel, which is not a handset.
    /// </summary>
    Handsets,
}
