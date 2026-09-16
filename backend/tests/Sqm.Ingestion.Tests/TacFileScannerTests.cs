using System.Text;
using Sqm.Ingestion.Processing;

namespace Sqm.Ingestion.Tests;

/// <summary>
/// Tests for the structural scan that decides whether a GSMA TAC export is loadable.
/// </summary>
/// <remarks>
/// <para>
/// These exist because of <c>DeviceDatabase_TAC16Sep2026.csv</c>, a 349 MB export that was
/// refused with <c>Code: 27. Cannot parse input: expected ',' before ...</c>. That message named
/// one line out of 482,048 and gave no way to tell a typo from a ruined download. The file
/// actually had two defects: 27 records with bytes missing, and 211,135 TACs present exactly
/// twice, in 1,224 replayed blocks of roughly 165 records.
/// </para>
/// <para>
/// The case this file class exists to prevent is the quiet one. A TAC export whose records are
/// duplicated but whose CSV is well-formed loads without a single parser complaint, and every
/// count that joins to the TAC table doubles. This project has shipped that shape of bug once
/// already - 251,879,046 against a real 125,939,523 - and it was found by disbelieving a
/// dashboard, not by an error.
/// </para>
/// </remarks>
public class TacFileScannerTests
{
    private const string Header = "tac,manufacturer,bandDetails";

    private static async Task<TacFileScan> ScanAsync(string csv)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        return await TacFileScanner.ScanAsync(stream, null, CancellationToken.None);
    }

    [Fact]
    public async Task A_clean_file_reports_no_faults()
    {
        var scan = await ScanAsync(
            $"{Header}\n35004012,Samsung,GSM 900\n35308690,Apple,GSM 1800\n");

        Assert.Equal(2, scan.Records);
        Assert.Equal(3, scan.HeaderFields);
        Assert.Equal(0, scan.MalformedRecords);
        Assert.Equal(2, scan.DistinctTacs);
        Assert.Equal(0, scan.RepeatedTacs);
        Assert.Equal(0, scan.RepeatedRecords);
    }

    [Fact]
    public async Task Commas_inside_quotes_are_not_field_separators()
    {
        // The reason this scanner parses RFC 4180 instead of splitting on commas. bandDetails
        // reaches 5,335 characters of comma-separated radio bands in the real export, so a line
        // splitter would call almost every record malformed and the check would be useless.
        var scan = await ScanAsync(
            $"{Header}\n35004012,Samsung,\"GSM 900,GSM 1800,WCDMA FDD Band 1\"\n");

        Assert.Equal(1, scan.Records);
        Assert.Equal(0, scan.MalformedRecords);
        Assert.Equal(1, scan.DistinctTacs);
    }

    [Fact]
    public async Task A_newline_inside_a_quoted_field_does_not_end_the_record()
    {
        // Measured at zero occurrences across the 482,047 records of the September export, but
        // RFC 4180 permits it and a future revision may use it. A scanner that got this wrong
        // would invent malformed records in a file that is fine.
        var scan = await ScanAsync(
            $"{Header}\n35004012,Samsung,\"GSM 900\nGSM 1800\"\n35308690,Apple,GSM 850\n");

        Assert.Equal(2, scan.Records);
        Assert.Equal(0, scan.MalformedRecords);
        Assert.Equal(2, scan.DistinctTacs);
    }

    [Fact]
    public async Task A_doubled_quote_is_one_literal_quote_and_the_field_continues()
    {
        var scan = await ScanAsync(
            $"{Header}\n35004012,Samsung,\"5.5\"\" display, black\"\n");

        Assert.Equal(1, scan.Records);
        Assert.Equal(0, scan.MalformedRecords);
    }

    [Fact]
    public async Task A_repeated_tac_is_counted_once_as_a_tac_and_once_as_a_record()
    {
        // The distinction the operator's message depends on: how many models are affected, and
        // how much of the file is redundant. In the September file these were 211,135 and
        // 211,135, because every repeat appeared exactly twice.
        var scan = await ScanAsync(
            $"{Header}\n35004012,Samsung,A\n35308690,Apple,B\n35004012,Samsung,A\n"
            + "35004012,Samsung,A\n");

        Assert.Equal(4, scan.Records);
        Assert.Equal(2, scan.DistinctTacs);
        Assert.Equal(1, scan.RepeatedTacs);
        Assert.Equal(2, scan.RepeatedRecords);
        Assert.Equal("35004012", Assert.Single(scan.RepeatedSamples));
    }

    [Fact]
    public async Task A_record_with_the_wrong_field_count_is_malformed_and_located()
    {
        var scan = await ScanAsync(
            $"{Header}\n35004012,Samsung,A\n 900,GSM 1800\n35308690,Apple,B\n");

        Assert.Equal(3, scan.Records);
        Assert.Equal(1, scan.MalformedRecords);
        Assert.Equal(3, scan.FirstMalformedLine);

        var sample = Assert.Single(scan.MalformedSamples);
        Assert.Equal(3, sample.LineNumber);
        Assert.Equal(" 900,GSM 1800", sample.RawRecord);
        Assert.Equal("2 fields, expected 3", sample.Value);
    }

    [Fact]
    public async Task A_stray_quote_in_the_middle_of_a_field_does_not_start_a_quoted_field()
    {
        // The bug this scanner shipped with, found by the file it was written for. 19 records in
        // the September export end in a single unbalanced quote. Reading that quote as the start
        // of a quoted field made the parser swallow everything after it: one "record" of 296,302
        // fields, and 183,187 records counted where there are 482,047. RFC 4180 opens a quoted
        // field only at the start of a field, and a diagnostic that mis-segments the file it is
        // diagnosing is worse than no diagnostic.
        var scan = await ScanAsync(
            $"{Header}\n35004012,Samsung,A\n 900,GSM 1800 | Radio Interface: NONE\"\n"
            + "35308690,Apple,B\n35495844,Macan24,C\n");

        Assert.Equal(4, scan.Records);
        Assert.Equal(1, scan.MalformedRecords);
        Assert.Equal(3, scan.FirstMalformedLine);
        Assert.Equal(3, scan.DistinctTacs);
        Assert.Equal("2 fields, expected 3", Assert.Single(scan.MalformedSamples).Value);
    }

    [Fact]
    public async Task The_header_not_the_column_contract_sets_the_expected_width()
    {
        // GSMA appends columns, and the importer is built to accept that: a wider header is a new
        // schema version, not a fault. What this rule catches is a record inconsistent with its
        // own file, which is what lost bytes look like.
        var scan = await ScanAsync(
            "tac,manufacturer,bandDetails,ntnConnectivity\n35004012,Samsung,A,No\n");

        Assert.Equal(4, scan.HeaderFields);
        Assert.Equal(1, scan.Records);
        Assert.Equal(0, scan.MalformedRecords);
    }

    [Fact]
    public async Task Crlf_and_lf_are_both_accepted()
    {
        var scan = await ScanAsync(
            $"{Header}\r\n35004012,Samsung,A\r\n35308690,Apple,B\r\n");

        Assert.Equal(2, scan.Records);
        Assert.Equal(0, scan.MalformedRecords);
        Assert.Equal(2, scan.DistinctTacs);
    }

    [Fact]
    public async Task A_final_record_without_a_trailing_newline_is_still_a_record()
    {
        var scan = await ScanAsync($"{Header}\n35004012,Samsung,A\n35308690,Apple,B");

        Assert.Equal(2, scan.Records);
        Assert.Equal(2, scan.DistinctTacs);
    }

    [Fact]
    public async Task Blank_lines_are_not_records()
    {
        var scan = await ScanAsync($"{Header}\n35004012,Samsung,A\n\n35308690,Apple,B\n\n");

        Assert.Equal(2, scan.Records);
        Assert.Equal(0, scan.MalformedRecords);
    }

    [Fact]
    public async Task A_tac_that_is_not_eight_digits_is_counted_but_not_treated_as_a_repeat()
    {
        // TAC_NOT_8_DIGITS is an existing warning, not a rejection: such a row matches no IMEI,
        // but it does not make the file wrong. Two of them must not read as a duplicate pair.
        var scan = await ScanAsync(
            $"{Header}\nNot Known,Samsung,A\nNot Known,Apple,B\n35004012,Samsung,C\n");

        Assert.Equal(3, scan.Records);
        Assert.Equal(2, scan.NonNumericTacs);
        Assert.Equal(1, scan.DistinctTacs);
        Assert.Equal(0, scan.RepeatedTacs);
        Assert.Equal(0, scan.MalformedRecords);
    }

    [Fact]
    public async Task A_quoted_first_field_still_yields_the_tac()
    {
        var scan = await ScanAsync(
            $"{Header}\n\"35004012\",Samsung,A\n35004012,Samsung,A\n");

        Assert.Equal(2, scan.Records);
        Assert.Equal(1, scan.DistinctTacs);
        Assert.Equal(1, scan.RepeatedTacs);
    }

    [Fact]
    public async Task A_file_with_only_a_header_reports_no_records()
    {
        var scan = await ScanAsync($"{Header}\n");

        Assert.Equal(0, scan.Records);
        Assert.Equal(3, scan.HeaderFields);
    }
}

/// <summary>
/// Tests for the message an operator actually reads when a TAC export is refused.
/// </summary>
/// <remarks>
/// The text is the deliverable here, not a side effect of it. What this replaces is
/// <c>Code: 27. DB::Exception: Cannot parse input: expected ',' before ... (at row 113551)</c>,
/// which is true, names one line out of 482,048, and leaves the reader unable to tell a typo
/// from a ruined download.
/// </remarks>
public class TacScanRulesTests
{
    private static TacFileScan Scan(
        long records = 1000,
        long malformed = 0,
        long repeatedTacs = 0,
        long repeatedRecords = 0,
        long? firstMalformedLine = null) =>
        new(records, 26, malformed, records - repeatedRecords, repeatedRecords, repeatedTacs,
            0, firstMalformedLine, [], ["35308690", "35308700"]);

    [Fact]
    public void A_sound_file_is_not_refused_and_raises_nothing()
    {
        var verdict = TacScanRules.Evaluate(Scan());

        Assert.Null(verdict.Rejection);
        Assert.Empty(verdict.Faults);
    }

    [Fact]
    public void The_real_september_file_is_refused_with_both_defects_and_the_transfer_hint()
    {
        // The measured shape of DeviceDatabase_TAC16Sep2026.csv, reproduced exactly by the
        // scanner and by an independent Python parse of the same 349 MB.
        var verdict = TacScanRules.Evaluate(Scan(
            records: 482_047,
            malformed: 27,
            repeatedTacs: 211_135,
            repeatedRecords: 211_135,
            firstMalformedLine: 113_551));

        var message = Assert.IsType<string>(verdict.Rejection);

        Assert.Contains("no TAC version was created", message, StringComparison.Ordinal);
        Assert.Contains("27 record(s)", message, StringComparison.Ordinal);
        Assert.Contains("line 113,551", message, StringComparison.Ordinal);
        Assert.Contains("211,135 TAC(s) appear more than once", message, StringComparison.Ordinal);
        Assert.Contains("of the file's 482,047 records", message, StringComparison.Ordinal);
        Assert.Contains("43.8", message, StringComparison.Ordinal);
        Assert.Contains("damaged transfer", message, StringComparison.Ordinal);

        Assert.Equal(2, verdict.Faults.Count);
        Assert.Contains(verdict.Faults, f => f.RuleCode == "TAC_RECORD_FIELD_COUNT");
        Assert.Contains(verdict.Faults, f => f.RuleCode == "TAC_DUPLICATE");
        Assert.All(verdict.Faults, f => Assert.Equal("error", f.Severity));
    }

    [Fact]
    public void Duplicates_alone_do_not_claim_a_damaged_transfer()
    {
        // The quiet case, and the dangerous one: a duplicated export whose CSV is clean would
        // load without a single parser complaint and double every count joined to sqm.tac. It
        // must still be refused - but the evidence for "damaged transfer" is not there.
        var verdict = TacScanRules.Evaluate(Scan(
            records: 482_047, repeatedTacs: 211_135, repeatedRecords: 211_135));

        var message = Assert.IsType<string>(verdict.Rejection);

        Assert.Contains("211,135 TAC(s) appear more than once", message, StringComparison.Ordinal);
        Assert.DoesNotContain("damaged transfer", message, StringComparison.Ordinal);
        Assert.DoesNotContain("bytes missing", message, StringComparison.Ordinal);
        Assert.Equal("TAC_DUPLICATE", Assert.Single(verdict.Faults).RuleCode);
    }

    [Fact]
    public void Malformed_records_alone_do_not_claim_a_damaged_transfer()
    {
        var verdict = TacScanRules.Evaluate(Scan(
            records: 482_047, malformed: 27, firstMalformedLine: 113_551));

        var message = Assert.IsType<string>(verdict.Rejection);

        Assert.Contains("bytes missing", message, StringComparison.Ordinal);
        Assert.DoesNotContain("damaged transfer", message, StringComparison.Ordinal);
        Assert.DoesNotContain("appear more than once", message, StringComparison.Ordinal);
    }
}
