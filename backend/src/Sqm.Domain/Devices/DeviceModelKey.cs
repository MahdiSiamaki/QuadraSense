using System.Text.RegularExpressions;

namespace Sqm.Domain.Devices;

/// <summary>
/// The identity of a device model, which is what a photograph belongs to.
/// </summary>
/// <remarks>
/// <para>
/// A TAC is not a model. The GSMA allocates them in blocks and a manufacturer burns through
/// many for one product: <b>Redmi Note 12S has 18, Redmi Note 13 has 38, Galaxy A12 has 184</b>.
/// Anything a person would call "the picture of this phone" therefore belongs to the
/// <i>marketing name</i>, and keying it by TAC meant 184 uploads of identical bytes to cover one
/// Samsung - or, in practice, one upload and 183 placeholders for the same handset.
/// </para>
/// <para>
/// <b>Brand, not manufacturer.</b> The GSMA record carries both and they are not the same word:
/// manufacturer is <c>Xiaomi Communications Co Ltd</c> where brand is <c>Redmi</c>, and it is the
/// brand that is printed on the handset and recognised by whoever is looking at the screen.
/// Manufacturer is the fallback for records whose brand is blank.
/// </para>
/// <para>
/// <b>The key is deliberately conservative.</b> It folds case and collapses whitespace, and stops
/// there. It does not strip <c>5G</c>, punctuation or model-code suffixes, because every one of
/// those turns out to distinguish real and different products - <c>Galaxy A05</c> from
/// <c>Galaxy A05s</c>, <c>Redmi Note 11</c> from <c>Redmi Note 11 Pro</c>, <c>Redmi Note 12</c>
/// from <c>Redmi Note 12S</c>. Folding any of them together would put a confident picture of the
/// wrong handset on a device page, which is worse than a placeholder because nobody re-checks a
/// picture that looks right.
/// </para>
/// </remarks>
public static partial class DeviceModelKey
{
    /// <summary>Runs of whitespace, folded to a single space.</summary>
    [GeneratedRegex(@"\s+", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex Whitespace { get; }

    /// <summary>
    /// The storage key for a model, or <c>null</c> when the record does not identify one.
    /// </summary>
    /// <remarks>
    /// Null is a real answer and not a failure. A TAC absent from the GSMA database, or present
    /// with no marketing name, has no model to attach a picture to - it is the <i>absence</i> of
    /// a device rather than a nameless one, and it keeps the drawn placeholder.
    /// </remarks>
    /// <param name="brand">The GSMA brand, e.g. <c>Redmi</c>.</param>
    /// <param name="manufacturer">Fallback when brand is blank, e.g. <c>Xiaomi Communications Co Ltd</c>.</param>
    /// <param name="marketingName">The product name, e.g. <c>Redmi Note 12S</c>.</param>
    public static string? For(string? brand, string? manufacturer, string? marketingName)
    {
        var name = Normalise(marketingName);
        if (name is null)
        {
            return null;
        }

        var maker = Normalise(brand) ?? Normalise(manufacturer);
        if (maker is null)
        {
            return null;
        }

        // The separator is '|' because it cannot occur in either half: the GSMA export is CSV and
        // a pipe in a brand name would have survived into the data, where none does.
        return $"{maker}|{name}";
    }

    /// <summary>The display brand that goes alongside the key, or null.</summary>
    public static string? DisplayBrand(string? brand, string? manufacturer) =>
        Trimmed(brand) ?? Trimmed(manufacturer);

    /// <summary>The display marketing name that goes alongside the key, or null.</summary>
    public static string? DisplayName(string? marketingName) => Trimmed(marketingName);

    private static string? Normalise(string? value)
    {
        var trimmed = Trimmed(value);
        return trimmed?.ToLowerInvariant();
    }

    private static string? Trimmed(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var collapsed = Whitespace.Replace(value.Trim(), " ");
        return collapsed.Length == 0 ? null : collapsed;
    }
}
