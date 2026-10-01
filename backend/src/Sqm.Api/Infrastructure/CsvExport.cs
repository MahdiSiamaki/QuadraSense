using System.Globalization;
using System.Text;

namespace Sqm.Api.Infrastructure;

/// <summary>Writes rows as CSV that a spreadsheet opens safely.</summary>
/// <remarks>
/// <para>
/// RFC 4180: a field holding a comma, a quote or a line break is quoted, and quotes inside are
/// doubled.
/// </para>
/// <para>
/// <b>Formula injection is neutralised</b>, as the security model promises for every export: a
/// cell beginning <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab or a carriage return is prefixed with
/// an apostrophe, so a spreadsheet shows it as text instead of running it. The values here come from
/// the operator's files and the GSMA database, not from this system's users - which is exactly why
/// nobody can vouch for what is in them.
/// </para>
/// <para>
/// UTF-8 with a byte-order mark, which is what Excel needs to read UTF-8 at all.
/// </para>
/// </remarks>
public static class CsvExport
{
    /// <summary>The CSV, as bytes.</summary>
    /// <param name="headers">Column headings.</param>
    /// <param name="rows">Values in column order: strings, numbers, booleans or null.</param>
    public static byte[] Write(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);

        var text = new StringBuilder();
        AppendLine(text, headers);

        foreach (var row in rows)
        {
            AppendLine(text, row.Select(Format));
        }

        var bom = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(text.ToString());
        return [.. bom, .. body];
    }

    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static void AppendLine(StringBuilder text, IEnumerable<string> fields)
    {
        var first = true;
        foreach (var field in fields)
        {
            if (!first)
            {
                text.Append(',');
            }

            text.Append(Field(field));
            first = false;
        }

        text.Append("\r\n");
    }

    /// <summary>One field: neutralised, then quoted if it needs to be.</summary>
    internal static string Field(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }
}
