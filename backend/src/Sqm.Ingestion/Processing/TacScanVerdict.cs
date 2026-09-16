using Sqm.Application.DataImport;

namespace Sqm.Ingestion.Processing;

/// <summary>What a <see cref="TacFileScan"/> means for the import.</summary>
/// <param name="Faults">Quarantine groups to record, whether or not the file is refused.</param>
/// <param name="Rejection">The reason to refuse the file, or <c>null</c> if it is sound.</param>
internal sealed record TacScanVerdict(
    IReadOnlyList<QuarantineWrite> Faults,
    string? Rejection);

/// <summary>Turns the scan's counts into a decision and something an operator can act on.</summary>
/// <remarks>
/// Separate from the processor so the message itself can be tested. The text is the whole point
/// of this work - the file that prompted it failed three times with
/// <c>Code: 27 ... expected ',' before ... (at row 113551)</c>, which is accurate and leaves the
/// reader no better off. A rejection should say what is wrong, how much of the file it affects,
/// and what to do next.
/// </remarks>
internal static class TacScanRules
{
    public static TacScanVerdict Evaluate(TacFileScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);

        var faults = new List<QuarantineWrite>(2);
        var reasons = new List<string>(3);

        if (scan.MalformedRecords > 0)
        {
            var message =
                $"{scan.MalformedRecords:N0} record(s) do not have the {scan.HeaderFields} fields "
                + $"this file's own header declares, the first at line "
                + $"{scan.FirstMalformedLine:N0}. Every column in a GSMA export is text, so there "
                + "is no value here the importer could be disagreeing with: a record with a "
                + "different field count is a record with bytes missing.";

            reasons.Add(message);
            faults.Add(new QuarantineWrite(
                "TAC_RECORD_FIELD_COUNT", null, "error", scan.MalformedRecords,
                scan.FirstMalformedLine, message,
                [.. scan.MalformedSamples.Select(s =>
                    new QuarantineSample(s.LineNumber, s.RawRecord, s.Value))]));
        }

        if (scan.RepeatedTacs > 0)
        {
            var share = scan.Records == 0 ? 0 : (double)scan.RepeatedRecords / scan.Records;

            var message =
                $"{scan.RepeatedTacs:N0} TAC(s) appear more than once, accounting for "
                + $"{scan.RepeatedRecords:N0} of the file's {scan.Records:N0} records "
                + $"({share:P1}). A TAC must appear exactly once: it is the key manufacturer, "
                + "model and capability are read from, so a repeat both makes those values "
                + "ambiguous and multiplies every count that joins to it. Examples: "
                + string.Join(", ", scan.RepeatedSamples) + ".";

            reasons.Add(message);
            faults.Add(new QuarantineWrite(
                "TAC_DUPLICATE", "tac", "error", scan.RepeatedRecords, null, message, []));
        }

        if (reasons.Count == 0)
        {
            return new TacScanVerdict(faults, null);
        }

        if (scan.MalformedRecords > 0 && scan.RepeatedTacs > 0)
        {
            // Said explicitly because the two together point somewhere different from either
            // alone. A supplier who re-issues an export changes its content; a transfer that
            // fails repeats what it already sent and loses bytes where it resumes.
            reasons.Add(
                "Lost bytes and wholesale repetition in the same file is what a damaged transfer "
                + "looks like rather than a bad export. Downloading the file again is the first "
                + "thing to try.");
        }

        return new TacScanVerdict(
            faults,
            "This file was not loaded and no TAC version was created. "
            + string.Join(" ", reasons));
    }
}
