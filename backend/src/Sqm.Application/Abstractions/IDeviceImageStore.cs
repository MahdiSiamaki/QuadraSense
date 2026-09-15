namespace Sqm.Application.Abstractions;

/// <summary>A curated device photograph, with its bytes.</summary>
/// <param name="Tac">The device model it belongs to.</param>
/// <param name="ContentType">One of <c>image/png</c>, <c>image/jpeg</c>, <c>image/webp</c>.</param>
/// <param name="Bytes">The image itself.</param>
/// <param name="ETag">A quoted entity tag derived from the content hash.</param>
/// <param name="UpdatedAt">When it was last replaced.</param>
public sealed record DeviceImage(
    string Tac, string ContentType, byte[] Bytes, string ETag, DateTimeOffset UpdatedAt);

/// <summary>What is known about a device photograph without fetching it.</summary>
/// <param name="Tac">The device model it belongs to.</param>
/// <param name="ContentType">Its media type.</param>
/// <param name="ByteSize">How large it is.</param>
/// <param name="SourceNote">Where the uploader said it came from.</param>
/// <param name="UploadedBy">Username of whoever last replaced it.</param>
/// <param name="UpdatedAt">When that was.</param>
public sealed record DeviceImageInfo(
    string Tac,
    string ContentType,
    int ByteSize,
    string SourceNote,
    string? UploadedBy,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Curated photographs of device models.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where these come from, because the question has a real answer.</b> The GSMA TAC database has
/// 26 columns and not one of them is an image, a URL, or a reference to one - there is no imagery
/// to import. The deployment is internal-network-only, so a device-catalogue CDN is unreachable
/// rather than merely undesirable. So images are uploaded here by administrators holding
/// <c>device.image.manage</c>, and a device with no photograph draws a typed placeholder.
/// </para>
/// <para>
/// The bytes live in PostgreSQL rather than behind <see cref="Sqm.Application.DataImport"/>'s file
/// store. That store is right for its job - multi-gigabyte source files - and wrong for this one:
/// a photograph is tens of kilobytes, there will be hundreds rather than hundreds of thousands,
/// and holding them in the row keeps bytes and metadata atomic, with no orphaned files and no rows
/// pointing at files a restore did not bring back. The 512 KB ceiling in the schema is what keeps
/// that trade honest.
/// </para>
/// </remarks>
public interface IDeviceImageStore
{
    /// <summary>Fetches one image, or null when the model has none.</summary>
    Task<DeviceImage?> GetAsync(string tac, CancellationToken ct);

    /// <summary>Metadata for one image, without transferring it.</summary>
    Task<DeviceImageInfo?> GetInfoAsync(string tac, CancellationToken ct);

    /// <summary>
    /// Which of these models have a photograph.
    /// </summary>
    /// <remarks>
    /// One indexed read for a whole page of the catalogue, rather than forty requests that each
    /// discover an absence. The list needs to know only whether to draw a picture or a placeholder.
    /// </remarks>
    Task<IReadOnlySet<string>> GetPresentAsync(IReadOnlyList<string> tacs, CancellationToken ct);

    /// <summary>Stores or replaces one model's photograph.</summary>
    /// <param name="tac">The device model.</param>
    /// <param name="contentType">Media type, already validated against the allowed set.</param>
    /// <param name="bytes">The image.</param>
    /// <param name="sourceNote">Where it came from, free text, for provenance.</param>
    /// <param name="userId">Who uploaded it.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveAsync(
        string tac, string contentType, byte[] bytes, string sourceNote, long userId,
        CancellationToken ct);

    /// <summary>Removes one model's photograph. True when there was one to remove.</summary>
    Task<bool> DeleteAsync(string tac, CancellationToken ct);
}
