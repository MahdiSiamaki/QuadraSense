using Sqm.Domain.Identifiers;

namespace Sqm.Domain.Bindings;

/// <summary>
/// The natural key of this dataset: a phone number, a SIM, and a handset, together.
/// </summary>
/// <remarks>
/// Verified against the real initial dump: <b>125,939,523 rows, 125,939,523 distinct triples, zero
/// duplicates</b>. This is a genuine natural key, not an assumption.
/// <para>
/// The grain is a <i>binding</i>, not a subscriber. 51.3% of MSISDNs were observed on two or more
/// handsets, so "one row per subscriber" is not a shape this data has.
/// </para>
/// </remarks>
public readonly record struct BindingKey(Msisdn Msisdn, Imsi Imsi, Imei Imei)
{
    /// <summary>The device model code, or <see langword="null"/> when the IMEI cannot yield one.</summary>
    public Tac? Tac => Imei.Tac;

    public override string ToString() => $"{Msisdn}/{Imsi}/{Imei}";
}

/// <summary>The only two operations the source feed performs on a binding.</summary>
/// <remarks>
/// Verified across all 677,580,701 delta rows: <b>zero rows carry any other label</b>.
/// </remarks>
public enum ChangeLabel
{
    /// <summary>The binding becomes active.</summary>
    Add = 1,

    /// <summary>The binding becomes inactive.</summary>
    Remove = 2,
}

/// <summary>A single change event, as delivered in one daily file.</summary>
/// <param name="Key">The binding being changed.</param>
/// <param name="Label">Whether the binding is being activated or deactivated.</param>
/// <param name="Sequence">
/// Delivery sequence number. <b>Not a date</b> — the source files contain no timestamp of any kind.
/// Ordering by (batch timestamp, filename) was validated independently via the TAC allocation-date
/// frontier, which advances monotonically across all 82 observed files.
/// </param>
public readonly record struct BindingChange(BindingKey Key, ChangeLabel Label, int Sequence);
