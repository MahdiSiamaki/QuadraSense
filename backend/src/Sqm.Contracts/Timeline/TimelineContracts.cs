namespace Sqm.Contracts.Timeline;

/// <summary>Whose timeline: a number, a SIM or a handset. In the body, never the URL.</summary>
/// <param name="Identifier">A phone number (10 digits), a SIM (15) or a handset (14). Length decides which.</param>
public sealed record TimelineRequest(string Identifier);

/// <summary>A stretch of time over which the feed held a binding, or any binding of a group, as active.</summary>
/// <param name="Start">yyyy-MM-dd: the day it was added, or the initial dump window's first day.</param>
/// <param name="StartsInDump">The start is the initial dump's window, not a known day.</param>
/// <param name="End">yyyy-MM-dd: the day the feed removed it; null while it has not.</param>
public sealed record TimelinePeriodInfo(string Start, bool StartsInDump, string? End);

/// <summary>One dated change from a daily file.</summary>
/// <param name="Date">yyyy-MM-dd.</param>
/// <param name="Change">"add" or "remove".</param>
/// <param name="Effect">"applied", "redundant" (an add of something already held) or "orphan" (a remove of something not held).</param>
public sealed record TimelineEventInfo(string Date, string Change, string Effect);

/// <summary>One binding - a number, a SIM and a handset together - over time.</summary>
/// <param name="Msisdn">Null when withheld; masked without identifier.reveal.</param>
/// <param name="Imsi">Null when withheld; masked without identifier.reveal.</param>
/// <param name="Imei">Null when withheld; masked without identifier.reveal.</param>
/// <param name="Tac">The handset's model allocation.</param>
/// <param name="Brand">GSMA brand; null when GSMA does not know the TAC.</param>
/// <param name="Model">GSMA marketing name; null when GSMA does not know the TAC.</param>
/// <param name="Active">Not yet removed by the feed, from current state. Not proof the SIM is in the handset today.</param>
/// <param name="InDump">The initial dump listed it.</param>
/// <param name="FirstSeen">yyyy-MM-dd: its first event, or the dump window's first day.</param>
/// <param name="FirstSeenIsDumpWindow">FirstSeen stands for the dump window.</param>
/// <param name="LastChange">yyyy-MM-dd of its last event; null when no daily file mentioned it.</param>
/// <param name="Periods">When the feed held it active.</param>
/// <param name="Events">Its events, oldest first.</param>
public sealed record TimelineBindingInfo(
    string? Msisdn, string? Imsi, string? Imei, string? Tac, string? Brand, string? Model,
    bool Active, bool InDump, string? FirstSeen, bool FirstSeenIsDumpWindow, string? LastChange,
    IReadOnlyList<TimelinePeriodInfo> Periods, IReadOnlyList<TimelineEventInfo> Events);

/// <summary>Every binding sharing one related identifier - one SIM of a number, say - combined.</summary>
/// <param name="Kind">msisdn, imsi or imei: what the group is of.</param>
/// <param name="Key">The identifier; masked without identifier.reveal.</param>
/// <param name="Drillable">The key is complete and can be opened.</param>
/// <param name="Tac">For a handset group: its model allocation.</param>
/// <param name="Brand">For a handset group: GSMA brand.</param>
/// <param name="Model">For a handset group: GSMA marketing name.</param>
/// <param name="Bindings">How many bindings the group combines.</param>
/// <param name="Active">Any of them not yet removed.</param>
/// <param name="FirstSeen">yyyy-MM-dd, the earliest of its bindings.</param>
/// <param name="FirstSeenIsDumpWindow">FirstSeen stands for the dump window.</param>
/// <param name="LastChange">yyyy-MM-dd, the latest event of any of its bindings.</param>
/// <param name="Periods">When any of its bindings was held: overlapping and same-day periods joined.</param>
public sealed record TimelineGroupInfo(
    string Kind, string Key, bool Drillable, string? Tac, string? Brand, string? Model,
    int Bindings, bool Active, string? FirstSeen, bool FirstSeenIsDumpWindow, string? LastChange,
    IReadOnlyList<TimelinePeriodInfo> Periods);

/// <summary>What the whole timeline says.</summary>
/// <param name="FirstSeen">yyyy-MM-dd, the earliest binding.</param>
/// <param name="FirstSeenIsDumpWindow">FirstSeen stands for the dump window.</param>
/// <param name="LastChange">yyyy-MM-dd, the latest event; null when no daily file mentioned it.</param>
/// <param name="Bindings">Bindings in the timeline.</param>
/// <param name="ActiveBindings">Of those, not yet removed.</param>
/// <param name="Numbers">Different numbers.</param>
/// <param name="Sims">Different SIMs.</param>
/// <param name="Handsets">Different IMEIs - not phones: a dual-SIM phone has two.</param>
/// <param name="RedundantAdds">Adds of bindings already held; mostly the dump being a month, not a moment.</param>
/// <param name="OrphanRemoves">Removes of bindings not held.</param>
/// <param name="StateDisagreements">Bindings whose events end in a different state from current state. Zero
/// unless a day is part way through being imported.</param>
public sealed record TimelineSummaryInfo(
    string? FirstSeen, bool FirstSeenIsDumpWindow, string? LastChange,
    int Bindings, int ActiveBindings, int Numbers, int Sims, int Handsets,
    int RedundantAdds, int OrphanRemoves, int StateDisagreements);

/// <summary>A number's, SIM's or handset's history, binding by binding and grouped by what it was bound to.</summary>
/// <param name="Kind">msisdn, imsi or imei.</param>
/// <param name="Identifier">As asked, normalised.</param>
/// <param name="Tac">For a handset: its model allocation.</param>
/// <param name="Brand">For a handset: GSMA brand.</param>
/// <param name="Model">For a handset: GSMA marketing name.</param>
/// <param name="Summary">The figures for the whole timeline.</param>
/// <param name="Groups">The bindings combined by each related kind the caller may see.</param>
/// <param name="Bindings">Every binding, most recently changed first.</param>
/// <param name="Truncated">More bindings exist than <paramref name="MaxBindings"/>; the most recent are shown.</param>
/// <param name="MaxBindings">The most a timeline returns.</param>
/// <param name="Withheld">Related kinds left out because the caller may not look them up.</param>
/// <param name="Masked">Identifiers are redacted: the caller lacks identifier.reveal.</param>
/// <param name="DumpWindowStart">yyyy-MM-dd, the initial dump's first day.</param>
/// <param name="DumpWindowEnd">yyyy-MM-dd, its last day.</param>
/// <param name="DataThrough">yyyy-MM-dd, the latest day in the event log.</param>
/// <param name="ElapsedMs">Server time.</param>
/// <param name="RowsRead">Rows the server read.</param>
public sealed record TimelineResponse(
    string Kind, string Identifier, string? Tac, string? Brand, string? Model,
    TimelineSummaryInfo Summary,
    IReadOnlyList<TimelineGroupInfo> Groups,
    IReadOnlyList<TimelineBindingInfo> Bindings,
    bool Truncated, int MaxBindings,
    IReadOnlyList<string> Withheld, bool Masked,
    string DumpWindowStart, string DumpWindowEnd, string? DataThrough,
    long ElapsedMs, long RowsRead);
