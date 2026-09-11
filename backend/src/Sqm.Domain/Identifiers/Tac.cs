using System.Diagnostics.CodeAnalysis;

namespace Sqm.Domain.Identifiers;

/// <summary>
/// A GSMA Type Allocation Code — the 8-digit prefix of an IMEI that identifies the device model.
/// </summary>
/// <remarks>
/// Measured against the real GSMA export (<c>DeviceDatabase_TAC1Sep2026.csv</c>):
/// 270,166 rows, every one exactly 8 characters, <b>zero duplicates</b>, zero nulls — a clean primary key.
/// <para>
/// Always text, never an integer. Leading zeros are significant: <c>00100100</c> is a real TAC, and
/// parsing it as a number would collide it with <c>100100</c>.
/// </para>
/// 95,571 distinct TACs appear in the subscriber data, and 92.8% of rows enrich successfully.
/// </remarks>
public readonly record struct Tac
{
    /// <summary>Length of every TAC in the GSMA database.</summary>
    public const int Length = 8;

    private Tac(string value) => Value = value;

    /// <summary>The 8-character code, leading zeros preserved.</summary>
    public string Value { get; }

    /// <summary>Extracts the TAC from an IMEI known to be well formed.</summary>
    /// <remarks>Callers must check <see cref="Imei.IsWellFormed"/> first; this does not re-validate.</remarks>
    internal static Tac FromImei(string wellFormedImei) => new(wellFormedImei[..Length]);

    public static bool TryParse(string? raw, [NotNullWhen(true)] out Tac? tac)
    {
        tac = null;
        if (raw is null) return false;

        var value = raw.Trim();
        if (value.Length != Length) return false;

        foreach (var c in value)
        {
            if (c is < '0' or > '9') return false;
        }

        tac = new Tac(value);
        return true;
    }

    public override string ToString() => Value;
}
