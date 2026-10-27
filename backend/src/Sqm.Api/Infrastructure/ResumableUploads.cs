using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Sqm.Api.Auth;
using Sqm.Api.Endpoints;
using Sqm.Application.Identity;
using Sqm.Infrastructure.DataImport;
using tusdotnet.Models;
using tusdotnet.Models.Configuration;
using tusdotnet.Models.Expiration;
using tusdotnet.Stores;

namespace Sqm.Api.Infrastructure;

/// <summary>
/// Uploads that survive a broken connection: the tus protocol (ADR-018).
/// </summary>
/// <remarks>
/// <para>
/// A daily file was one ~370 MB request. Over the department's VPN - 80-330 KB/s, a drop every
/// ~15 minutes - two of two browser uploads broke at 12.6 and 16.5 minutes and had to start again
/// from nothing. With tus the browser creates an upload, sends the bytes, and after a break asks how
/// many arrived and sends the rest.
/// </para>
/// <para>
/// The pieces gather in <c>_resumable</c> under the storage root - the same volume as the store, so
/// the finished file is moved into it, not copied (<see cref="Application.DataImport.IImportFileStore.AdoptAsync"/>).
/// tus only carries the bytes; who may upload is checked when an upload is created, and what
/// becomes of the file is decided by the same intake as any other upload, when the browser asks
/// to complete it (<see cref="ResumableUploadEndpoints"/>).
/// </para>
/// <para>
/// Each upload belongs to the user who created it, recorded beside it: an upload's id is
/// unguessable, but an id is not a permission, and nobody else can add to, read or finish it.
/// One left alone for a day is removed.
/// </para>
/// </remarks>
public sealed partial class ResumableUploads
{
    /// <summary>Where the tus protocol is served.</summary>
    public const string RoutePattern = "/api/v1/imports/uploads";

    /// <summary>An upload nobody has added to for this long is removed, with its pieces.</summary>
    public static readonly TimeSpan Abandoned = TimeSpan.FromHours(24);

    private const string OwnerSuffix = ".owner";

    private readonly string _directory;
    private readonly ILogger<ResumableUploads> _logger;

    [LoggerMessage(EventId = 6100, Level = LogLevel.Information,
        Message = "Removed {Count} abandoned resumable upload(s)")]
    private partial void LogRemoved(int count);

    public ResumableUploads(IOptions<ImportStorageOptions> options, ILogger<ResumableUploads> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger;

        // Under the storage root, so a finished upload is renamed into the store, not copied. An
        // underscore, because no source code has one and the store lays files out by source.
        _directory = Path.Combine(Path.GetFullPath(options.Value.RootPath), "_resumable");
        Directory.CreateDirectory(_directory);

        Store = new TusDiskStore(_directory);
        Configuration = new DefaultTusConfiguration
        {
            Store = Store,
            MaxAllowedUploadSizeInBytesLong = options.Value.MaxFileBytes,
            Expiration = new SlidingExpiration(Abandoned),
            Events = new Events
            {
                OnAuthorizeAsync = AuthorizeAsync,
                OnBeforeCreateAsync = BeforeCreateAsync,
                OnCreateCompleteAsync = RecordOwnerAsync,
                OnDeleteCompleteAsync = ForgetOwnerAsync,
            },
        };
    }

    /// <summary>Where the pieces are kept.</summary>
    public TusDiskStore Store { get; }

    /// <summary>The tus endpoint's configuration.</summary>
    public DefaultTusConfiguration Configuration { get; }

    /// <summary>
    /// Whether this is the shape of id the store hands out - 32 hex digits - so an id from a request
    /// can be put in a path without being able to name anything else.
    /// </summary>
    public static bool IsUploadId(string uploadId) => UploadIdPattern.IsMatch(uploadId);

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex UploadIdPattern { get; }

    /// <summary>The assembled file of an upload.</summary>
    public string DataPath(string uploadId) =>
        IsUploadId(uploadId)
            ? Path.Combine(_directory, uploadId)
            : throw new ArgumentException("not an upload id", nameof(uploadId));

    /// <summary>Whether the upload exists and was created by this user.</summary>
    public bool IsOwnedBy(string uploadId, long userId)
    {
        if (!IsUploadId(uploadId))
        {
            return false;
        }

        var owner = Path.Combine(_directory, uploadId + OwnerSuffix);
        return File.Exists(owner)
            && long.TryParse(File.ReadAllText(owner), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            && id == userId;
    }

    /// <summary>The upload's name and source, as the browser declared them when creating it.</summary>
    public async Task<(string Source, string FileName)?> GetDeclaredAsync(string uploadId, CancellationToken ct)
    {
        var metadata = Metadata.Parse(await Store.GetUploadMetadataAsync(uploadId, ct).ConfigureAwait(false));
        return metadata.TryGetValue("source", out var source) && metadata.TryGetValue("filename", out var name)
            ? (source.GetString(Encoding.UTF8).ToUpperInvariant(), name.GetString(Encoding.UTF8))
            : null;
    }

    /// <summary>Removes what is left of an upload: its pieces, its records, its owner.</summary>
    public async Task ForgetAsync(string uploadId, CancellationToken ct)
    {
        await Store.DeleteFileAsync(uploadId, ct).ConfigureAwait(false);
        File.Delete(Path.Combine(_directory, uploadId + OwnerSuffix));
    }

    /// <summary>
    /// Removes uploads left alone for <see cref="Abandoned"/>: unfinished ones, and finished ones the
    /// browser never asked to complete.
    /// </summary>
    public async Task<int> RemoveAbandonedAsync(CancellationToken ct)
    {
        var removed = await Store.RemoveExpiredFilesAsync(ct).ConfigureAwait(false);

        foreach (var owner in Directory.EnumerateFiles(_directory, "*" + OwnerSuffix))
        {
            var uploadId = Path.GetFileNameWithoutExtension(owner);
            var data = Path.Combine(_directory, uploadId);
            var lastTouched = File.Exists(data) ? File.GetLastWriteTimeUtc(data) : File.GetLastWriteTimeUtc(owner);

            if (DateTime.UtcNow - lastTouched > Abandoned)
            {
                await ForgetAsync(uploadId, ct).ConfigureAwait(false);
                removed++;
            }
        }

        if (removed > 0)
        {
            LogRemoved(removed);
        }

        return removed;
    }

    private Task AuthorizeAsync(AuthorizeContext context)
    {
        var user = CurrentUser.Session(context.HttpContext)?.User;
        if (user is null)
        {
            context.FailRequest(HttpStatusCode.Unauthorized);
            return Task.CompletedTask;
        }

        switch (context.Intent)
        {
            case IntentType.CreateFile:
            case IntentType.GetOptions:
                // Who may create one is decided per source, in BeforeCreateAsync.
                break;
            case IntentType.ConcatenateFiles:
                context.FailRequest(HttpStatusCode.Forbidden, "Concatenation is not supported.");
                break;
            default:
                // Writing, asking for the offset, terminating: the creator's own upload only. Not
                // found rather than forbidden, so an id says nothing about anyone else's uploads.
                if (context.FileId is null || !IsOwnedBy(context.FileId, user.UserId))
                {
                    context.FailRequest(HttpStatusCode.NotFound);
                }

                break;
        }

        return Task.CompletedTask;
    }

    private async Task BeforeCreateAsync(BeforeCreateContext context)
    {
        if (context.UploadLengthIsDeferred)
        {
            context.FailRequest("Send Upload-Length: the size is checked before any byte is taken.");
            return;
        }

        if (!context.Metadata.TryGetValue("source", out var source)
            || !context.Metadata.TryGetValue("filename", out var name)
            || source.HasEmptyValue || name.HasEmptyValue)
        {
            context.FailRequest("Upload-Metadata must name the source and the filename.");
            return;
        }

        // The same decision, and the same audit entry, as the single-request upload and its
        // preflight - made before a byte is accepted.
        var http = context.HttpContext;
        var refusal = await ImportEndpoints.RefuseUploadAsync(
                source.GetString(Encoding.UTF8),
                http,
                http.RequestServices.GetRequiredService<IAuditLog>(),
                context.CancellationToken)
            .ConfigureAwait(false);

        if (refusal is not null)
        {
            context.FailRequest(HttpStatusCode.Forbidden, "Not permitted to upload files of this source.");
        }
    }

    private async Task RecordOwnerAsync(CreateCompleteContext context)
    {
        var user = CurrentUser.Session(context.HttpContext)!.User;
        await File.WriteAllTextAsync(
                Path.Combine(_directory, context.FileId + OwnerSuffix),
                user.UserId.ToString(CultureInfo.InvariantCulture),
                context.CancellationToken)
            .ConfigureAwait(false);
    }

    // A cancel from the browser is tus's own DELETE, which removes only tus's files: without this the
    // owner stayed until the day-old sweep (on the VM, 2026-10-27).
    private Task ForgetOwnerAsync(DeleteCompleteContext context)
    {
        File.Delete(Path.Combine(_directory, context.FileId + OwnerSuffix));
        return Task.CompletedTask;
    }
}

/// <summary>Removes abandoned resumable uploads once an hour.</summary>
public sealed class ResumableUploadCleanup(ResumableUploads uploads) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));

        do
        {
            try
            {
                await uploads.RemoveAbandonedAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // A file in use is tried again in an hour; litter is not worth stopping the host for.
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
