using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Sqm.Api.Infrastructure;
using Sqm.Application.DataImport;

using Sqm.Api.Auth;
using Sqm.Application.Identity;

namespace Sqm.Api.Endpoints;

/// <summary>Import Center endpoints: upload, history, detail, operator actions.</summary>
public static class ImportEndpoints
{
    /// <summary>Registers the import routes.</summary>
    public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Per route rather than per group: reading import history and submitting a gigabyte of
        // subscriber data are not the same risk, and a Data Operator has one without the other.
        var group = app.MapGroup("/api/v1/imports").WithTags("Imports")
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.ImportView);

        // The source code decides which upload permission applies, so it is checked inside the
        // handler rather than declared here - see UploadAsync.
        group.MapPost("/{sourceCode}/upload", UploadAsync)
            .WithName("UploadImportFile")
            .WithSummary("Uploads a file and queues it for import.")
            .DisableAntiforgery()
            .WithMetadata(new DisableRequestSizeLimitAttribute());

        // Asks the same question the upload asks, without the body.
        //
        // The server already refuses an unauthorised upload before reading a byte - but the
        // BROWSER does not know that, and pushes the whole file at it regardless. A 319.6 MB
        // daily file was sent and refused in 0.4 ms, which is 319.6 MB across the wire to learn
        // something a 200-byte request could have said first.
        group.MapPost("/{sourceCode}/upload/preflight", PreflightAsync)
            .WithName("PreflightImportUpload")
            .WithSummary("Whether this file would be accepted, asked before sending it.");

        group.MapGet("/", ListAsync)
            .WithName("ListImports")
            .WithSummary("Import history, newest first.");

        group.MapGet("/{jobId:long}", GetAsync)
            .WithName("GetImport")
            .WithSummary("One import: timeline, progress, quarantine and lineage.");

        group.MapGet("/{jobId:long}/preview", PreviewAsync)
            .WithName("PreviewImportFile")
            .WithSummary("The first rows of the stored file, exactly as delivered.");

        group.MapGet("/{jobId:long}/quarantine/{summaryId:long}", GetQuarantineSamplesAsync)
            .WithName("GetQuarantineSamples")
            .WithSummary("Example rows for one quarantine rule.");

        group.MapPost("/{jobId:long}/cancel", CancelAsync)
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.ImportCancel)
            .WithName("CancelImport")
            .WithSummary("Asks a queued or running import to stop at the next stage boundary.");

        group.MapPost("/{jobId:long}/reprocess", ReprocessAsync)
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.ImportReprocess)
            .WithName("ReprocessImport")
            .WithSummary("Queues the same stored file again as a new job.");

        group.MapGet("/freshness", GetFreshnessAsync)
            .WithName("GetImportFreshness")
            .WithSummary("How current each source is, and which expected days are missing.");

        group.MapGet("/worker-health", GetWorkerHealthAsync)
            .WithName("GetWorkerHealth")
            .WithSummary("Queue depth, running jobs, failures and stale leases.");

        return app;
    }

    private static async Task<IResult> UploadAsync(
        string sourceCode,
        IImportFileStore fileStore,
        IImportJobRepository repository,
        IAuditLog audit,
        HttpContext http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);

        var source = sourceCode.ToUpperInvariant();
        var actor = CurrentActor(http);

        // The same decision the preflight made, made again. The client is expected to have asked
        // first, but the client is not what enforces this - a caller that skips the preflight,
        // or whose permission was withdrawn between the two requests, is refused here.
        var refusal = await RefuseUploadAsync(sourceCode, http, audit, ct).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        if (!StreamedUpload.IsMultipart(http.Request))
        {
            return Results.Problem(
                title: "Not a file upload",
                detail: "Send the file as a multipart/form-data request.",
                statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        // The bytes are streamed from the socket into the file store and hashed on the way past.
        // Binding an IFormFile would have the framework buffer the whole body first, writing a
        // gigabyte import to disk twice.
        var received = await StreamedUpload.ReadFileSectionAsync(
            http.Request,
            async (fileName, body, token) =>
            {
                var stored = await fileStore
                    .SaveAsync(source, fileName, body, token).ConfigureAwait(false);
                return new ReceivedFile(fileName, stored);
            },
            ct).ConfigureAwait(false);

        if (received is null)
        {
            return Results.Problem(
                title: "No file",
                detail: "The request carried no file.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var (originalName, stored) = received;

        var registered = await repository
            .RegisterFileAsync(source, originalName, stored, actor, ct).ConfigureAwait(false);

        if (!registered.IsNew)
        {
            // The bytes just written duplicate a file already on disk. Removing the copy keeps
            // storage honest: one content hash, one stored blob.
            await fileStore.DeleteAsync(stored.StoredPath, ct).ConfigureAwait(false);

            await repository.WriteAuditAsync(
                actor, "import.upload.duplicate", registered.ExistingJobId, registered.FileId,
                null, http.TraceIdentifier,
                new { fileName = originalName, sha256 = stored.Sha256 }, ct).ConfigureAwait(false);

            // 409, not an error page. A duplicate upload is a normal thing for an operator to do
            // - the same file sent twice, or a retry after a lost connection - and the useful
            // response is "here is the import you already have", not "something went wrong".
            return Results.Conflict(new DuplicateUploadResponse(
                registered.FileId,
                registered.ExistingJobId,
                registered.ExistingOriginalName ?? originalName,
                stored.Sha256,
                "This file's content has already been uploaded for this source."));
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
        var declaredDate = DailyFileName.BusinessDateOf(originalName);

        var jobId = await repository
            .EnqueueAsync(source, registered.FileId, declaredDate, actor, ct: ct)
            .ConfigureAwait(false);

        await repository.WriteAuditAsync(
            actor, "import.upload", jobId, registered.FileId, null, http.TraceIdentifier,
            new { fileName = originalName, bytes = stored.SizeBytes, sha256 = stored.Sha256 },
            ct).ConfigureAwait(false);

        return Results.Created($"/api/v1/imports/{jobId}", new UploadAcceptedResponse(
            jobId, registered.FileId, originalName, stored.SizeBytes, stored.Sha256));
    }

    private static async Task<IResult> ListAsync(
        IImportJobRepository repository,
        CancellationToken ct,
        string? source = null,
        string? status = null,
        string? from = null,
        string? to = null,
        string? fileName = null,
        bool effectiveOnly = false,
        int page = 1,
        int pageSize = 25)
    {
        var statuses = ParseStatuses(status);
        if (statuses is null)
        {
            return Results.Problem(
                title: "Unknown status",
                detail: $"'{status}' is not an import status.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var filter = new ImportHistoryFilter(
            string.IsNullOrWhiteSpace(source) ? null : source.ToUpperInvariant(),
            statuses.Count == 0 ? null : statuses,
            ParseDate(from),
            ParseDate(to),
            fileName,
            effectiveOnly);

        var size = Math.Clamp(pageSize, 1, 200);
        var offset = (Math.Max(1, page) - 1) * size;

        var items = await repository.ListJobsAsync(filter, size, offset, ct).ConfigureAwait(false);
        var total = await repository.CountJobsAsync(filter, ct).ConfigureAwait(false);

        return Results.Ok(new ImportPage(items, total, Math.Max(1, page), size));
    }

    private static async Task<IResult> GetAsync(
        long jobId, IImportJobRepository repository, CancellationToken ct)
    {
        var detail = await repository.GetJobAsync(jobId, ct).ConfigureAwait(false);
        return detail is null ? Results.NotFound() : Results.Ok(detail);
    }

    /// <summary>
    /// Reads the first few lines of a stored file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reads exactly as many lines as it needs and stops. These files reach a gigabyte, so a
    /// preview that opened the whole thing would be a way to take the server down by clicking a
    /// link twenty times.
    /// </para>
    /// <para>
    /// The rows are returned raw, unparsed and unformatted. The point of a preview is to see what
    /// the source actually sent - a stray quote, a shifted column, a header that changed - and
    /// any tidying the API did on the way past would hide precisely the thing being looked for.
    /// </para>
    /// </remarks>
    private static async Task<IResult> PreviewAsync(
        long jobId,
        IImportJobRepository repository,
        IImportFileStore fileStore,
        CancellationToken ct,
        int lines = 20)
    {
        var detail = await repository.GetJobAsync(jobId, ct).ConfigureAwait(false);

        if (detail is null)
        {
            return Results.NotFound();
        }

        if (!detail.IsBlobPresent
            || !await fileStore.ExistsAsync(detail.StoredPath, ct).ConfigureAwait(false))
        {
            return Results.Problem(
                title: "Original file is gone",
                detail: "The stored file has been deleted, so it cannot be previewed.",
                statusCode: StatusCodes.Status410Gone);
        }

        var take = Math.Clamp(lines, 1, 200);

        await using var stream = await fileStore
            .OpenReadAsync(detail.StoredPath, ct).ConfigureAwait(false);
        using var reader = new StreamReader(
            stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 64 * 1024);

        var header = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        var rows = new List<string>(take);

        while (rows.Count < take
               && await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            rows.Add(line.Length > 1000 ? line[..1000] : line);
        }

        return Results.Ok(new FilePreview(
            detail.Summary.OriginalFileName,
            detail.Summary.FileBytes,
            header?.Split(',').Select(c => c.Trim().Trim('"')).ToArray() ?? [],
            rows,
            detail.Summary.RowsInput));
    }

    private static async Task<IResult> GetQuarantineSamplesAsync(
        long jobId, long summaryId, IImportJobRepository repository, CancellationToken ct)
    {
        var samples = await repository
            .GetQuarantineSamplesAsync(summaryId, 20, ct).ConfigureAwait(false);
        return Results.Ok(samples);
    }

    private static async Task<IResult> CancelAsync(
        long jobId, IImportJobRepository repository, HttpContext http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);

        var accepted = await repository
            .RequestCancellationAsync(jobId, CurrentActor(http), ct).ConfigureAwait(false);

        return accepted
            ? Results.Accepted($"/api/v1/imports/{jobId}")
            : Results.Problem(
                title: "Cannot cancel",
                detail: "The job has already finished, or does not exist.",
                statusCode: StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> ReprocessAsync(
        long jobId, IImportJobRepository repository, HttpContext http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);

        var original = await repository.GetJobAsync(jobId, ct).ConfigureAwait(false);
        if (original is null)
        {
            return Results.NotFound();
        }

        if (!original.IsBlobPresent)
        {
            return Results.Problem(
                title: "Original file is gone",
                detail: "The stored file has been deleted, so this import cannot be run again.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var actor = CurrentActor(http);

        // A new job against the same file, not a reset of the old one. History is a record of
        // what happened, and rewriting the failed attempt would erase the evidence of why the
        // reprocess was needed.
        var newJobId = await repository.EnqueueAsync(
            original.Summary.SourceCode,
            original.FileId,
            original.Summary.BusinessDate,
            actor,
            priority: 1,
            reprocessOfJobId: jobId,
            ct).ConfigureAwait(false);

        await repository.WriteAuditAsync(
            actor, "import.reprocess", newJobId, null, null, http.TraceIdentifier,
            new { originalJobId = jobId }, ct).ConfigureAwait(false);

        return Results.Created($"/api/v1/imports/{newJobId}", new ReprocessResponse(newJobId, jobId));
    }

    private static async Task<IResult> GetFreshnessAsync(
        IImportJobRepository repository, TimeProvider clock, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clock);

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var freshness = await repository.GetFreshnessAsync(today, ct).ConfigureAwait(false);
        return Results.Ok(freshness);
    }

    private static async Task<IResult> GetWorkerHealthAsync(
        IImportJobRepository repository, CancellationToken ct)
    {
        var health = await repository.GetWorkerHealthAsync(ct).ConfigureAwait(false);
        return Results.Ok(health);
    }

    /// <summary>
    /// Parses a comma-separated status filter. Returns <see langword="null"/> when a value is not
    /// a status, so the caller can answer 400 rather than silently ignoring it.
    /// </summary>
    private static List<ImportJobStatus>? ParseStatuses(string? value)
    {
        var statuses = new List<ImportJobStatus>();

        if (string.IsNullOrWhiteSpace(value))
        {
            return statuses;
        }

        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<ImportJobStatus>(part.Replace("_", "", StringComparison.Ordinal), true, out var status))
            {
                return null;
            }

            statuses.Add(status);
        }

        return statuses;
    }

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date : null;

    /// <summary>
    /// Who is performing this action.
    /// </summary>
    /// <remarks>
    /// Authentication is not wired up yet, so this reads the authenticated name when there is one
    /// and falls back to a marker that is obviously a placeholder rather than a plausible user
    /// name. Audit rows written before authentication exists should say so.
    /// </remarks>
    /// <summary>
    /// Everything that can refuse an upload without looking at the bytes.
    /// </summary>
    /// <param name="sourceCode">The source the file is for.</param>
    /// <param name="http">The request, for the caller and their address.</param>
    /// <param name="audit">Where a refusal is recorded.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The problem to return, or <see langword="null"/> when it would be accepted.</returns>
    /// <remarks>
    /// <para>
    /// <b>One function, two callers, on purpose.</b> The preflight exists to give the same answer
    /// the upload would give, and the fastest way to make it lie is to write the rule twice: a
    /// preflight that says yes where the upload says no wastes the very upload it was added to
    /// prevent, and one that says no where the upload says yes silently removes a capability.
    /// </para>
    /// <para>
    /// Which permission applies depends on the source, so it cannot be declared on the route.
    /// </para>
    /// </remarks>
    private static async Task<IResult?> RefuseUploadAsync(
        string sourceCode, HttpContext http, IAuditLog audit, CancellationToken ct)
    {
        var source = sourceCode.ToUpperInvariant();

        var required = source switch
        {
            "SQM" => Permissions.ImportUploadSqm,
            "TAC" => Permissions.ImportUploadTac,
            _ => null,
        };

        if (required is null)
        {
            return Results.Problem(
                title: "Unknown source",
                detail: $"'{sourceCode}' is not a source this platform accepts.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var user = CurrentUser.Require(http);

        if (user.Can(required))
        {
            return null;
        }

        // Audited here, because a check made inside a handler never reaches the pipeline's
        // authorisation result handler - so without this the one refusal that matters most in
        // the import platform would be the only one leaving no trace.
        //
        // Both callers reach this, and that is not double-counting: a caller refused by the
        // preflight does not go on to send the body, so a refused upload still produces one
        // entry. A caller allowed here is audited by the upload itself when it succeeds.
        await audit.WriteAsync(new Sqm.Application.Identity.AuditEntry(
            ActorName: user.Username,
            Action: required,
            Category: Sqm.Application.Identity.AuditCategory.Import,
            Outcome: Sqm.Application.Identity.AuditOutcome.Denied,
            ActorUserId: user.UserId,
            TargetType: "source",
            TargetId: source,
            Ip: http.Connection.RemoteIpAddress?.ToString(),
            CorrelationId: http.TraceIdentifier), ct).ConfigureAwait(false);

        return Results.Problem(
            title: "Not permitted",
            detail: $"Uploading {source} files requires the '{required}' permission.",
            statusCode: StatusCodes.Status403Forbidden);
    }

    /// <summary>Answers whether a file would be accepted, before it is sent.</summary>
    /// <remarks>
    /// <para>
    /// <b>What this cannot tell you, and does not pretend to.</b> The one remaining refusal is a
    /// duplicate, decided by the SHA-256 of the content - and the only way to know that is to
    /// have the content. Guessing from the file name and size would be worse than useless: a
    /// corrected re-delivery usually arrives under the same name at nearly the same size, and
    /// refusing it would be refusing exactly the file an operator most needs to send.
    /// </para>
    /// <para>
    /// So the duplicate check stays where the bytes are, and stays a 409 rather than an error -
    /// re-sending a file is a normal thing for an operator to do.
    /// </para>
    /// </remarks>
    private static async Task<IResult> PreflightAsync(
        string sourceCode,
        UploadPreflightRequest request,
        HttpContext http,
        IAuditLog audit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var refusal = await RefuseUploadAsync(sourceCode, http, audit, ct).ConfigureAwait(false);

        return refusal ?? Results.Ok(new UploadPreflightResponse(
            Source: sourceCode.ToUpperInvariant(),
            FileName: request.FileName,
            SizeBytes: request.SizeBytes,
            Message: "Accepted. Content is checked for duplicates once the file arrives."));
    }

    private static string CurrentActor(HttpContext http) =>
        http.User.Identity?.Name is { Length: > 0 } name ? name : "anonymous@pre-auth";
}

/// <summary>What the client is about to send.</summary>
/// <param name="FileName">The name the file will arrive under.</param>
/// <param name="SizeBytes">How large it is, so the answer can name it.</param>
/// <remarks>
/// Neither field decides anything today - the refusals that can be made without the body depend
/// on the source and the caller, not on the file. They are carried so the answer is about a
/// named file, and so a future size or naming rule has somewhere to live.
/// </remarks>
public sealed record UploadPreflightRequest(string FileName, long SizeBytes);

/// <summary>The upload would be accepted.</summary>
/// <param name="Source">The source, normalised.</param>
/// <param name="FileName">The file the answer is about.</param>
/// <param name="SizeBytes">Its size as the client reported it.</param>
/// <param name="Message">What is still undecided, said plainly.</param>
public sealed record UploadPreflightResponse(
    string Source, string FileName, long SizeBytes, string Message);

/// <summary>The first rows of a stored file, as delivered.</summary>
/// <param name="FileName">The name it arrived under.</param>
/// <param name="FileBytes">Its size.</param>
/// <param name="Columns">The header, split on commas.</param>
/// <param name="Rows">The first data lines, raw.</param>
/// <param name="TotalRows">Rows the import counted, or 0 if it has not run yet.</param>
public sealed record FilePreview(
    string FileName, long FileBytes, IReadOnlyList<string> Columns,
    IReadOnlyList<string> Rows, long TotalRows);

/// <summary>A file that arrived and was stored.</summary>
/// <param name="OriginalName">The name the client sent.</param>
/// <param name="Stored">Where it went, how big it was, and what it hashed to.</param>
internal sealed record ReceivedFile(string OriginalName, StoredFile Stored);

/// <summary>A page of import history.</summary>
/// <param name="Items">The jobs on this page.</param>
/// <param name="Total">Total jobs matching the filter.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Rows per page.</param>
public sealed record ImportPage(
    IReadOnlyList<ImportJobSummary> Items, long Total, int Page, int PageSize);

/// <summary>Returned when an upload is accepted and queued.</summary>
/// <param name="JobId">The queued job.</param>
/// <param name="FileId">The stored file.</param>
/// <param name="FileName">The name it arrived under.</param>
/// <param name="SizeBytes">Bytes written.</param>
/// <param name="Sha256">Content hash.</param>
public sealed record UploadAcceptedResponse(
    long JobId, long FileId, string FileName, long SizeBytes, string Sha256);

/// <summary>Returned when the uploaded content was already known.</summary>
/// <param name="FileId">The file already on record.</param>
/// <param name="ExistingJobId">Its most recent import job.</param>
/// <param name="OriginalFileName">The name it arrived under the first time.</param>
/// <param name="Sha256">Content hash.</param>
/// <param name="Message">What happened, in plain words.</param>
public sealed record DuplicateUploadResponse(
    long FileId, long? ExistingJobId, string OriginalFileName, string Sha256, string Message);

/// <summary>Returned when a file is queued again.</summary>
/// <param name="JobId">The new job.</param>
/// <param name="ReprocessOfJobId">The job it re-runs.</param>
public sealed record ReprocessResponse(long JobId, long ReprocessOfJobId);
