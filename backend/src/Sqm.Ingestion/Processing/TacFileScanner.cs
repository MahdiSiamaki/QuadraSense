using System.Text;

namespace Sqm.Ingestion.Processing;

/// <summary>What one structural pass over a GSMA TAC export found.</summary>
/// <param name="Records">Logical CSV records, excluding the header.</param>
/// <param name="HeaderFields">Field count of the header, the contract every record is held to.</param>
/// <param name="MalformedRecords">Records whose field count differs from the header's.</param>
/// <param name="DistinctTacs">Distinct well-formed TACs.</param>
/// <param name="RepeatedRecords">Records carrying a TAC already seen earlier in the file.</param>
/// <param name="RepeatedTacs">Distinct TACs that appear more than once.</param>
/// <param name="NonNumericTacs">Records whose TAC is not exactly eight digits.</param>
/// <param name="FirstMalformedLine">Line number of the first malformed record, for the operator.</param>
/// <param name="MalformedSamples">A capped set of malformed records, as they appear in the file.</param>
/// <param name="RepeatedSamples">A capped set of TACs that repeat.</param>
internal sealed record TacFileScan(
    long Records,
    int HeaderFields,
    long MalformedRecords,
    long DistinctTacs,
    long RepeatedRecords,
    long RepeatedTacs,
    long NonNumericTacs,
    long? FirstMalformedLine,
    IReadOnlyList<QuarantineSampleDraft> MalformedSamples,
    IReadOnlyList<string> RepeatedSamples);

/// <summary>A malformed record kept as an example, before it becomes a quarantine row.</summary>
internal sealed record QuarantineSampleDraft(long LineNumber, string RawRecord, string? Value);

/// <summary>
/// Reads a TAC export once, without writing anything, and reports what is structurally wrong
/// with it.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of a real file. <c>DeviceDatabase_TAC16Sep2026.csv</c> was refused with
/// <c>Code: 27. Cannot parse input: expected ',' before ... (at row 113551)</c>, which is
/// accurate and tells an operator nothing they can act on. The file turned out to have two
/// independent defects: 27 records with bytes missing, and 211,135 TACs present exactly twice -
/// 1,224 blocks of roughly 165 records replayed from earlier in the file. Diagnosing that took a
/// separate pass with a real CSV parser. This is that pass, made part of the product.
/// </para>
/// <para>
/// It runs <em>before</em> the load rather than after it, which is the pattern
/// <see cref="SqmDailyProcessor"/> already uses for the daily file: validate and count without
/// writing, then stream the bytes. The TAC processor did the opposite - load 366 MB into a
/// version, then check it - so a file destined for rejection paid the full load first, and a file
/// that broke ClickHouse's parser never reached the checks at all.
/// </para>
/// <para>
/// Parsing is RFC 4180 rather than line splitting, and that is not optional here:
/// <c>bandDetails</c> runs to 5,335 characters of comma-separated radio bands inside quotes, so
/// a line splitter would report almost every record as malformed. The scanner also handles
/// newlines inside quoted fields, which this export does not currently use - measured at zero
/// across 482,047 records - but which RFC 4180 permits and a future revision may introduce.
/// </para>
/// </remarks>
internal static class TacFileScanner
{
    /// <summary>How many example records to keep per problem.</summary>
    /// <remarks>Matches <see cref="SqmDailyProcessor"/>: enough to see the shape, not a second copy
    /// of the file.</remarks>
    private const int MaxSamples = 10;

    private const int MaxSampleLength = 500;

    /// <summary>The largest TAC, which is eight digits, plus one.</summary>
    private const int TacDomain = 100_000_000;

    /// <summary>
    /// Scans the stream from its current position, which must be the start of the file.
    /// </summary>
    /// <remarks>
    /// The header is read as the first record and its field count becomes the contract every
    /// other record is measured against - deliberately the file's own header rather than the 26
    /// columns on record, because GSMA appends columns and the import is built to accept that.
    /// What this rule catches is a record inconsistent with its <em>own</em> file, which is what
    /// lost bytes look like.
    /// </remarks>
    public static async Task<TacFileScan> ScanAsync(
        Stream csv,
        Func<long, CancellationToken, Task>? onProgress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(csv);

        using var reader = new StreamReader(
            csv, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 20,
            leaveOpen: true);

        // Two bitmaps over the eight-digit TAC domain rather than a hash set: 12.5 MB each,
        // fixed, whatever the file turns out to contain. A hash set would be simpler to read but
        // its size is chosen by the input, and the input is the thing under suspicion.
        var seen = new ulong[(TacDomain / 64) + 1];
        var repeated = new ulong[(TacDomain / 64) + 1];

        var buffer = new char[1 << 16];
        var raw = new char[MaxSampleLength];
        var field = new StringBuilder(16);

        var malformedSamples = new List<QuarantineSampleDraft>(MaxSamples);
        var repeatedSamples = new List<string>(MaxSamples);

        long records = 0, malformed = 0, distinct = 0, repeatedRecords = 0;
        long repeatedTacs = 0, nonNumeric = 0, charsRead = 0, line = 1;
        long? firstMalformedLine = null;

        int headerWidth = -1;
        int fieldIndex = 0, rawLength = 0;
        bool inQuotes = false, justClosedQuote = false, recordStarted = false;

        // Whether the field being read has any characters yet, which is what decides how a quote
        // is interpreted. See the '"' case below - this file is the reason the flag exists.
        bool fieldHasContent = false;

        void EndRecord(long endLine)
        {
            // A trailing newline at end of file, or a blank line, is not a record.
            if (!recordStarted && fieldIndex == 0 && field.Length == 0)
            {
                return;
            }

            var width = fieldIndex + 1;

            if (headerWidth < 0)
            {
                headerWidth = width;
            }
            else
            {
                records++;

                if (width != headerWidth)
                {
                    malformed++;
                    firstMalformedLine ??= endLine;

                    if (malformedSamples.Count < MaxSamples)
                    {
                        malformedSamples.Add(new QuarantineSampleDraft(
                            endLine,
                            new string(raw, 0, rawLength),
                            $"{width} fields, expected {headerWidth}"));
                    }
                }
                else
                {
                    Classify(field.ToString());
                }
            }

            fieldIndex = 0;
            rawLength = 0;
            recordStarted = false;
            fieldHasContent = false;
            field.Clear();
        }

        void Classify(string tac)
        {
            if (tac.Length != 8 || !IsEightDigits(tac))
            {
                // Counted, not rejected. The existing TAC_NOT_8_DIGITS rule already treats this
                // as a warning: such a row matches no IMEI, but it does not make the file wrong.
                nonNumeric++;
                return;
            }

            var value = int.Parse(tac, System.Globalization.CultureInfo.InvariantCulture);
            var word = value >> 6;
            var bit = 1UL << (value & 63);

            if ((seen[word] & bit) == 0)
            {
                seen[word] |= bit;
                distinct++;
                return;
            }

            repeatedRecords++;

            if ((repeated[word] & bit) == 0)
            {
                repeated[word] |= bit;
                repeatedTacs++;

                if (repeatedSamples.Count < MaxSamples)
                {
                    repeatedSamples.Add(tac);
                }
            }
        }

        int read;
        while ((read = await reader.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            charsRead += read;

            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];

                if (rawLength < MaxSampleLength && c != '\n' && c != '\r')
                {
                    raw[rawLength++] = c;
                }

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        inQuotes = false;
                        justClosedQuote = true;
                    }
                    else
                    {
                        if (c == '\n')
                        {
                            line++;
                        }

                        Append(field, fieldIndex, c);
                    }

                    continue;
                }

                if (justClosedQuote)
                {
                    justClosedQuote = false;

                    // "" inside a quoted field is one literal quote, and the field continues.
                    if (c == '"')
                    {
                        Append(field, fieldIndex, '"');
                        inQuotes = true;
                        continue;
                    }
                }

                switch (c)
                {
                    case '"':
                        // A quote only opens a quoted field at the START of a field. Anywhere
                        // else it is an ordinary character, and treating it as an opening quote
                        // is catastrophic rather than merely wrong: the September file has 19
                        // records ending in a single stray quote, and an earlier version of this
                        // scanner read the first one as opening a field that then ran on for
                        // 296,302 commas. It reported 183,187 records where there are 482,047.
                        if (fieldHasContent)
                        {
                            Append(field, fieldIndex, c);
                        }
                        else
                        {
                            inQuotes = true;
                        }

                        recordStarted = true;
                        fieldHasContent = true;
                        break;
                    case ',':
                        fieldIndex++;
                        recordStarted = true;
                        fieldHasContent = false;
                        break;
                    case '\r':
                        break;
                    case '\n':
                        line++;
                        EndRecord(line - 1);
                        break;
                    default:
                        recordStarted = true;
                        fieldHasContent = true;
                        Append(field, fieldIndex, c);
                        break;
                }
            }

            if (onProgress is not null)
            {
                await onProgress(charsRead, ct).ConfigureAwait(false);
            }
        }

        // A file whose last record has no trailing newline still has that record.
        EndRecord(line);

        return new TacFileScan(
            records, headerWidth < 0 ? 0 : headerWidth, malformed, distinct, repeatedRecords,
            repeatedTacs, nonNumeric, firstMalformedLine, malformedSamples, repeatedSamples);
    }

    /// <summary>Only the first field is kept; the other 25 are not what this pass is about.</summary>
    private static void Append(StringBuilder field, int fieldIndex, char c)
    {
        if (fieldIndex == 0 && field.Length < 16)
        {
            field.Append(c);
        }
    }

    private static bool IsEightDigits(string value)
    {
        foreach (var c in value)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}
