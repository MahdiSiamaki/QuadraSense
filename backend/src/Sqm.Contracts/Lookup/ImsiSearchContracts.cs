namespace Sqm.Contracts.Lookup;

/// <summary>What the caller asked for when searching by IMSI.</summary>
/// <remarks>
/// <para>
/// The term travels in a request body, never a query string. That is the same rule the subscriber
/// lookup follows and for the same reason: a GET would put a real SIM identity into web-server
/// access logs, browser history and any <c>Referer</c> the page later emits. Masking in the UI
/// would not help - the identifier would already be in three logs before it was drawn.
/// </para>
/// <para>
/// The filters are the dimensions the data actually has. There is no free-text filter, because
/// every value here is matched against a bounded set the client got from the API.
/// </para>
/// <para>
/// <c>Imsi</c> is a complete IMSI or a prefix of at least ten digits. <c>ActiveOnly</c> defaults to
/// false, because "this SIM used to be in that handset" is usually the question. <c>From</c> and
/// <c>To</c> bound the last-change date inclusively. <c>Page</c> is 1-based and <c>PageSize</c> is
/// capped server-side.
/// </para>
/// </remarks>
public sealed record ImsiSearchRequest(
    string Imsi,
    string? DeviceType = null,
    string? Manufacturer = null,
    bool? ActiveOnly = null,
    DateOnly? From = null,
    DateOnly? To = null,
    int Page = 1,
    int PageSize = 50);

/// <summary>One binding matching an IMSI search.</summary>
/// <remarks>
/// Identifiers arrive already masked when the caller lacks permission to see them in full. There
/// is no second field carrying the raw value: what the server does not want the caller to have,
/// the server does not send.
/// </remarks>
public sealed record ImsiMatch(
    string Imsi,
    string Msisdn,
    string Imei,
    string? Tac,
    string? Manufacturer,
    string? MarketingName,
    string? DeviceType,
    string? OperatingSystem,
    bool IsActive,
    DateOnly? LastChangeDate);

/// <summary>Facts about one IMSI, shown when the search term was a complete one.</summary>
/// <remarks>
/// <c>DistinctSubscribers</c> above one is a number change; <c>DistinctHandsets</c> above one is
/// the SIM-swap signal. <c>EverTouchedByDailyFile</c> is false when the state comes from the
/// initial dump alone and has never been confirmed or contradicted.
/// <para>
/// <c>FirstSeen</c> and <c>LastSeen</c> come from the binding history once it is complete (analytics
/// migration 022): the SIM's first binding, which for one from the initial dump is the dump window -
/// <c>FirstSeenIsDumpWindow</c> says so - and the last day the feed changed any of its bindings.
/// Before that they were the earliest and latest last-change dates, which is later than the SIM
/// first appeared and ignores the dump (corrected with the product owner, 2026-10-01).
/// </para>
/// </remarks>
public sealed record ImsiSummary(
    int DistinctSubscribers,
    int DistinctHandsets,
    int ActiveBindings,
    DateOnly? FirstSeen,
    DateOnly? LastSeen,
    bool EverTouchedByDailyFile,
    bool FirstSeenIsDumpWindow = false);

/// <summary>The result of an IMSI search.</summary>
/// <remarks>
/// <c>Term</c> is what the server understood after separators were stripped. <c>WellFormed</c> is
/// false for the 16- and 17-digit anomalies, which stay searchable so they can be investigated.
/// <c>Total</c> counts matches before paging, so the client can render real paging.
/// <c>Summary</c> is present only for an exact term - aggregating a prefix would fold thousands of
/// unrelated SIMs into one misleading figure.
/// </remarks>
public sealed record ImsiSearchResponse(
    string Term,
    bool IsExact,
    bool WellFormed,
    int Total,
    int Page,
    int PageSize,
    IdentifierVisibility Identifiers,
    IReadOnlyList<ImsiMatch> Items,
    ImsiSummary? Summary,
    SearchTiming Timing);

/// <summary>Whether identifiers in a response are complete or redacted.</summary>
public enum IdentifierVisibility
{
    /// <summary>Redacted server-side; the raw value was never sent.</summary>
    Masked,

    /// <summary>Complete, because the caller holds the permission for it.</summary>
    Full,
}

/// <summary>What a search cost.</summary>
/// <remarks>
/// Returned and shown in the UI. On a dataset this size the difference between an indexed lookup
/// and a scan is three orders of magnitude, and a search that suddenly takes four seconds is a
/// signal worth surfacing rather than a mystery for whoever is watching the dashboard.
/// <para>
/// <c>ElapsedMs</c> is wall time as the server measured it; <c>RowsExamined</c> is how many rows
/// the engine read, which is the number to compare against the table size.
/// </para>
/// </remarks>
public sealed record SearchTiming(long ElapsedMs, long RowsExamined);

/// <summary>One dated change to a binding involving this IMSI.</summary>
/// <remarks>
/// <c>Date</c> is the business date of the daily file that carried it and <c>Sequence</c> orders
/// events within that day. <c>Added</c> is true for an <c>add</c>, false for a <c>remove</c>.
/// </remarks>
public sealed record ImsiHistoryEvent(
    DateOnly Date,
    int Sequence,
    string Msisdn,
    string Imei,
    string? Manufacturer,
    string? MarketingName,
    bool Added);

/// <summary>An IMSI's history over a date range.</summary>
/// <remarks>
/// <c>Truncated</c> is true when more events exist than were returned. A SIM that changes handset
/// daily for a year has hundreds, and the page says so rather than showing the first hundred as if
/// they were all of them.
/// </remarks>
public sealed record ImsiHistoryResponse(
    string Imsi,
    DateOnly? From,
    DateOnly? To,
    IReadOnlyList<ImsiHistoryEvent> Events,
    bool Truncated,
    SearchTiming Timing);
