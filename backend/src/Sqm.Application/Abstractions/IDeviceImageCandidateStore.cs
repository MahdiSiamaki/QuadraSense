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
    /// <summary>A page of the queue, filtered and ordered as the query says.</summary>
    Task<DeviceImageCandidatePage> ListAsync(DeviceImageCandidateQuery query, CancellationToken ct);

    /// <summary>
    /// How many candidates in a status each filter value would show. Status <c>all</c> counts
    /// every state.
    /// </summary>
    Task<DeviceImageCandidateFacets> GetFacetsAsync(string status, CancellationToken ct);

    /// <summary>
    /// The model each of these candidates is proposed for, whatever its status. An id that does
    /// not exist is simply absent from the answer.
    /// </summary>
    Task<IReadOnlyList<CandidateModel>> GetModelsAsync(IReadOnlyCollection<long> ids, CancellationToken ct);

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

/// <summary>
/// Which candidates a reviewer wants to see, and in what order.
/// </summary>
/// <remarks>
/// Every value has already been validated against its closed set by the endpoint, so the store
/// can treat an unknown value as a programming error rather than as user input.
/// </remarks>
/// <param name="Status"><c>needs_review</c>, <c>approved</c>, <c>rejected</c>, <c>failed</c> or <c>all</c>.</param>
/// <param name="Brand">Exact brand, compared without regard to case; null for every brand.</param>
/// <param name="SourceType">Exact source type; null for every source.</param>
/// <param name="OnNetwork">
/// True for models with active bindings, false for models without, null for both.
/// </param>
/// <param name="Warnings">
/// <c>any</c>; <c>with</c> at least one warning; <c>without</c> any; or at least one <c>high</c>.
/// </param>
/// <param name="Sort">
/// <c>bindings</c> (most-carried models first, then score) or <c>score</c> (highest score first,
/// oldest first among equals).
/// </param>
/// <param name="Limit">Page size.</param>
/// <param name="Offset">Rows to skip.</param>
public sealed record DeviceImageCandidateQuery(
    string Status,
    string? Brand,
    string? SourceType,
    bool? OnNetwork,
    string Warnings,
    string Sort,
    int Limit,
    int Offset);

/// <summary>The model one candidate is proposed for.</summary>
/// <param name="Id">The candidate.</param>
/// <param name="ModelKey">The model's key.</param>
/// <param name="Brand">Display brand.</param>
/// <param name="MarketingName">Display model name.</param>
public sealed record CandidateModel(long Id, string ModelKey, string Brand, string MarketingName);
