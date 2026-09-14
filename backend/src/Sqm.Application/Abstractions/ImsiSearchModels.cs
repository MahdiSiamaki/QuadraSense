using Sqm.Domain.Identifiers;

namespace Sqm.Application.Abstractions;

/// <summary>
/// An IMSI search as the store receives it: a parsed range plus the dimensions to filter on.
/// </summary>
/// <remarks>
/// <para>
/// The term arrives already parsed. Passing a string down to the store would put the decision
/// about what counts as a usable prefix inside the SQL layer, where the measurement that justifies
/// it - <c>43211</c> on every row - is invisible.
/// </para>
/// <para>
/// <c>Term</c> carries the numeric range a prefix expands to. <c>From</c> and <c>To</c> bound the
/// last-change date inclusively, and <c>Limit</c> is already clamped by the caller.
/// </para>
/// </remarks>
public sealed record ImsiSearchCriteria(
    ImsiQuery Term,
    string? DeviceType,
    string? Manufacturer,
    bool ActiveOnly,
    DateOnly? From,
    DateOnly? To,
    int Offset,
    int Limit);

/// <summary>One binding, as the store returns it - identifiers still raw.</summary>
/// <remarks>
/// Masking happens at the API boundary, not here. The store's job is to answer the question; the
/// decision about who may see the answer belongs where the caller's permissions are known.
/// </remarks>
public sealed record ImsiBindingRow(
    ulong Imsi,
    ulong Msisdn,
    string Imei,
    string? Tac,
    string? Manufacturer,
    string? MarketingName,
    string? DeviceType,
    string? OperatingSystem,
    bool IsActive,
    DateOnly? LastChangeDate);

/// <summary>What a search found, and what it cost.</summary>
/// <remarks>
/// <c>Total</c> counts matching rows before paging. <c>RowsExamined</c> is what the engine read,
/// for comparison against the table size - the number that distinguishes an indexed lookup from a
/// scan far more reliably than elapsed time, which also moves with cache warmth.
/// </remarks>
public sealed record ImsiSearchOutcome(
    IReadOnlyList<ImsiBindingRow> Rows,
    int Total,
    ImsiFacts? Facts,
    long ElapsedMs,
    long RowsExamined);

/// <summary>Aggregate facts about one IMSI. Computed only for an exact term.</summary>
/// <remarks>
/// <c>EverTouchedByDailyFile</c> is false when every binding for this IMSI is still at sequence 0 -
/// present because the initial dump listed it, never confirmed or contradicted since. Roughly half
/// the active population is in that state, and a page that does not say so is overstating what it
/// knows.
/// </remarks>
public sealed record ImsiFacts(
    int DistinctSubscribers,
    int DistinctHandsets,
    int ActiveBindings,
    DateOnly? FirstSeen,
    DateOnly? LastSeen,
    bool EverTouchedByDailyFile);

/// <summary>One dated change involving an IMSI.</summary>
public sealed record ImsiHistoryRow(
    DateOnly Date,
    int Sequence,
    ulong Msisdn,
    string Imei,
    string? Manufacturer,
    string? MarketingName,
    bool Added);

/// <summary>An IMSI's history, and what reading it cost.</summary>
/// <remarks><c>Truncated</c> means more events exist than the limit allowed.</remarks>
public sealed record ImsiHistoryOutcome(
    IReadOnlyList<ImsiHistoryRow> Events,
    bool Truncated,
    long ElapsedMs,
    long RowsExamined);
