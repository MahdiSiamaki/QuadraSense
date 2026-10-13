using System.Text.RegularExpressions;

namespace Sqm.Application.Dashboards;

/// <summary>
/// Text checks that must agree with the frontend and with PostgreSQL, not only with .NET.
/// </summary>
/// <remarks>
/// <para>
/// <b>Patterns the way JavaScript and PostgreSQL read them.</b> The widget contract's patterns are
/// checked by the frontend in JavaScript and, for template keys, by a PostgreSQL CHECK. .NET differs from
/// both: <c>$</c> also matches before a final newline, and <c>\d</c> matches any Unicode digit. So
/// <c>"security-risk\n"</c> passed here and failed the database's CHECK as a 500, and an Arabic-Indic
/// TAC passed here while the frontend refused it. Patterns run with ECMAScript semantics (ASCII
/// classes) and a closing <c>$</c> anchored at the true end.
/// </para>
/// <para>
/// <b>Text PostgreSQL can store.</b> It refuses U+0000 in text and jsonb, and Npgsql refuses a lone
/// surrogate; either reached the database as a 500. Such text is refused up front, as a 400 that names
/// the field.
/// </para>
/// </remarks>
public static class ContractText
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    /// <summary>Whether <paramref name="value"/> matches a contract pattern, as JavaScript would.</summary>
    public static bool Matches(string value, string pattern)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(pattern);
        return Regex.IsMatch(value, EndAnchored(pattern), RegexOptions.ECMAScript, Timeout);
    }

    /// <summary>A pattern whose closing <c>$</c> matches only at the end of the input.</summary>
    public static string EndAnchored(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return pattern.EndsWith('$') && !pattern.EndsWith(@"\$", StringComparison.Ordinal) ? pattern[..^1] + @"\z" : pattern;
    }

    /// <summary>Whether PostgreSQL can store the text: no U+0000, no unpaired surrogate.</summary>
    public static bool Storable(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\0')
            {
                return false;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>At most <paramref name="length"/> UTF-16 units, never ending inside a surrogate pair.</summary>
    public static string Truncate(string text, int length)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= length)
        {
            return text;
        }

        var end = length > 0 && char.IsHighSurrogate(text[length - 1]) ? length - 1 : length;
        return text[..end].TrimEnd();
    }
}
