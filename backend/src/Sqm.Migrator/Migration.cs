using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Sqm.Migrator;

/// <summary>One migration file on disk.</summary>
/// <param name="Version">The leading numeric part of the file name, e.g. <c>001</c>.</param>
/// <param name="Name">The file name without its extension.</param>
/// <param name="Path">Absolute path, for error messages.</param>
/// <param name="Sql">File contents.</param>
/// <param name="Checksum">SHA-256 of the contents, hex-encoded.</param>
internal sealed record Migration(string Version, string Name, string Path, string Sql, string Checksum)
{
    /// <summary>
    /// Reads every <c>*.sql</c> file in <paramref name="directory"/>, ordered by version.
    /// </summary>
    /// <remarks>
    /// Ordering is numeric on the leading digits, not lexical on the whole name: lexical order
    /// puts <c>010</c> before <c>009</c> the moment the zero-padding runs out, and a migration
    /// applied out of order is a corrupt schema rather than a failed run.
    /// </remarks>
    public static IReadOnlyList<Migration> Discover(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"migration directory not found: {directory}");
        }

        var migrations = new List<Migration>();

        foreach (var path in Directory.EnumerateFiles(directory, "*.sql", SearchOption.TopDirectoryOnly))
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            var version = LeadingDigits(name);

            if (version.Length == 0)
            {
                throw new InvalidOperationException(
                    $"migration file name must start with a version number: {path}");
            }

            var sql = File.ReadAllText(path);
            migrations.Add(new Migration(version, name, path, sql, Sha256Hex(sql)));
        }

        var duplicate = migrations
            .GroupBy(m => m.Version, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"two migrations share version {duplicate.Key}: "
                + string.Join(", ", duplicate.Select(m => m.Name)));
        }

        return [.. migrations.OrderBy(m => long.Parse(m.Version, CultureInfo.InvariantCulture))];
    }

    private static string LeadingDigits(string name)
    {
        var end = 0;
        while (end < name.Length && char.IsAsciiDigit(name[end]))
        {
            end++;
        }

        return name[..end];
    }

    private static string Sha256Hex(string content)
    {
        // Normalise line endings before hashing. Git checkouts differ between machines and a
        // checksum that changes with the checkout would flag every migration as tampered with.
        var normalised = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalised)));
    }
}
