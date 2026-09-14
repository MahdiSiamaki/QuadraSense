using System.Security.Cryptography;
using System.Text;

namespace Sqm.Application.DataImport;

/// <summary>What the platform decided about a file's column header.</summary>
public enum SchemaVerdict
{
    /// <summary>The header matches a version already on record. Import normally.</summary>
    Known,

    /// <summary>
    /// The header is new but compatible: it adds columns at the end and keeps every existing one
    /// in place. Registered as a new version and imported, with a warning on the timeline.
    /// </summary>
    AcceptedWithWarning,

    /// <summary>
    /// The header is new and incompatible: a column was removed, renamed or reordered. Nothing
    /// is imported.
    /// </summary>
    Rejected,
}

/// <summary>The outcome of matching a file's header against the known column contracts.</summary>
/// <param name="Verdict">What happens next.</param>
/// <param name="SchemaVersionId">The version this file matched or was registered as.</param>
/// <param name="VersionLabel">Its label, e.g. <c>v2</c>.</param>
/// <param name="Explanation">What changed, in plain words, for the timeline.</param>
public sealed record SchemaResolution(
    SchemaVerdict Verdict, int? SchemaVersionId, string? VersionLabel, string Explanation);

/// <summary>
/// Decides whether a header the platform has not seen before can be imported.
/// </summary>
/// <remarks>
/// <para>
/// A source's columns will change eventually - GSMA has added columns to the TAC export before,
/// and it will again. The question is what should happen when they do, and there are only three
/// defensible answers: this is the same contract, this is a compatible extension, or this is a
/// different file.
/// </para>
/// <para>
/// The line is drawn at whether existing columns still mean what they meant. Appending columns is
/// safe because nothing already being read moves. Removing, renaming or reordering is not:
/// ClickHouse's <c>CSVWithNames</c> matches by header name, so a reorder loads without a single
/// type error and puts every value in the wrong column. That has already happened once in this
/// project - eleven TAC columns silently received nothing while the row count stayed exactly
/// right, and it surfaced only when a capability query returned zero.
/// </para>
/// </remarks>
public static class SchemaFingerprint
{
    /// <summary>
    /// Hashes a header so two files with the same contract match in one index lookup.
    /// </summary>
    /// <remarks>
    /// Case and surrounding whitespace are normalised away because they are formatting, not
    /// contract. Order is not: it is part of what the header means.
    /// </remarks>
    public static string Compute(IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        var canonical = string.Join('', columns.Select(c => c.Trim().ToLowerInvariant()));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    /// Compares a new header against the current contract.
    /// </summary>
    /// <returns>
    /// <see cref="SchemaVerdict.AcceptedWithWarning"/> when the new header keeps every known
    /// column in its existing position and only appends, otherwise
    /// <see cref="SchemaVerdict.Rejected"/> with an explanation naming what moved.
    /// </returns>
    public static (SchemaVerdict Verdict, string Explanation) Compare(
        IReadOnlyList<string> known, IReadOnlyList<string> incoming)
    {
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(incoming);

        var missing = known
            .Where(k => !incoming.Any(i => Same(i, k)))
            .ToList();

        if (missing.Count > 0)
        {
            return (SchemaVerdict.Rejected,
                $"The file is missing {missing.Count} column(s) the current contract has: "
                + $"{string.Join(", ", missing)}. Importing it would leave those columns empty "
                + "for this day while every other day has them.");
        }

        // Position matters as much as presence. CSVWithNames matches by name, so a file that has
        // every column but in a different order loads cleanly into the wrong columns.
        for (var i = 0; i < known.Count; i++)
        {
            if (!Same(known[i], incoming[i]))
            {
                return (SchemaVerdict.Rejected,
                    $"Column {i + 1} is '{incoming[i]}', but the current contract has "
                    + $"'{known[i]}' there. The columns were reordered, which loads without a "
                    + "single error and puts every value in the wrong column.");
            }
        }

        var added = incoming.Skip(known.Count).ToList();

        return (SchemaVerdict.AcceptedWithWarning,
            $"The file adds {added.Count} new column(s) at the end: {string.Join(", ", added)}. "
            + "Every existing column is unchanged and in place, so this is imported and recorded "
            + "as a new schema version. The new columns are not yet read by anything.");
    }

    private static bool Same(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
