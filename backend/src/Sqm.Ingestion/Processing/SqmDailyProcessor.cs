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
    IImportJobRepository repository,
    DashboardSnapshot dashboard,
    FeedQualityMonitor feedQuality,
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

    public async Task<ImportOutcome> ProcessAsync(
        ClaimedJob job, IImportContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        // DailyFileName, not a private copy: the upload endpoint reads the date from the same
        // rule so the job carries it before any worker sees it, which is what the queue's
        // ordering constraint is enforced against. Two spellings of one rule would put jobs in
        // the wrong place in the queue and be very hard to see.
        var businessDate = job.BusinessDate ?? DailyFileName.BusinessDateOf(job.OriginalFileName)
            ?? throw new ImportRejectedException(
                $"Cannot determine which day '{job.OriginalFileName}' describes. The name must "
                + "contain a date in YYYY-MM-DD form.");

        // ---------------------------------------------------------------- validate
        await context.EnterStageAsync(
            ImportJobStatus.Validating,
            $"Checking the column contract and every row of {job.OriginalFileName}", ct)
            .ConfigureAwait(false);

        var validation = await ValidateAsync(job, context, repository, ct).ConfigureAwait(false);
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
                + $"({rejectionRate:P1}) failed validation. Nothing was imported.",
                businessDate);
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

        try
        {
            if (validation.RejectedRows == 0 && !validation.NeedsRewrite)
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
                    validation.RejectedRows > 0
                        ? $"{validation.RejectedRows:N0} rows will be excluded; the file is being "
                          + "rewritten row by row rather than streamed directly, which is slower"
                        : "The file has blank lines or bare-CR line endings, which ClickHouse "
                          + "cannot stream; it is being rewritten row by row, which is slower",
                    null, ct).ConfigureAwait(false);

                await using var filtered = await OpenFilteredAsync(context, ct).ConfigureAwait(false);
                written = await analytics.LoadDailyEventsAsync(
                    businessDate, sequence, filtered, null, ct).ConfigureAwait(false);
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // An insert that fails partway is not atomic: the blocks before the failure stay.
            // Left there, they are a partial day that nothing would ever fold or finish. The day
            // goes, and the retry starts from nothing.
            await analytics.RemoveDayAsync(businessDate, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        // Checked now, before anything is derived from the day. It used to run at the end, after
        // the fold and both mart rebuilds had published whatever landed - so a file ClickHouse
        // read differently from the validator (an unclosed quote swallows the next line) put its
        // garbage into current state and the dashboard before the job failed, and the failure
        // removed nothing.
        var stored = await analytics.CountEventsForDateAsync(businessDate, ct).ConfigureAwait(false);

        if (stored != validation.AcceptedRows)
        {
            await analytics.RemoveDayAsync(businessDate, CancellationToken.None).ConfigureAwait(false);

            throw new InvalidOperationException(
                $"Row count mismatch for {businessDate:yyyy-MM-dd}: expected "
                + $"{validation.AcceptedRows:N0} accepted rows, the store holds {stored:N0}. The "
                + "day was removed rather than kept half-written.");
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
            $"{folded:N0} bindings had their state updated by this day. The last event by date "
            + "decides a binding's state: for the newest day only that day is read; a day that "
            + "arrived after later ones, or replaced one, re-derives its bindings from their "
            + "whole history.",
            new { bindingsTouched = folded }, ct).ConfigureAwait(false);

        await analytics.RefreshChangeMartsForDayAsync(businessDate, sequence, ct)
            .ConfigureAwait(false);

        // The binding history - every binding's dated adds and removes, so one number, SIM or
        // handset's whole timeline is a key read (analytics migration 022). A day it has never
        // seen is added; one it has - this file replacing it, or a retry - rebuilds its month.
        var history = await analytics.RefreshHistoryForDayAsync(businessDate, ct).ConfigureAwait(false);

        await context.NoteAsync(
            history.Kind == HistoryRefreshKind.NotDeployed ? "warning" : "info",
            history.Kind switch
            {
                HistoryRefreshKind.NotDeployed =>
                    "The binding history is not deployed, so timelines will not include this day. Apply "
                    + "analytics migration 022 and run Sqm.Ingestion --backfill-history.",
                HistoryRefreshKind.Added => $"{history.Events:N0} events added to the binding history.",
                _ => $"The binding history for {businessDate:yyyy-MM} was rebuilt from the event log "
                     + $"({history.Reason}): {history.Events:N0} events.",
            },
            new { historyPath = history.Kind.ToString(), historyEvents = history.Events },
            ct).ConfigureAwait(false);

        // What the file looks like against the ordinary days: shifted IMEIs, SIMs with several
        // numbers, malformed IMEIs. Noted on the job when out of line; never a failure - see
        // FeedQualityMonitor.
        await feedQuality.AfterDayAsync(businessDate, context, ct).ConfigureAwait(false);

        // The dashboard's headline figures come from the seq-partitioned marts, not from the
        // day-level ones above, and they are rebuilt for the delivery holding the latest DAY -
        // this one, unless the day arrived late or replaced an earlier one. That rebuild is most
        // of an import (~31 of ~34 minutes), so when more files of this source are queued behind
        // this one it is left to the last of them; see DashboardSnapshot.
        await dashboard.AfterDayAsync(job, context, ct).ConfigureAwait(false);

        // ---------------------------------------------------------------- verify
        await context.EnterStageAsync(
            ImportJobStatus.Finalizing, "Recording what landed", ct).ConfigureAwait(false);

        var counters = new ImportCounters(
            RowsInput: validation.TotalRows,
            RowsValid: validation.AcceptedRows,
            RowsInvalid: validation.RejectedRows,
            RowsInserted: written == 0 ? stored : written,
            RowsRejected: validation.RejectedRows);

        var status = StatusFor(validation.RejectedRows, validation.WarnedRows);

        var message = (validation.RejectedRows, validation.WarnedRows) switch
        {
            (0, 0) => $"Imported {stored:N0} rows for {businessDate:yyyy-MM-dd} as day {sequence}.",
            (0, var warned) =>
                $"Imported {stored:N0} rows for {businessDate:yyyy-MM-dd} as day {sequence}; "
                + $"{warned:N0} imported with warnings.",
            var (rejected, warned) =>
                $"Imported {stored:N0} rows for {businessDate:yyyy-MM-dd} as day {sequence}; "
                + $"{rejected:N0} rejected and not imported, {warned:N0} imported with warnings.",
        };

        // The date goes back with the outcome so CompleteAsync can record it. The job was
        // enqueued without one when the file came from the browser, and this is the only
        // place that knows which day it was.
        return new ImportOutcome(status, counters, MakeEffective: true, message, businessDate);
    }

    /// <summary>
    /// Partially completed means rows were rejected and are not in the data. Nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A warning used to count too, and every daily file carries a few thousand IMEIs that are
    /// neither 14 digits nor the unknown-device sentinel. So all 60 days imported through the
    /// platform read "partially completed" while not one row of 422.9 million had been rejected,
    /// and the status stopped meaning anything. Decided by the product owner, 2026-09-24: a day
    /// whose every row landed is Completed, and its warnings are shown beside it.
    /// </para>
    /// <para>
    /// The cost is the alarm the old rule was kept for. On 25 and 26 August about 10% of rows
    /// carried IMEIs cut to eight digits - 250 times the usual count - and that went unnoticed,
    /// because the status could not get any worse than it already was. The warning count beside
    /// the status is where such a day now shows.
    /// </para>
    /// </remarks>
    internal static ImportJobStatus StatusFor(long rejectedRows, long warnedRows)
    {
        // Taken and deliberately ignored, so that putting warnings back into the status is a
        // change to this line - which ImportStatusTests fails on - not a quiet edit at the caller.
        _ = warnedRows;

        return rejectedRows > 0 ? ImportJobStatus.PartiallyCompleted : ImportJobStatus.Completed;
    }

    private static ImportOutcome Cancelled(ValidationResult validation) => new(
        ImportJobStatus.Cancelled,
        new ImportCounters(RowsInput: validation.TotalRows),
        MakeEffective: false,
        "Cancelled after validation, before anything was written.");

    /// <summary>
    /// Decides which day number this file is; see
    /// <see cref="IAnalyticsIngestionStore.ResolveSequenceForDateAsync"/>.
    /// </summary>
    private Task<int> ResolveSequenceAsync(DateOnly businessDate, CancellationToken ct) =>
        analytics.ResolveSequenceForDateAsync(businessDate, ct);

    /// <summary>Reads the whole file, checking every row, writing nothing.</summary>
    private static async Task<ValidationResult> ValidateAsync(
        ClaimedJob job, IImportContext context, IImportJobRepository repository, CancellationToken ct)
    {
        await using var stream = new BareCarriageReturnProbe(
            await context.OpenFileAsync(ct).ConfigureAwait(false));
        using var reader = new StreamReader(
            stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 20);

        var header = await reader.ReadLineAsync(ct).ConfigureAwait(false)
            ?? throw new ImportRejectedException("The file was empty.");

        var columns = header.Split(',').Select(c => c.Trim().Trim('"')).ToArray();

        // The header is matched against the contracts on record rather than against a constant.
        // A source's columns will change one day, and the platform should record that it happened
        // and decide whether it is survivable - not fail with "unexpected columns" and leave
        // someone to work out which.
        var schema = await repository
            .ResolveSchemaAsync(job.JobId, job.SourceCode, columns, ct).ConfigureAwait(false);

        if (schema.Verdict == SchemaVerdict.Rejected)
        {
            throw new ImportRejectedException(schema.Explanation);
        }

        if (schema.Verdict == SchemaVerdict.AcceptedWithWarning)
        {
            await context.NoteAsync("warning", schema.Explanation, null, ct).ConfigureAwait(false);
        }

        // Beyond the contract, this processor can only read the four columns it knows how to
        // validate. A compatible extension that appends columns is fine; a file that does not
        // start with these four is not something this code can parse at all.
        foreach (var (expected, index) in SqmRowValidator.ExpectedColumns.Select((c, i) => (c, i)))
        {
            if (index >= columns.Length
                || !string.Equals(columns[index], expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new ImportRejectedException(
                    $"Column {index + 1} must be '{expected}'. This importer reads "
                    + $"[{string.Join(", ", SqmRowValidator.ExpectedColumns)}] and cannot parse a "
                    + "file shaped differently.");
            }
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
                // Not a row, and harmless here - but ClickHouse refuses a blank line anywhere
                // in a CSV stream, after it has already written the blocks before it.
                result.NeedsRewrite = true;
                continue;
            }

            result.TotalRows++;
            findings.Clear();
            var verdict = SqmRowValidator.Validate(line, findings, columns.Length);

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

        result.NeedsRewrite |= stream.Seen;

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

                // The same column count validation used, or this pass would drop every row of a
                // file that validation accepted.
                var columns = Math.Max(4, header?.Split(',').Length ?? 4);

                var findings = new List<RowFinding>(4);

                while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    findings.Clear();
                    if (SqmRowValidator.Validate(line, findings, columns) != RowVerdict.Reject)
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


    /// <summary>Running tallies and per-rule groups built during validation.</summary>
    private sealed class ValidationResult
    {
        public long TotalRows { get; set; }

        /// <summary>
        /// True when the file validates but cannot be streamed to ClickHouse as it stands: it has
        /// a blank line, or lines ending in a bare CR. Such a file goes through the rewriting path.
        /// </summary>
        public bool NeedsRewrite { get; set; }

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
