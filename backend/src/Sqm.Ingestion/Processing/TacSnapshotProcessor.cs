using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Sqm.Application.DataImport;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Ingestion.Processing;

/// <summary>Imports a GSMA TAC snapshot as a new, inactive version.</summary>
/// <remarks>
/// <para>
/// This processor never changes what the product shows. It loads the file as a numbered
/// version, checks it, diffs it against the active one and stops at READY — the decision the
/// product owner made, and for a concrete reason: TAC determines the manufacturer, model and
/// capability on every screen, so a bad file that activated itself would be wrong everywhere at
/// once. The review it costs is one click against a measured change rate of 1,000–1,650 rows a
/// month.
/// </para>
/// <para>
/// Validation is done in the store rather than row by row here. The file is 206 MB of quoted
/// CSV whose 26 columns are all strings, so there are no types to get wrong; the questions that
/// matter are about the set — are TACs unique, are they 8 digits, what date does the data claim
/// — and those are answered far better by SQL than by a loop.
/// </para>
/// </remarks>
internal sealed partial class TacSnapshotProcessor(
    ITacVersionStore store,
    IImportJobRepository repository,
    ILogger<TacSnapshotProcessor> logger) : IImportProcessor
{
    [LoggerMessage(EventId = 3700, Level = LogLevel.Information,
        Message = "TAC version {VersionId} is ready for review: +{Added} -{Removed} ~{Updated}")]
    private partial void LogReady(int versionId, int added, int removed, int updated);

    [LoggerMessage(EventId = 3701, Level = LogLevel.Information,
        Message = "Scanned {FileName}: {Records} records, {Malformed} malformed, "
            + "{RepeatedTacs} repeated TAC(s)")]
    private partial void LogScanned(
        string fileName, long records, long malformed, long repeatedTacs);

    /// <summary>
    /// How large a diff has to be before the import stops rather than waiting for review.
    /// </summary>
    /// <remarks>
    /// The observed monthly change is 1,000–1,650 TACs. Ten thousand is roughly an order of
    /// magnitude above that, which is the level at which "the file changed a lot" stops being a
    /// plausible month and starts being a different dataset. It is a warning on the timeline,
    /// not a rejection: the reviewer is the one who decides, and this makes sure they look.
    /// </remarks>
    private const int UnusualDiffThreshold = 10_000;

    public string SourceCode => "TAC";

    [GeneratedRegex(@"(\d{1,2})([A-Za-z]{3})(\d{4})", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex DatasetDatePattern { get; }

    public async Task<ImportOutcome> ProcessAsync(
        ClaimedJob job, IImportContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        await context.EnterStageAsync(
            ImportJobStatus.Validating, $"Checking the column contract of {job.OriginalFileName}", ct)
            .ConfigureAwait(false);

        await ValidateHeaderAsync(job, context, ct).ConfigureAwait(false);

        await context.EnterStageAsync(
            ImportJobStatus.Validating,
            $"Checking the structure of every record in {job.OriginalFileName}", ct)
            .ConfigureAwait(false);

        await ScanStructureAsync(job, context, ct).ConfigureAwait(false);

        // The job's own id, not max(version_id) + 1 read back from ClickHouse. Two TAC jobs
        // running at once - two workers, or one API-hosted and one standalone - read the same
        // max and loaded into the same version, and the duplicate check then dropped both. The
        // job id is unique by construction.
        var versionId = checked((int)job.JobId);
        var activeVersionId = await store.GetActiveVersionIdAsync(ct).ConfigureAwait(false);

        // A retry of this job gets the same id, so whatever an earlier attempt left half-loaded
        // is cleared first. Dropping a partition that does not exist is a no-op.
        await store.DropVersionAsync(versionId, CancellationToken.None).ConfigureAwait(false);

        await context.EnterStageAsync(
            ImportJobStatus.Importing, $"Loading as TAC version {versionId} (not yet active)", ct)
            .ConfigureAwait(false);

        long rows;
        try
        {
            await using var file = await context.OpenFileAsync(ct).ConfigureAwait(false);
            rows = await store.LoadVersionAsync(
                versionId, file,
                bytes => ReportBytes(context, bytes, job.FileBytes, ct), ct).ConfigureAwait(false);
        }
        catch
        {
            // The version is partitioned by its own id, so removing a half-loaded one is a
            // partition drop: instant, and it cannot touch a version that is fine.
            await store.DropVersionAsync(versionId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        // ---------------------------------------------------------------- check
        await context.EnterStageAsync(
            ImportJobStatus.Enriching, "Checking the loaded version", ct).ConfigureAwait(false);

        var check = await store.CheckVersionAsync(versionId, ct).ConfigureAwait(false);
        var quarantine = BuildQuarantine(check);

        if (quarantine.Count > 0)
        {
            await context.QuarantineAsync(quarantine, ct).ConfigureAwait(false);
        }

        // A backstop. ScanStructureAsync answers this before anything is loaded and with far more
        // to say about it, so reaching here means the scanner and ClickHouse read the same file
        // differently - which is worth failing loudly over, because one of the two is wrong about
        // the data that decides what every device in the product is called.
        if (check.DistinctTacs != check.RowCount)
        {
            await store.DropVersionAsync(versionId, CancellationToken.None).ConfigureAwait(false);
            throw new ImportRejectedException(
                $"The loaded version holds {check.RowCount - check.DistinctTacs:N0} duplicate "
                + "TAC(s) that the pre-load scan did not see. The two disagree about the file, so "
                + "it has been discarded rather than trusted. This is a defect in the importer, "
                + "not in the file.");
        }

        await context.NoteAsync(
            "info",
            $"Loaded {rows:N0} TACs. Newest lastUpdatedDate in the file: "
            + $"{check.LatestUpdateDate ?? "none"}.",
            check, ct).ConfigureAwait(false);

        // ---------------------------------------------------------------- diff
        TacVersionDiff? diff = null;

        if (activeVersionId is { } previous)
        {
            await context.EnterStageAsync(
                ImportJobStatus.Aggregating,
                $"Comparing against the active version {previous}", ct).ConfigureAwait(false);

            diff = await store.DiffAsync(versionId, previous, ct).ConfigureAwait(false);

            var total = diff.Added + diff.Removed + diff.Updated;

            await context.NoteAsync(
                total > UnusualDiffThreshold ? "warning" : "info",
                $"{diff.Added:N0} TACs added, {diff.Removed:N0} removed, {diff.Updated:N0} "
                + $"changed, {diff.Unchanged:N0} unchanged. "
                + $"{diff.AffectedActiveBindings:N0} active binding(s) sit on a TAC it adds, removes or changes."
                + (total > UnusualDiffThreshold
                    ? $" That is well above the {UnusualDiffThreshold:N0} expected for one month's"
                      + " revision - worth confirming this is the intended file before activating."
                    : string.Empty),
                diff, ct).ConfigureAwait(false);

            LogReady(versionId, diff.Added, diff.Removed, diff.Updated);
        }
        else
        {
            await context.NoteAsync(
                "info", "No active version to compare against; this is the first.", null, ct)
                .ConfigureAwait(false);
        }

        // ---------------------------------------------------------------- register
        await context.EnterStageAsync(
            ImportJobStatus.Finalizing, "Recording the version as ready for review", ct)
            .ConfigureAwait(false);

        await repository.CreateTacVersionAsync(
            job.JobId,
            versionId,
            LabelFor(job.OriginalFileName, versionId),
            ParseDatasetDate(job.OriginalFileName),
            (int)rows,
            activeVersionId,
            diff,
            ct).ConfigureAwait(false);

        var counters = new ImportCounters(
            RowsInput: rows, RowsValid: rows, RowsInserted: rows);

        // Not "Completed": nothing the product shows has changed, and a status that reads as
        // finished would tell the operator the opposite of the truth. The version is loaded and
        // waiting for someone to activate it.
        return new ImportOutcome(
            ImportJobStatus.PartiallyCompleted,
            counters,
            MakeEffective: false,
            $"TAC version {versionId} is loaded and ready. It is NOT active - an administrator "
            + "must activate it before the dashboard uses it.");
    }

    /// <summary>
    /// Reads the whole file once, writing nothing, and refuses it if its structure is unsound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This runs before a version id is allocated, so a file refused here leaves nothing at all
    /// behind - no partition to drop, no gap in the version numbering. That is the difference
    /// from the checks further down, which can only speak after 366 MB has been loaded.
    /// </para>
    /// <para>
    /// Both rules below were already enforced somewhere; neither was usable. Duplicate TACs were
    /// caught after the load, so the file was read, parsed and stored before being thrown away.
    /// Records with the wrong field count were caught by ClickHouse's own parser, which stops at
    /// the first one and reports it as a syntax error - true, and no help at all to an operator
    /// trying to work out whether to re-download the file or call the supplier. Counting every
    /// occurrence first is what turns the failure into a decision.
    /// </para>
    /// </remarks>
    private async Task ScanStructureAsync(
        ClaimedJob job, IImportContext context, CancellationToken ct)
    {
        await using var stream = await context.OpenFileAsync(ct).ConfigureAwait(false);

        var scan = await TacFileScanner.ScanAsync(
            stream,
            (bytes, token) => context.ReportProgressAsync(bytes, job.FileBytes, token),
            ct).ConfigureAwait(false);

        LogScanned(job.OriginalFileName, scan.Records, scan.MalformedRecords, scan.RepeatedTacs);

        if (scan.Records == 0)
        {
            throw new ImportRejectedException("The file contained a header but no data rows.");
        }

        var verdict = TacScanRules.Evaluate(scan);

        if (verdict.Faults.Count > 0)
        {
            // Written before the rejection, so the samples survive it and the operator can look
            // at the actual records on the job's page rather than at a message about them.
            await context.QuarantineAsync(verdict.Faults, ct).ConfigureAwait(false);
        }

        if (verdict.Rejection is null)
        {
            await context.NoteAsync(
                "info",
                $"Structure checked: {scan.Records:N0} records, {scan.DistinctTacs:N0} distinct "
                + "TACs, no repeats and no malformed records.",
                scan, ct).ConfigureAwait(false);

            return;
        }

        throw new ImportRejectedException(verdict.Rejection);
    }

    /// <summary>Reads the header and checks it against the GSMA contract.</summary>
    private async Task ValidateHeaderAsync(
        ClaimedJob job, IImportContext context, CancellationToken ct)
    {
        await using var stream = await context.OpenFileAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(
            stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);

        var header = await reader.ReadLineAsync(ct).ConfigureAwait(false)
            ?? throw new ImportRejectedException("The file was empty.");

        var columns = header.Split(',').Select(c => c.Trim().Trim('"')).ToArray();

        // GSMA has extended this export before and will again, so the header is matched against
        // the contracts on record. An appended column is recorded as a new schema version and
        // imported; a removed or reordered one is refused.
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

        if (columns.Length < ClickHouseTacVersionStore.Columns.Length)
        {
            throw new ImportRejectedException(
                $"Expected at least {ClickHouseTacVersionStore.Columns.Length} columns, the file "
                + $"has {columns.Length}. This is not a GSMA TAC export.");
        }

        for (var i = 0; i < ClickHouseTacVersionStore.Columns.Length; i++)
        {
            if (!string.Equals(columns[i], ClickHouseTacVersionStore.Columns[i],
                StringComparison.OrdinalIgnoreCase))
            {
                throw new ImportRejectedException(
                    $"Column {i + 1} is '{columns[i]}', expected "
                    + $"'{ClickHouseTacVersionStore.Columns[i]}'. Every column here is text, so a "
                    + "reordered header would load without a single error and put the wrong values "
                    + "in every column.");
            }
        }
    }

    private static List<QuarantineWrite> BuildQuarantine(TacVersionCheck check)
    {
        var groups = new List<QuarantineWrite>();

        if (check.MalformedTacs > 0)
        {
            groups.Add(new QuarantineWrite(
                "TAC_NOT_8_DIGITS", "tac", "warning", check.MalformedTacs, null,
                "The TAC was not exactly 8 digits. Such a row cannot match any IMEI, so the "
                + "devices it describes will show as unregistered.",
                []));
        }

        if (check.BlankManufacturers > 0)
        {
            groups.Add(new QuarantineWrite(
                "TAC_NO_MANUFACTURER", "manufacturer", "warning", check.BlankManufacturers, null,
                "The manufacturer was blank or 'Not Known'. The row is kept; those devices will "
                + "appear under an unknown vendor.",
                []));
        }

        return groups;
    }

    /// <summary>Reads the dataset date out of a name like <c>DeviceDatabase_TAC1Sep2026.csv</c>.</summary>
    private static DateOnly? ParseDatasetDate(string fileName)
    {
        var match = DatasetDatePattern.Match(fileName);
        if (!match.Success)
        {
            return null;
        }

        var text = $"{match.Groups[1].Value}-{match.Groups[2].Value}-{match.Groups[3].Value}";

        return DateOnly.TryParseExact(
            text, "d-MMM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private static string LabelFor(string fileName, int versionId)
    {
        var date = ParseDatasetDate(fileName);
        return date is { } d
            ? $"v{d:yyyy.MM.dd}"
            : $"v{versionId}";
    }

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
                // Best-effort: a progress bar that could not be updated is not a reason to fail
                // an import that is otherwise succeeding.
            }
        }, CancellationToken.None);
    }
}
