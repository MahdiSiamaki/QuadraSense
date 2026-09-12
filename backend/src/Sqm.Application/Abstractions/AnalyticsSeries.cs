namespace Sqm.Application.Abstractions;

/// <summary>
/// Pre-computed series that widgets can plot.
/// </summary>
/// <remarks>
/// <para>
/// An enum rather than a free-text name, for the same reason as
/// <see cref="AnalyticsDimension"/>: it is the allow-list. A widget stored in a saved layout
/// carries a series name, and saved layouts are user-editable data. Resolving that name through a
/// compile-time enum means a tampered layout cannot reach an arbitrary table.
/// </para>
/// <para>
/// Every series here reads a mart of at most a few hundred rows. The underlying computations are
/// the most expensive in the system — the devices-per-subscriber grouping measured at 52 s against
/// raw data — so they are never run on request.
/// </para>
/// </remarks>
public enum AnalyticsSeries
{
    /// <summary>
    /// How many distinct devices each subscriber uses. Measured: 55.5M use one, 14.6M use two.
    /// </summary>
    DevicesPerSubscriber,

    /// <summary>
    /// How many subscribers share each device — the dual-SIM and resale signal.
    /// Measured: 73.2M devices serve one number, 15.3M serve two or more.
    /// Excludes the <c>000000</c> sentinel, which is not a device.
    /// </summary>
    SubscribersPerDevice,

    /// <summary>Active bindings by 4-digit subscriber-number prefix. 342 distinct prefixes observed.</summary>
    MsisdnPrefix,

    /// <summary>The handset-versus-machine mix.</summary>
    DeviceClass,
}
