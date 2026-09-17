namespace Sqm.Contracts.Devices;

/// <summary>One term of a candidate's quality score, and why it scored that.</summary>
/// <param name="Term">Which rule, e.g. <c>exactModelMatch</c>.</param>
/// <param name="Points">What it contributed.</param>
/// <param name="Detail">The measurement behind it, in words a reviewer can check.</param>
public sealed record ScoreTerm(string Term, int Points, string Detail);

/// <summary>
/// A proposed device image, waiting for a person to decide.
/// </summary>
/// <remarks>
/// Carries its own provenance and its own reasoning. A reviewer is being asked to replace
/// something that is on screen in front of customers, and "the pipeline liked it" is not a
/// reason - the source, the resolution and every scoring term are here so the decision can be
/// made on evidence rather than on trust in a number.
/// </remarks>
/// <param name="Id">Candidate identifier.</param>
/// <param name="ModelKey">The model it is proposed for.</param>
/// <param name="Brand">Display brand.</param>
/// <param name="MarketingName">Display model name.</param>
/// <param name="Status">needs_review, approved, rejected or failed.</param>
/// <param name="ContentType">Media type of the normalised image.</param>
/// <param name="ByteSize">Size of the normalised image.</param>
/// <param name="SourceType">manufacturer, encyclopedic, and so on.</param>
/// <param name="SourceDomain">The host it came from, which must be on the allowlist.</param>
/// <param name="SourceUrl">Where exactly, so the claim can be checked.</param>
/// <param name="OriginalWidth">Source width before normalisation.</param>
/// <param name="OriginalHeight">Source height before normalisation.</param>
/// <param name="QualityScore">The total.</param>
/// <param name="ScoreBreakdown">Every term that produced it.</param>
/// <param name="RejectionReason">Why a reviewer turned it down, when they did.</param>
/// <param name="CreatedAt">When it was proposed.</param>
/// <param name="CurrentStatus">
/// What is being served for this model right now: <c>missing</c>, <c>needs_review</c> or
/// <c>verified</c>. The reviewer needs it to know whether they are filling a gap or replacing
/// something somebody already approved.
/// </param>
public sealed record DeviceImageCandidate(
    long Id,
    string ModelKey,
    string Brand,
    string MarketingName,
    string Status,
    string ContentType,
    int ByteSize,
    string SourceType,
    string SourceDomain,
    string SourceUrl,
    int OriginalWidth,
    int OriginalHeight,
    int QualityScore,
    IReadOnlyList<ScoreTerm> ScoreBreakdown,
    string? RejectionReason,
    DateTimeOffset CreatedAt,
    string CurrentStatus);

/// <summary>A page of the review queue.</summary>
/// <param name="Total">Candidates matching the filter, before paging.</param>
/// <param name="Items">The page, highest score first.</param>
public sealed record DeviceImageCandidatePage(int Total, IReadOnlyList<DeviceImageCandidate> Items);

/// <summary>A reviewer turning a candidate down.</summary>
/// <param name="Reason">
/// Why, in the reviewer's words. Stored, and it is what stops the same image being proposed
/// again: the sourcing tool will not re-offer bytes it has already had an answer about.
/// </param>
public sealed record RejectCandidateRequest(string? Reason);
