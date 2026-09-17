using Sqm.Contracts.Devices;

namespace Sqm.Application.Abstractions;

/// <summary>
/// Device images proposed by the sourcing pipeline, awaiting a person's decision.
/// </summary>
/// <remarks>
/// <para>
/// The staging area that makes automatic sourcing safe. The pipeline writes here and nowhere
/// else; the live table is reached only through <see cref="ApproveAsync"/>, which a reviewer
/// holding <c>device.image.manage</c> triggers. Until then the image already on screen stays on
/// screen, which is the property the whole design turns on.
/// </para>
/// <para>
/// A rejected candidate is kept rather than deleted. Its <c>(model_key, sha256)</c> is what stops
/// the sourcing tool proposing the same bytes again, so deleting it would guarantee the reviewer
/// saw it again on the next run.
/// </para>
/// </remarks>
public interface IDeviceImageCandidateStore
{
    /// <summary>A page of the queue, highest score first. Status <c>all</c> returns every state.</summary>
    Task<DeviceImageCandidatePage> ListAsync(string status, int limit, int offset, CancellationToken ct);

    /// <summary>The candidate's own bytes, so a reviewer can look at it before deciding.</summary>
    Task<DeviceImage?> GetImageAsync(long id, CancellationToken ct);

    /// <summary>
    /// Promotes a candidate to the live image, in one transaction, marked verified.
    /// </summary>
    /// <returns>False when it was not awaiting review - already decided, or gone.</returns>
    Task<bool> ApproveAsync(long id, long userId, CancellationToken ct);

    /// <summary>Turns a candidate down, keeping it so it cannot be proposed again.</summary>
    /// <returns>False when it was not awaiting review.</returns>
    Task<bool> RejectAsync(long id, long userId, string? reason, CancellationToken ct);
}
