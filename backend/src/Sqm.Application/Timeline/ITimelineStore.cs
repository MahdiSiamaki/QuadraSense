using Sqm.Domain.Timeline;

namespace Sqm.Application.Timeline;

/// <summary>Whose timeline.</summary>
public enum TimelineCentre
{
    /// <summary>A phone number.</summary>
    Msisdn,

    /// <summary>A SIM.</summary>
    Imsi,

    /// <summary>A handset (IMEI).</summary>
    Imei,
}

/// <summary>Whether the binding history can answer.</summary>
public enum HistoryReadiness
{
    /// <summary>Every day in the event log, and the initial dump, is in the history.</summary>
    Ready,

    /// <summary>Analytics migration 022 is not applied.</summary>
    NotDeployed,

    /// <summary>The tables exist but the backfill has not finished: a timeline would be missing days.</summary>
    Building,
}

/// <summary>One binding's stored history, with its state in current state.</summary>
/// <param name="Msisdn">The number.</param>
/// <param name="Imsi">The SIM.</param>
/// <param name="Imei">The handset, as stored - including malformed values.</param>
/// <param name="Tac">The first eight digits of a 14-digit IMEI; null otherwise.</param>
/// <param name="Brand">GSMA brand.</param>
/// <param name="Model">GSMA marketing name.</param>
/// <param name="InDump">The initial dump listed it.</param>
/// <param name="Events">Its events, unordered.</param>
/// <param name="Active">Current state's verdict: not yet removed by the feed.</param>
public sealed record StoredBinding(
    ulong Msisdn, ulong Imsi, string Imei, string? Tac, string? Brand, string? Model,
    bool InDump, IReadOnlyList<BindingEvent> Events, bool Active);

/// <summary>The bindings of one number, SIM or handset, and what reading them cost.</summary>
/// <param name="Bindings">Most recently changed first, at most the limit asked for.</param>
/// <param name="Total">How many there are in all.</param>
/// <param name="ElapsedMs">Server time.</param>
/// <param name="RowsRead">Rows the server read.</param>
public sealed record StoredTimeline(IReadOnlyList<StoredBinding> Bindings, long Total, long ElapsedMs, long RowsRead);

/// <summary>When an entity was first seen and last changed, from its history.</summary>
/// <param name="FirstSeen">The earliest of its bindings: the dump window's first day if any was in the dump.</param>
/// <param name="FirstSeenIsDumpWindow">FirstSeen stands for the dump window.</param>
/// <param name="LastChange">The latest event of any of its bindings; null when no daily file mentioned it.</param>
public sealed record EntitySeen(DateOnly FirstSeen, bool FirstSeenIsDumpWindow, DateOnly? LastChange);

/// <summary>Reads the binding history (analytics migration 022).</summary>
public interface ITimelineStore
{
    /// <summary>Whether the history is deployed and complete. Cached briefly: it is asked on every request.</summary>
    Task<HistoryReadiness> GetReadinessAsync(CancellationToken ct);

    /// <summary>The latest day in the event log.</summary>
    Task<DateOnly?> DataThroughAsync(CancellationToken ct);

    /// <summary>One entity's bindings with their events - a key read on the copy sorted by its kind.</summary>
    /// <param name="centre">What kind of identifier.</param>
    /// <param name="digits">The identifier, normalised.</param>
    /// <param name="limit">The most bindings to return; the most recently changed are kept.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<StoredTimeline> GetAsync(TimelineCentre centre, string digits, int limit, CancellationToken ct);

    /// <summary>First seen and last change for one entity, without its events; null when it has no binding.</summary>
    Task<EntitySeen?> GetSeenAsync(TimelineCentre centre, string digits, CancellationToken ct);
}

/// <summary>What one timeline may return. Configuration section <c>Timeline</c>.</summary>
public sealed class TimelineOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Timeline";

    /// <summary>
    /// Bindings in one timeline, at most. The most-shared real IMEI has 8,578 (measured 2026-10-01),
    /// so this shows every binding of every entity measured; past it the most recently changed are
    /// shown and the response says so.
    /// </summary>
    public int MaxBindings { get; set; } = 10_000;
}
