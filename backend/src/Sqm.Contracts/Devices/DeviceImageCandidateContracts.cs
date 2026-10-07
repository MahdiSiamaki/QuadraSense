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
/// <param name="Bindings">
/// The model's active bindings when the candidate was staged - a snapshot for ordering the queue,
/// most-carried models first, not a live figure. Zero for a model not on the network.
/// </param>
/// <param name="Warnings">
/// What a reviewer should look at before approving: measurements of the pixels and of how well the
/// image's source matches the model. Warnings, never rejections - the product owner decided on
/// 2026-10-06 that a person makes that call.
/// </param>
/// <param name="Evidence">
/// How a package candidate was matched to the model. Null when there is none, which is every
/// web-sourced candidate.
/// </param>
/// <param name="Package">The image package it came from, or null when it was sourced from the web.</param>
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
    string CurrentStatus,
    long Bindings,
    IReadOnlyList<CandidateWarning> Warnings,
    CandidateEvidence? Evidence,
    string? Package);

/// <summary>Something a reviewer should check before approving a candidate.</summary>
/// <param name="Code">
/// Which rule: <c>low_resolution</c>, <c>touches_edges</c>, <c>small_subject</c>,
/// <c>background</c>, <c>aspect</c>, <c>partial_coverage</c>, <c>different_variant</c>,
/// <c>several_images</c>, <c>replaces_verified</c>, <c>replaces_unreviewed</c> or
/// <c>checksum_mismatch</c>.
/// </param>
/// <param name="Severity"><c>high</c> or <c>info</c>.</param>
/// <param name="Detail">The measurement behind it, in words a reviewer can check.</param>
public sealed record CandidateWarning(string Code, string Severity, string Detail);

/// <summary>
/// How a candidate from an image package was matched to the model it is proposed for.
/// </summary>
/// <remarks>
/// A package maps TACs to images, and the catalogue keys images by model, so the match can be
/// partial: "Galaxy A14" has 200 TACs and a package may map one of them, with no bindings, to a
/// "Galaxy A14 5G" image. Every member is optional - a value that is missing or cannot be read is
/// null, and the rest still reach the reviewer.
/// </remarks>
/// <param name="Package">The package folder name.</param>
/// <param name="ProductName">The product the package named for this image.</param>
/// <param name="ProductId">The package's own product identifier.</param>
/// <param name="MatchMethods">How the package matched its TACs to that product.</param>
/// <param name="MappedTacs">TACs of the model the package maps to this image.</param>
/// <param name="ModelTacs">TACs the model has in the active GSMA version.</param>
/// <param name="MappedBindings">Active bindings on the mapped TACs.</param>
/// <param name="ModelBindings">Active bindings on all of the model's TACs.</param>
/// <param name="SourceKind">manufacturer, retailer, museum_archive, and so on.</param>
/// <param name="SourcePage">The page the package took the image from.</param>
/// <param name="ImageUrl">The image's own address on that page.</param>
/// <param name="IdentitySource">Where the package's identification of the product came from.</param>
/// <param name="MatchScope">How specific the package says the image is to the product.</param>
/// <param name="QaStatus">The package's own quality-check verdict, when it gave one.</param>
/// <param name="PackageFile">The image's path inside the package.</param>
/// <param name="Upscale">The scale factor applied when the image was normalised.</param>
/// <param name="OriginalSha256Ok">Whether the file matched the checksum the package lists for it.</param>
public sealed record CandidateEvidence(
    string? Package,
    string? ProductName,
    string? ProductId,
    IReadOnlyList<string>? MatchMethods,
    long? MappedTacs,
    long? ModelTacs,
    long? MappedBindings,
    long? ModelBindings,
    string? SourceKind,
    string? SourcePage,
    string? ImageUrl,
    string? IdentitySource,
    string? MatchScope,
    string? QaStatus,
    string? PackageFile,
    double? Upscale,
    bool? OriginalSha256Ok);

/// <summary>A page of the review queue.</summary>
/// <param name="Total">Candidates matching the filter, before paging.</param>
/// <param name="Items">The page, in the order asked for.</param>
public sealed record DeviceImageCandidatePage(int Total, IReadOnlyList<DeviceImageCandidate> Items);

/// <summary>One value of a review-queue filter and how many candidates carry it.</summary>
/// <param name="Value">The value, as it would be passed back as a filter.</param>
/// <param name="Count">Candidates with it.</param>
public sealed record CandidateFacetValue(string Value, int Count);

/// <summary>
/// What the review queue holds in one status, so the filters can say how much each would show.
/// </summary>
/// <param name="Total">Candidates in the status.</param>
/// <param name="OnNetwork">Of those, candidates for a model with active bindings.</param>
/// <param name="WithWarnings">Of those, candidates with at least one warning.</param>
/// <param name="HighWarnings">Of those, candidates with at least one high-severity warning.</param>
/// <param name="Brands">Brands, most candidates first.</param>
/// <param name="SourceTypes">Source types, most candidates first.</param>
public sealed record DeviceImageCandidateFacets(
    int Total,
    int OnNetwork,
    int WithWarnings,
    int HighWarnings,
    IReadOnlyList<CandidateFacetValue> Brands,
    IReadOnlyList<CandidateFacetValue> SourceTypes);

/// <summary>Approving several candidates at once.</summary>
/// <param name="Ids">
/// The candidates, at most 100, no repeats, and no two for the same model - approving two images
/// for one model would leave whichever ran last on screen, which is a decision nobody made.
/// </param>
public sealed record ApproveCandidatesRequest(IReadOnlyList<long>? Ids);

/// <summary>What a batch approval did, candidate by candidate.</summary>
/// <param name="Approved">Promoted to the live image, in the order asked for.</param>
/// <param name="NotAwaitingReview">
/// Not promoted, because somebody had already decided them or they do not exist.
/// </param>
public sealed record ApproveCandidatesResult(
    IReadOnlyList<long> Approved,
    IReadOnlyList<long> NotAwaitingReview);

/// <summary>A reviewer turning a candidate down.</summary>
/// <param name="Reason">
/// Why, in the reviewer's words. Stored, and it is what stops the same image being proposed
/// again: the sourcing tool will not re-offer bytes it has already had an answer about.
/// </param>
public sealed record RejectCandidateRequest(string? Reason);

/// <summary>
/// An image the product is currently serving, for the review of what is already live.
/// </summary>
/// <remarks>
/// Distinct from <see cref="DeviceImageCandidate"/>, which is a proposal. This is the other half
/// of the same problem: 84 images were sourced automatically before there was any review step,
/// and flagging them was not the same as giving anybody a way to act on them.
/// </remarks>
/// <param name="ModelKey">The model it belongs to.</param>
/// <param name="Brand">Display brand.</param>
/// <param name="MarketingName">Display model name.</param>
/// <param name="Status"><c>verified</c> or <c>needs_review</c>.</param>
/// <param name="ContentType">Media type.</param>
/// <param name="ByteSize">Stored size.</param>
/// <param name="SourceType">Where it came from: manual, wikimedia, and so on.</param>
/// <param name="SourceDomain">The host, when it was sourced rather than uploaded.</param>
/// <param name="SourceNote">Free-text provenance, which for the old rows is the licence line.</param>
/// <param name="QualityScore">Null for anything predating the scoring pipeline.</param>
/// <param name="UploadedBy">Who put it there.</param>
/// <param name="VerifiedBy">Who vouched for it, when somebody has.</param>
/// <param name="UpdatedAt">When it last changed.</param>
public sealed record DeviceImageSummary(
    string ModelKey,
    string Brand,
    string MarketingName,
    string Status,
    string ContentType,
    int ByteSize,
    string SourceType,
    string SourceDomain,
    string SourceNote,
    int? QualityScore,
    string? UploadedBy,
    string? VerifiedBy,
    DateTimeOffset UpdatedAt);

/// <summary>A page of the live image catalogue.</summary>
/// <param name="Total">Images matching the filter, before paging.</param>
/// <param name="Items">The page.</param>
public sealed record DeviceImagePage(int Total, IReadOnlyList<DeviceImageSummary> Items);
