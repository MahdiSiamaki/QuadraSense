using System.Globalization;
using System.IO.Compression;
using System.Text;
using Sqm.Application.Dashboards;

namespace Sqm.Api.Infrastructure;

/// <summary>A dashboard's CSV export as a file: one widget as a CSV, several as a ZIP.</summary>
/// <param name="FileName">The name to save it under; carries the scope.</param>
/// <param name="ContentType">text/csv or application/zip.</param>
/// <param name="Content">The bytes.</param>
/// <param name="Scope">visible, topN or full; several, comma-separated, for a ZIP of mixed widgets.</param>
public sealed record DashboardCsvFile(string FileName, string ContentType, byte[] Content, string Scope);

/// <summary>Writes a prepared CSV export through <see cref="CsvExport"/>, which neutralises formulas.</summary>
/// <remarks>
/// <para>
/// <b>The scope is in the name.</b> <c>…-top10.csv</c> is never mistaken for everything: a file says
/// whether it is what the card showed (<c>visible</c>), the card's top N of more (<c>topN</c>) or all the
/// source returned (<c>full</c>).
/// </para>
/// <para>
/// A ZIP carries <c>about.txt</c>: the dashboard, when it was prepared, the lens, each file's scope and
/// caveats, and the widgets left out and why - a CSV has no place to say any of that.
/// </para>
/// </remarks>
public static class DashboardCsvWriter
{
    /// <summary>The file for a prepared export.</summary>
    public static DashboardCsvFile Write(DashboardCsvExport export)
    {
        ArgumentNullException.ThrowIfNull(export);

        var included = export.Parts.Where(p => p.Table is not null).ToList();
        var stamp = export.PreparedAt.UtcDateTime.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        var dashboard = Slug(export.Name);

        if (export.Parts.Count == 1 && included.Count == 1)
        {
            var part = included[0];
            return new DashboardCsvFile(
                $"{dashboard}-{Slug(part.Title)}-{part.Table!.Scope}-{stamp}.csv", "text/csv; charset=utf-8",
                CsvExport.Write(part.Table.Headers, part.Table.Rows), part.Table.Scope);
        }

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in included)
            {
                var name = Unique($"{Slug(part.Title)}-{part.Table!.Scope}.csv", used);
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(CsvExport.Write(part.Table.Headers, part.Table.Rows));
            }

            var about = zip.CreateEntry("about.txt", CompressionLevel.Optimal);
            using var aboutStream = about.Open();
            aboutStream.Write(Encoding.UTF8.GetBytes(About(export)));
        }

        var scopes = string.Join(",", included.Select(p => p.Table!.Scope).Distinct().Order(StringComparer.Ordinal));
        return new DashboardCsvFile($"{dashboard}-{stamp}.zip", "application/zip", buffer.ToArray(), scopes.Length == 0 ? "none" : scopes);
    }

    private static string About(DashboardCsvExport export)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"{export.Name}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Prepared {export.PreparedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC, for the person who exported it, with their permissions.");
        text.AppendLine(CultureInfo.InvariantCulture, $"Lens: {Lens(export)}");
        text.AppendLine();
        text.AppendLine("Scope: visible = what the card showed; topN = the card's top N of more; full = everything the source returned.");
        text.AppendLine();

        foreach (var part in export.Parts)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{part.Title} ({part.Type})");
            if (part.Table is { } table)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  Scope: {table.Scope}. {table.ScopeNote}");
            }
            else
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  Not included ({part.Status}): {part.Reason}");
            }

            foreach (var note in part.Notes)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  Note: {note}");
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    private static string Lens(DashboardCsvExport export)
    {
        var l = export.Lens;
        var parts = new List<string>();
        if (l.CountBy is { } c)
        {
            parts.Add($"count by {c}");
        }

        if (l.Range is { } r)
        {
            parts.Add($"range {r}");
        }

        if (l.Vendor is { } v)
        {
            parts.Add($"vendor {v}");
        }

        if (l.DeviceType is { } d)
        {
            parts.Add($"device type {d}");
        }

        if (l.OperatingSystem is { } o)
        {
            parts.Add($"OS {o}");
        }

        if (l.Tac is { } t)
        {
            parts.Add($"TAC {t}");
        }

        if (l.IncludeUnknownDevice is false)
        {
            parts.Add("unknown devices excluded");
        }

        return parts.Count == 0 ? "the dashboard's defaults" : string.Join(", ", parts);
    }

    /// <summary>ASCII letters, digits and dashes: safe in a file name on every system.</summary>
    internal static string Slug(string value)
    {
        var text = new StringBuilder();
        foreach (var ch in value.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                text.Append(ch);
            }
            else if (text.Length > 0 && text[^1] != '-')
            {
                text.Append('-');
            }
        }

        var slug = text.ToString().Trim('-');
        return slug.Length == 0 ? "dashboard" : slug[..Math.Min(60, slug.Length)].Trim('-');
    }

    private static string Unique(string name, HashSet<string> used)
    {
        var candidate = name;
        for (var i = 2; !used.Add(candidate); i++)
        {
            candidate = name.Replace(".csv", $"-{i}.csv", StringComparison.Ordinal);
        }

        return candidate;
    }
}
