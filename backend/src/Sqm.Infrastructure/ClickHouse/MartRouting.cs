using Sqm.Contracts.Dashboard;

namespace Sqm.Infrastructure.ClickHouse;

/// <summary>
/// Decides whether a request can be answered from the pre-aggregated marts.
/// </summary>
/// <remarks>
/// <para>
/// The marts are rolled up to <c>(seq, tac, active)</c>. Every dimension the dashboard groups by —
/// manufacturer, vendor, model, device type, OS — is an attribute of the TAC, so those all resolve from
/// the rollup. Measured: <b>39 ms against the mart versus 3,481 ms against raw</b>, reading 365K rows
/// instead of 126M.
/// </para>
/// <para>
/// A filter on <b>MSISDN prefix</b> is different in kind: it is a property of the <i>subscriber</i>, not
/// the device, so it cannot be derived from a TAC-level rollup. Those requests fall back to the raw
/// table and are slow — correctly slow, rather than quietly wrong.
/// </para>
/// <para>
/// This type exists so that decision is made in one place and is visible at review time. The failure
/// mode it prevents is subtle: answering a subscriber-filtered question from a device-level rollup
/// returns a plausible number that is simply not the number that was asked for.
/// </para>
/// </remarks>
internal static class MartRouting
{
    /// <summary>Sequence of the initial full snapshot.</summary>
    public const int SnapshotSequence = 0;

    /// <summary>True when the filter only touches TAC-level attributes.</summary>
    public static bool CanUseMart(DashboardFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // MSISDN prefix is subscriber-level and has no representation in the rollup.
        if (!string.IsNullOrWhiteSpace(filter.MsisdnPrefix)) return false;

        return true;
    }
}
