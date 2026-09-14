using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Sqm.Application.DataImport;

namespace Sqm.Ingestion.Processing;

/// <summary>Imports one day of subscriber device/SIM changes.</summary>
/// <remarks>
/// <para>
/// The file is read twice, on purpose. The first pass validates and counts without writing
/// anything; the second streams the bytes into ClickHouse. A single pass would mean either
/// writing rows before knowing whether the file is sound, or parsing 8 million rows into this
/// process so they can be inspected and re-serialised.
/// </para>
/// <para>
/// The second pass has two forms, and which one runs is decided by the first. When no row was
/// rejected - the case for every file delivered so far - the file is copied to the socket
/// verbatim, at disk speed. Only when rows must be excluded does the worker write the CSV itself,
/// line by line, which is slower but is the only way to import the good rows from a file that
/// also contains bad ones.
/// </para>
/// </remarks>
internal sealed partial class SqmDailyProcessor(
    IAnalyticsIngestionStore analytics,
    ILogger<SqmDailyProcessor> logger) : IImportProcessor
{
    [LoggerMessage(EventId = 3300, Level = LogLevel.Information,
        Message = "Validated {FileName}: {Total} rows, {Rejected} rejected, {Warned} warned")]
    private partial void LogValidated(string fileName, long total, long rejected, long warned);

    /// <summary>How many example rows to keep per broken rule.</summary>
    /// <remarks>
    /// A wholly malformed 8-million-row file would otherwise write 8 million error rows and make
    /// error handling the storage problem. Ten examples is enough to see the shape of the
    /// failure; the count tells the operator how widespread it is.
    /// </remarks>
    private const int SamplesPerRule = 10;

    private const int MaxSampleLineLength = 500;

    public string SourceCode => "SQM";

    [GeneratedRegex(@"(\d{4}-\d{2}-\d{2})", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex BusinessDatePattern { get; }

    public async Task<ImportOutcome> ProcessAsync(
        ClaimedJob job, IImportContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        var businessDate = job.BusinessDate ?? ParseBusinessDate(job.OriginalFileName)
            ?? throw new ImportRejectedException(
                $"Cannot determine which day '{job.OriginalFileName}' describes. The name must "
                + "contain a date in YYYY-MM-DD form.");

        // ---------------------------------------------------------------- validate
        await context.EnterStageAsync(
            ImportJobStatus.Validating,
            $"Checking the column contract and every row of {job.OriginalFileName}", ct)
            .ConfigureAwait(false);

        var validation = await ValidateAsync(job, context, ct).ConfigureAwait(false);
        LogValidated(job.OriginalFileName, validation.TotalRows, validation.RejectedRows,
            validation.WarnedRows);

        if (validation.TotalRows == 0)
        {
            throw new ImportRejectedException("The file contained a header but no data rows.");
        }

        if (validation.Groups.Count > 0)
        {
            await context.QuarantineAsync([.. validation.Groups.Values.Select(g => g.ToWrite())], ct)
                .ConfigureAwait(false);
        }

        // A file where most rows are unusable is a different kind of event from a file with a few
        // bad rows: it usually means the format changed or the wrong file was sent. Importing the
        // remainder would produce a day that looks complete and is not.
        var rejectionRate = (double)validation.RejectedRows / validation.TotalRows;
        if (rejectionRate > 0.05)
        {
            return new ImportOutcome(
                ImportJobStatus.Quarantined,
                new ImportCounters(
                    RowsInput: validation.TotalRows,
                    RowsValid: validation.TotalRows - validation.RejectedRows,
                    RowsInvalid: validation.RejectedRows,
                    RowsRejected: validation.TotalRows),
                MakeEffective: false,
                $"Rejected: {validation.RejectedRows:N0} of {validation.TotalRows:N0} rows "
                + $"({rejectionRate:P1}) failed validation. Nothing was imported.");
        }

        if (await context.IsCancellationRequestedAsync(ct).ConfigureAwait(false))
        {
            return Cancelled(validation);
        }

        // ---------------------------------------------------------------- prepare
        await context.EnterStageAsync(
            ImportJobStatus.Deduplicating,
            $"Checking whether {businessDate:yyyy-MM-dd} is already loaded", ct).ConfigureAwait(false);

        var existing = await analytics.CountEventsForDateAsync(businessDate, ct).ConfigureAwait(false);
        var sequence = await ResolveSequenceAsync(businessDate, ct).ConfigureAwait(false);

        if (existing > 0)
        {
            // This is what makes the import idempotent, and it is also how a corrected file for
            // an already-imported day replaces it. Removing the day first means re-running any
            // import is safe; appending would double it, silently, exactly as an early version of
            // the mart refresh did before it was made to drop and rebuild.
            await context.NoteAsync(
                "warning",
                $"{existing:N0} events already exist for {businessDate:yyyy-MM-dd}; removing them "
                + "so this file replaces that day rather than adding to it",
                new { existingEvents = existing, businessDate = businessDate.ToString("yyyy-MM-dd") },
                ct).ConfigureAwait(false);

            await analytics.RemoveDayAsync(businessDate, ct).ConfigureAwait(false);
        }

        // ---------------------------------------------------------------- import
        await context.EnterStageAsync(
            ImportJobStatus.Importing,
            $"Writing {validation.AcceptedRows:N0} rows as day {sequence}", ct).ConfigureAwait(false);

        long written;

        if (validation.RejectedRows == 0)
        {
            await using var raw = await context.OpenFileAsync(ct).ConfigureAwait(false);
            written = await analytics.LoadDailyEventsAsync(
                businessDate, sequence, raw,
                bytes => ReportBytes(context, bytes, job.FileBytes, ct),
                ct).ConfigureAwait(false);
        }
        else
        {
            await context.NoteAsync(
                "warning",
                $"{validation.RejectedRows:N0} rows will be excluded; the file is being rewritten "
                + "row by row rather than streamed directly, which is slower",
                null, ct).ConfigureAwait(false);

            await using var filtered = await OpenFilteredAsync(context, ct).ConfigureAwait(false);
            written = await analytics.LoadDailyEventsAsync(
                businessDate, sequence, filtered, null, ct).ConfigureAwait(false);
        }

        // ---------------------------------------------------------------- fold
        // The import is not finished when the rows land. Current state and the marts are derived
        // from the event log, and until they are rebuilt the dashboard is showing yesterday while
        // the event log holds today - the most confusing state the system can be in, because
        // nothing on screen says so.
        await context.EnterStageAsync(
            ImportJobStatus.Aggregating,
            $"Folding {businessDate:yyyy-MM-dd} into current state", ct).ConfigureAwait(false);

        var folded = await analytics.FoldDayAsync(businessDate, ct).ConfigureAwait(false);

        await context.NoteAsync(
            "info",
            $"{folded:N0} bindings had their state updated by this day. Only the new day was "
            + "aggregated, not the full history - the last event decides a binding's state, so "
            + "replaying the rest would produce the same answer at a thousand times the cost.",
            new { bindingsTouched = folded }, ct).ConfigureAwait(false);

        await analytics.RefreshChangeMartsForDayAsync(businessDate, sequence, ct)
            .ConfigureAwait(false);

        // ---------------------------------------------------------------- verify
        await context.EnterStageAsync(
            ImportJobStatus.Finalizing, "Verifying what landed", ct).ConfigureAwait(false);

        var stored = await analytics.CountEventsForDateAsync(businessDate, ct).ConfigureAwait(false);

        if (stored != validation.AcceptedRows)
        {
            // Not a warning. The worker counted the rows it intended to write and the store
            // reports a different number, which means the day is wrong in a way no later stage
            // would notice. Failing here leaves the day removed rather than half-written.
            throw new InvalidOperationException(
                $"Row count mismatch for {businessDate:yyyy-MM-dd}: expected "
                + $"{validation.AcceptedRows:N0} accepted rows, the store holds {stored:N0}.");
        }

        var counters = new ImportCounters(
            RowsInput: validation.TotalRows,
            RowsValid: validation.AcceptedRows,
            RowsInvalid: validation.RejectedRows,
            RowsInserted: written == 0 ? stored : written,
            RowsRejected: validation.RejectedRows);

        var status = validation.RejectedRows > 0 || validation.WarnedRows > 0
            ? ImportJobStatus.PartiallyCompleted
            : ImportJobStatus.Completed;

        var message = status == ImportJobStatus.Completed
            ? $"Imported {stored:N0} rows for {businessDate:yyyy-MM-dd} as day {sequence}."
            : $"Imported {stored:N0} rows for {businessDate:yyyy-MM-dd} as day {sequence}; "
              + $"{validation.RejectedRows:N0} rejected, {validation.WarnedRows:N0} imported with warnings.";

        return new ImportOutcome(status, counters, MakeEffective: true, message);
    }

    private static ImportOutcome Cancelled(ValidationResult validation) => new(
        ImportJobStatus.Cancelled,
        new ImportCounters(RowsInput: validation.TotalRows),
        MakeEffective: false,
        "Cancelled after validation, before anything was written.");

    /// <summary>
    /// Decides which day number this file is.
    /// </summary>
    /// <remarks>
    /// A day that already has a sequence keeps it, so a corrected file replaces the day in place
    /// rather than appearing as a new one at the end of the series. A day that does not gets the
    /// next number. This holds as long as days arrive in order, which the delivery does; a day
    /// arriving out of order gets a sequence that does not match its calendar position, which the
    /// charts do not use - they read <c>data_date</c> - but which would look odd in the event log.
    /// </remarks>
    private async Task<int> ResolveSequenceAsync(DateOnly businessDate, CancellationToken ct)
    {
        var existing = await analytics.GetSequenceForDateAsync(businessDate, ct).ConfigureAwait(false);
        if (existing is { } sequence)
        {
            return sequence;
        }

        var max = await analytics.GetMaxSequenceAsync(ct).ConfigureAwait(false);
        return max + 1;
    }

    /// <summary>Reads the whole file, checking every row, writing nothing.</summary>
    private static async Task<ValidationResult> ValidateAsync(
        ClaimedJob job, IImportContext context, CancellationToken ct)
    {
        await using var stream = await context.OpenFileAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(
            stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 20);

        var header = await reader.ReadLineAsync(ct).ConfigureAwait(false)
            ?? throw new ImportRejectedException("The file was empty.");

        var columns = header.Split(',').Select(c => c.Trim().Trim('"')).ToArray();

        if (!columns.SequenceEqual(SqmRowValidator.ExpectedColumns, StringComparer.OrdinalIgnoreCase))
        {
            // Order is checked, not just membership. A file whose columns were reordered but
            // correctly named would otherwise pass and load every value into the wrong column.
            throw new ImportRejectedException(
                $"Unexpected columns. Expected [{string.Join(", ", SqmRowValidator.ExpectedColumns)}], "
                + $"the file has [{string.Join(", ", columns)}].");
        }

        var result = new ValidationResult();
        var findings = new List<RowFinding>(4);
        long lineNumber = 1;
        long bytesSeen = 0;

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            lineNumber++;
            bytesSeen += line.Length + 1;

            if (line.Length == 0)
            {
                continue;
            }

            result.TotalRows++;
            findings.Clear();
            var verdict = SqmRowValidator.Validate(line, findings);

            switch (verdict)
            {
                case RowVerdict.Accept:
                    result.AcceptedRows++;
                    break;
                case RowVerdict.Warn:
                    result.AcceptedRows++;
                    result.WarnedRows++;
                    break;
                default:
                    result.RejectedRows++;
                    break;
            }

            foreach (var finding in findings)
            {
                result.Record(finding, lineNumber, line);
            }

            if ((result.TotalRows & 0xFFFF) == 0)
            {
                await context.ReportProgressAsync(bytesSeen, job.FileBytes, ct).ConfigureAwait(false);
            }
        }

        return result;
    }

    /// <summary>
    /// Produces a CSV stream containing only the rows that passed validation.
    /// </summary>
    /// <remarks>
    /// A pipe rather than a buffer: the file can be a gigabyte, so the filtered copy is produced
    /// as ClickHouse consumes it and never exists in memory or on disk in full.
    /// </remarks>
    private static async Task<Stream> OpenFilteredAsync(IImportContext context, CancellationToken ct)
    {
        var pipe = new Pipe();
        var source = await context.OpenFileAsync(ct).ConfigureAwait(false);

        _ = Task.Run(async () =>
        {
            try
            {
                using var reader = new StreamReader(
                    source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 20);
                await using var writer = new StreamWriter(
                    pipe.Writer.AsStream(leaveOpen: true), new UTF8Encoding(false),
                    bufferSize: 1 << 20, leaveOpen: true);

                var header = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (header is not null)
                {
                    await writer.WriteLineAsync(header).ConfigureAwait(false);
                }

                var findings = new List<RowFinding>(4);

                while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    findings.Clear();
                    if (SqmRowValidator.Validate(line, findings) != RowVerdict.Reject)
                    {
                        await writer.WriteLineAsync(line).ConfigureAwait(false);
                    }
                }

                await writer.FlushAsync(ct).ConfigureAwait(false);
                pipe.Writer.Complete();
            }
            catch (Exception ex)
            {
                // Completing with the exception surfaces it on the reading side, so the insert
                // fails loudly instead of silently importing a truncated day.
                pipe.Writer.Complete(ex);
            }
            finally
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }
        }, ct);

        return pipe.Reader.AsStream();
    }

    /// <summary>
    /// Fires a progress report without waiting for it.
    /// </summary>
    /// <remarks>
    /// Called from the stream's read path, thousands of times a second, so it must not block the
    /// bytes moving. The context throttles the writes that actually reach the database; failures
    /// are swallowed because a progress bar that could not be updated is not a reason to fail an
    /// import that is otherwise succeeding.
    /// </remarks>
    private static void ReportBytes(
        IImportContext context, long bytesRead, long total, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await context.ReportProgressAsync(bytesRead, total, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best-effort by design; see the remarks above.
            }
        }, CancellationToken.None);
    }

    /// <summary>Reads the business date out of a file name.</summary>
    private static DateOnly? ParseBusinessDate(string fileName)
    {
        var match = BusinessDatePattern.Match(fileName);

        return match.Success
            && DateOnly.TryParseExact(
                match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    /// <summary>Running tallies and per-rule groups built during validation.</summary>
    private sealed class ValidationResult
    {
        public long TotalRows { get; set; }

        public long AcceptedRows { get; set; }

        public long WarnedRows { get; set; }

        public long RejectedRows { get; set; }

        public Dictionary<string, RuleGroup> Groups { get; } = new(StringComparer.Ordinal);

        public void Record(RowFinding finding, long lineNumber, string line)
        {
            var key = $"{finding.RuleCode}|{finding.Column}";

            if (!Groups.TryGetValue(key, out var group))
            {
                group = new RuleGroup(finding.RuleCode, finding.Column, finding.Verdict, lineNumber);
                Groups[key] = group;
            }

            group.Count++;

            if (group.Samples.Count < SamplesPerRule)
            {
                group.Samples.Add(new QuarantineSample(
                    lineNumber,
                    line.Length > MaxSampleLineLength ? line[..MaxSampleLineLength] : line,
                    finding.Value));
            }
        }
    }

    private sealed class RuleGroup(string ruleCode, string? column, RowVerdict verdict, long firstRow)
    {
        public long Count { get; set; }

        public List<QuarantineSample> Samples { get; } = [];

        public QuarantineWrite ToWrite() => new(
            ruleCode, column, SqmRowValidator.SeverityFor(verdict), Count, firstRow,
            SqmRowValidator.DescribeRule(ruleCode), Samples);
    }
}

/// <summary>
/// The file cannot be imported at all, and trying again would not help.
/// </summary>
/// <remarks>
/// Distinguished from any other exception so the worker can skip the retry schedule. Retrying a
/// file whose columns are wrong just fails three more times and delays the operator learning why.
/// </remarks>
public sealed class ImportRejectedException : Exception
{
    public ImportRejectedException(string message) : base(message)
    {
    }

    public ImportRejectedException(string message, Exception inner) : base(message, inner)
    {
    }

    public ImportRejectedException()
    {
    }
}
