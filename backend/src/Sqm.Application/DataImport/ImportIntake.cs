namespace Sqm.Application.DataImport;

/// <summary>
/// What becomes of a file once its bytes are stored: a new import in the queue, or the import its
/// content already has.
/// </summary>
/// <remarks>
/// <para>
/// One place for the rules, because a file now arrives two ways: the Import Center's upload
/// endpoint, and <c>--import-file</c> for a file already on the server. The second exists because
/// uploads through the department's VPN broke at every drop (2026-10-25: two of two attempts at a
/// 370 MB day, at 12.6 and 16.5 minutes). Two copies of these rules would disagree the first time
/// one of them was corrected.
/// </para>
/// <para>
/// The caller stores the bytes first - <see cref="IImportFileStore.SaveAsync"/> hashes them as they
/// are written - and decides who may do this: the endpoint checks the user's permission, the
/// command is run by whoever holds the server.
/// </para>
/// </remarks>
public static class ImportIntake
{
    /// <summary>Registers a stored file and queues its import, or finds the import it already has.</summary>
    /// <remarks>
    /// <para>
    /// <b>No cancellation token, and that is the point.</b> The bytes are stored by the time this
    /// runs, so there is nothing left to save by stopping: stopping part way can only leave a file
    /// registered with no job. Only the enqueue used to be off the request's token. On 2026-11-03
    /// the file for 26 October was registered and got neither a job nor an audit row, among
    /// neighbours whose steps took up to 10.8 seconds apart while an import was running: a long
    /// window for a connection to end in. Nothing logged why that one stopped, so the token is a
    /// likely cause, not a proven one; a failed enqueue would look the same. Whatever the cause,
    /// a file that does end up here is listed for the operator: see
    /// <see cref="QueueStoredFileAsync"/>.
    /// </para>
    /// </remarks>
    /// <param name="repository">The Import Center's history and queue.</param>
    /// <param name="files">The file store the bytes were written to.</param>
    /// <param name="sourceCode">Data source, upper case.</param>
    /// <param name="originalFileName">The name the file arrived under; a daily file's date is read from it.</param>
    /// <param name="stored">Where the bytes went, and their hash.</param>
    /// <param name="actor">Who sent it.</param>
    /// <param name="correlationId">The request's trace identifier, when there is a request.</param>
    public static async Task<IntakeResult> AcceptAsync(
        IImportJobRepository repository,
        IImportFileStore files,
        string sourceCode,
        string originalFileName,
        StoredFile stored,
        string actor,
        string? correlationId)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(stored);

        var registered = await repository
            .RegisterFileAsync(sourceCode, originalFileName, stored, actor, CancellationToken.None)
            .ConfigureAwait(false);

        if (!registered.IsNew)
        {
            // The bytes just written duplicate a file already on disk. Removing the copy keeps
            // storage honest: one content hash, one stored blob.
            await files.DeleteAsync(stored.StoredPath, CancellationToken.None).ConfigureAwait(false);
        }

        // A known file with no job is not a duplicate import, it is an unfinished upload: the
        // file was registered and the request ended - a dropped connection, a database blip -
        // before its job was queued. Refusing it as a duplicate left that content importable by
        // no route at all, since reprocessing needs a job to start from. It is queued now, from
        // the copy already stored.
        if (!registered.IsNew && registered.ExistingJobId is not null)
        {
            await repository.WriteAuditAsync(
                actor, "import.upload.duplicate", registered.ExistingJobId, registered.FileId,
                null, correlationId,
                new { fileName = originalFileName, sha256 = stored.Sha256 },
                CancellationToken.None).ConfigureAwait(false);

            return new IntakeResult(registered, null);
        }

        // The day this file claims to describe, read from its name and recorded NOW rather than
        // when the worker gets to it.
        //
        // It is what puts the job in the right place in the queue: the claim refuses to run a day
        // while an earlier day for the same source is still unlanded, and it can only compare
        // dates that already exist. A job enqueued without one used to sit outside that ordering
        // entirely - and outside the "effective import for this day" rule, where every dateless
        // job matched every other one and each new upload demoted the last.
        //
        // A hint, not the authority: the processor re-derives it and CompleteAsync records what
        // was actually imported, so a misnamed file is corrected rather than believed.
        var declaredDate = DailyFileName.BusinessDateOf(originalFileName);

        var jobId = await repository
            .EnqueueAsync(sourceCode, registered.FileId, declaredDate, actor, ct: CancellationToken.None)
            .ConfigureAwait(false);

        await repository.WriteAuditAsync(
            actor, "import.upload", jobId, registered.FileId, null, correlationId,
            new { fileName = originalFileName, bytes = stored.SizeBytes, sha256 = stored.Sha256 },
            CancellationToken.None).ConfigureAwait(false);

        return new IntakeResult(registered, jobId);
    }

    /// <summary>Queues a stored file that has no job, from the copy already stored.</summary>
    /// <remarks>
    /// <para>
    /// The way back for a file <see cref="AcceptAsync"/> did not finish with - a failed enqueue, a
    /// process that died. A day with no job blocks nothing, so nothing else would ever say it is
    /// missing, and this list is what does. Sending the file again also works, and costs the whole
    /// upload. Only a file still on the list is queued, so two people asking cannot queue it twice
    /// unless they ask in the same instant, and then the day is imported twice, which is harmless.
    /// </para>
    /// <para>
    /// A file gone from the store answers <see cref="ReprocessOutcome.FileGone"/>: with the store
    /// away a job queued now would only wait, and saying so is more use than a queue entry.
    /// </para>
    /// </remarks>
    /// <param name="repository">The Import Center's history and queue.</param>
    /// <param name="files">The file store the original is read from.</param>
    /// <param name="sourceCode">Data source, upper case. The file must belong to it.</param>
    /// <param name="fileId">The file to queue.</param>
    /// <param name="actor">Who asked for it.</param>
    /// <param name="correlationId">The request's trace identifier, when there is a request.</param>
    /// <param name="ct">Cancellation token; it stops the checks, never the queueing.</param>
    public static async Task<ReprocessResult> QueueStoredFileAsync(
        IImportJobRepository repository,
        IImportFileStore files,
        string sourceCode,
        long fileId,
        string actor,
        string? correlationId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(files);

        var unqueued = await repository.GetUnqueuedFilesAsync(ct).ConfigureAwait(false);
        var file = unqueued.FirstOrDefault(f => f.FileId == fileId && f.SourceCode == sourceCode);

        if (file is null)
        {
            return new ReprocessResult(ReprocessOutcome.NotFound, null, null);
        }

        if (!await files.ExistsAsync(file.StoredPath, ct).ConfigureAwait(false))
        {
            return new ReprocessResult(ReprocessOutcome.FileGone, null, file.StoredPath);
        }

        var jobId = await repository
            .EnqueueAsync(
                file.SourceCode, file.FileId, DailyFileName.BusinessDateOf(file.OriginalFileName), actor,
                ct: CancellationToken.None)
            .ConfigureAwait(false);

        await repository.WriteAuditAsync(
            actor, "import.upload.queued", jobId, file.FileId, null, correlationId,
            new { fileName = file.OriginalFileName, bytes = file.SizeBytes },
            CancellationToken.None).ConfigureAwait(false);

        return new ReprocessResult(ReprocessOutcome.Queued, jobId, file.StoredPath);
    }

    /// <summary>"Import again": queues a finished import's file as a new job, its day's next revision.</summary>
    /// <remarks>
    /// A new job against the same file, not a reset of the old one. History is a record of what
    /// happened, and rewriting the failed attempt would erase the evidence of why the reprocess was
    /// needed. The file must be readable, not only recorded as present: the first machine's 134
    /// bulk-loaded files are recorded at a drive they have since left (2026-10-26), and a job
    /// queued against one could only fail.
    /// </remarks>
    /// <param name="repository">The Import Center's history and queue.</param>
    /// <param name="files">The file store the original is read from.</param>
    /// <param name="jobId">The import to run again.</param>
    /// <param name="actor">Who asked for it.</param>
    /// <param name="correlationId">The request's trace identifier, when there is a request.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<ReprocessResult> ReprocessAsync(
        IImportJobRepository repository,
        IImportFileStore files,
        long jobId,
        string actor,
        string? correlationId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(files);

        var original = await repository.GetJobAsync(jobId, ct).ConfigureAwait(false);
        if (original is null)
        {
            return new ReprocessResult(ReprocessOutcome.NotFound, null, null);
        }

        if (!original.IsBlobPresent || !await files.ExistsAsync(original.StoredPath, ct).ConfigureAwait(false))
        {
            return new ReprocessResult(ReprocessOutcome.FileGone, null, original.StoredPath);
        }

        var newJobId = await repository.EnqueueAsync(
            original.Summary.SourceCode,
            original.FileId,
            original.Summary.BusinessDate,
            actor,
            priority: 1,
            reprocessOfJobId: jobId,
            ct).ConfigureAwait(false);

        await repository.WriteAuditAsync(
            actor, "import.reprocess", newJobId, null, null, correlationId,
            new { originalJobId = jobId }, ct).ConfigureAwait(false);

        return new ReprocessResult(ReprocessOutcome.Queued, newJobId, original.StoredPath);
    }
}

/// <summary>What became of a request to import a job's file again.</summary>
public enum ReprocessOutcome
{
    /// <summary>Queued as a new job.</summary>
    Queued,

    /// <summary>There is no such import.</summary>
    NotFound,

    /// <summary>Its original file cannot be read: deleted, or not where it is recorded.</summary>
    FileGone,
}

/// <summary>The outcome of <see cref="ImportIntake.ReprocessAsync"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="QueuedJobId">The new job, when one was queued.</param>
/// <param name="StoredPath">Where the original was looked for.</param>
public sealed record ReprocessResult(ReprocessOutcome Outcome, long? QueuedJobId, string? StoredPath);

/// <summary>The outcome of <see cref="ImportIntake.AcceptAsync"/>.</summary>
/// <param name="File">The registered file - new, or the one these bytes already were.</param>
/// <param name="QueuedJobId">The import just queued; <see langword="null"/> when the content already had one.</param>
public sealed record IntakeResult(RegisteredFile File, long? QueuedJobId);
