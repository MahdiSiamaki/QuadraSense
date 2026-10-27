namespace Sqm.Contracts.Campaigns;

/// <summary>A request for a campaign list: one class of one planning run, and why it is wanted.</summary>
/// <param name="View">"sunset" or "esim".</param>
/// <param name="Class">The class to list.</param>
/// <param name="Reason">For the not-assessable class, the reason to narrow to.</param>
/// <param name="Vendor">The vendor filter, if any.</param>
/// <param name="Model">The model filter, if any.</param>
/// <param name="DeviceType">The device type filter, if any.</param>
/// <param name="RunId">The planning run; the newest when null.</param>
/// <param name="Purpose">Why the list is wanted - recorded, shown to the approver, and audited. 10 to 1,000 characters.</param>
public sealed record CampaignExportCreate(
    string? View, string? Class, string? Reason, string? Vendor, string? Model, string? DeviceType, ulong? RunId, string? Purpose);

/// <summary>A decision on someone else's request.</summary>
public sealed record CampaignExportDecision(string? Note);

/// <summary>A campaign list request as the page shows it.</summary>
/// <param name="Id">The request.</param>
/// <param name="View">The planning view.</param>
/// <param name="Class">The class listed.</param>
/// <param name="Reason">The not-assessable reason narrowed to, if any.</param>
/// <param name="Filter">The vendor, model and device type filters, as set.</param>
/// <param name="AsOfSequence">The delivery the list is of.</param>
/// <param name="AsOfDate">That delivery's business date.</param>
/// <param name="RowCount">Numbers counted when it was asked for - what the download holds.</param>
/// <param name="Purpose">Why it was asked for.</param>
/// <param name="RequestedBy">Who asked.</param>
/// <param name="RequestedAt">When, ISO UTC.</param>
/// <param name="NeedsApproval">Whether it was above the threshold, so a second person decides.</param>
/// <param name="Status">pending, approved, rejected or withdrawn.</param>
/// <param name="DecidedBy">Who decided, if anyone.</param>
/// <param name="DecidedAt">When.</param>
/// <param name="DecisionNote">What they said.</param>
/// <param name="ExpiresAt">Until when an approved list can be downloaded.</param>
/// <param name="Downloads">How many times it has been.</param>
/// <param name="CanDownload">Whether this caller may download it now.</param>
/// <param name="CanDecide">Whether this caller may approve or reject it.</param>
public sealed record CampaignExportView(
    long Id, string View, string Class, string? Reason, IReadOnlyDictionary<string, string?> Filter,
    int AsOfSequence, string? AsOfDate, long RowCount, string Purpose, string RequestedBy, string RequestedAt,
    bool NeedsApproval, string Status, string? DecidedBy, string? DecidedAt, string? DecisionNote, string? ExpiresAt,
    int Downloads, bool CanDownload, bool CanDecide);

/// <summary>Requests visible to the caller, and the threshold in force.</summary>
/// <param name="Requests">Newest first: one's own, or everybody's for an approver.</param>
/// <param name="ApprovalThreshold">Lists larger than this need a second person; 0 means every list does.</param>
/// <param name="ValidHours">How long an approved list can be downloaded.</param>
public sealed record CampaignExportList(IReadOnlyList<CampaignExportView> Requests, long ApprovalThreshold, int ValidHours);
