namespace Sqm.Application.Campaigns;

/// <summary>The configuration section <c>CampaignExport</c>: when a campaign list needs a second person.</summary>
/// <remarks>
/// The product owner's decision of 2026-10-21: a separate permission, a recorded reason, and a second
/// person's approval above a size threshold. The threshold is a setting, not a constant, because what
/// counts as "large" is the operator's to say; until it is said, every list needs approval.
/// </remarks>
public sealed class CampaignExportOptions
{
    /// <summary>The configuration section's name.</summary>
    public const string SectionName = "CampaignExport";

    /// <summary>
    /// A list of at most this many numbers is approved when it is asked for; a larger one waits for a
    /// second person. 0, the default, sends every list to a second person.
    /// </summary>
    public long ApprovalThreshold { get; set; }

    /// <summary>How long an approved list can be downloaded, in hours.</summary>
    public int ValidHours { get; set; } = 72;
}

/// <summary>What a campaign list is: one class of one planning run, narrowed as the page was. Never the numbers.</summary>
/// <param name="View">"sunset" or "esim".</param>
/// <param name="Class">The class listed.</param>
/// <param name="Reason">For the not-assessable class, the reason narrowed to, if any.</param>
/// <param name="Vendor">The vendor filter, if any.</param>
/// <param name="Model">The model filter, if any.</param>
/// <param name="DeviceType">The device type filter, if any.</param>
/// <param name="RunId">The planning run it was counted on - and will be read from.</param>
/// <param name="AsOfSequence">That run's delivery.</param>
/// <param name="AsOfDate">That delivery's business date.</param>
public sealed record CampaignListDefinition(
    string View, string Class, string? Reason, string? Vendor, string? Model, string? DeviceType,
    ulong RunId, int AsOfSequence, string? AsOfDate);

/// <summary>A request for a campaign list, as the operational store holds it.</summary>
public sealed record CampaignExportRecord(
    long Id,
    CampaignListDefinition Definition,
    long RowCount,
    string Reason,
    long RequestedBy,
    string RequestedByName,
    DateTimeOffset RequestedAt,
    bool NeedsApproval,
    string Status,
    long? DecidedBy,
    string? DecidedByName,
    DateTimeOffset? DecidedAt,
    string? DecisionNote,
    DateTimeOffset? ExpiresAt,
    int Downloads,
    DateTimeOffset? LastDownloadedAt);

/// <summary>What a decision on a request came to.</summary>
public enum CampaignDecisionOutcome
{
    /// <summary>Approved or rejected as asked.</summary>
    Decided,

    /// <summary>No such request.</summary>
    NotFound,

    /// <summary>It was already decided or withdrawn.</summary>
    NotPending,

    /// <summary>The decider is the requester: a second person is the point.</summary>
    OwnRequest,
}

/// <summary>Campaign list requests in the operational database (operational migration 021).</summary>
public interface ICampaignExportStore
{
    /// <summary>Records a request; approved at once when it does not need approval.</summary>
    Task<CampaignExportRecord> CreateAsync(
        CampaignListDefinition definition, long rowCount, string reason, long requestedBy, bool needsApproval,
        DateTimeOffset? expiresAt, CancellationToken ct);

    /// <summary>Requests, newest first: one person's, or everybody's when <paramref name="requestedBy"/> is null.</summary>
    Task<IReadOnlyList<CampaignExportRecord>> ListAsync(long? requestedBy, int limit, CancellationToken ct);

    /// <summary>One request, or null.</summary>
    Task<CampaignExportRecord?> GetAsync(long id, CancellationToken ct);

    /// <summary>Approves or rejects a pending request - never by its requester - in one statement.</summary>
    Task<(CampaignDecisionOutcome Outcome, CampaignExportRecord? Request)> DecideAsync(
        long id, long decidedBy, bool approve, string? note, DateTimeOffset? expiresAt, CancellationToken ct);

    /// <summary>Withdraws one's own pending or approved request.</summary>
    Task<bool> WithdrawAsync(long id, long requestedBy, CancellationToken ct);

    /// <summary>Counts a download.</summary>
    Task RecordDownloadAsync(long id, CancellationToken ct);
}
