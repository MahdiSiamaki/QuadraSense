namespace Sqm.Application.Devices;

/// <summary>Whether first appearances can be read, and up to which day.</summary>
/// <param name="Deployed">False before analytics migration 025.</param>
/// <param name="Complete">True when every day of the event log and the initial dump are written.</param>
/// <param name="DaysWritten">Days written, the dump not counted.</param>
/// <param name="DaysInLog">Days the event log holds.</param>
/// <param name="DataThrough">The latest day written.</param>
public sealed record ModelArrivalState(bool Deployed, bool Complete, int DaysWritten, int DaysInLog, DateOnly? DataThrough);

/// <summary>Which models to list: first seen in a period, optionally narrowed.</summary>
/// <param name="From">First-seen day, from (inclusive).</param>
/// <param name="To">First-seen day, to (inclusive).</param>
/// <param name="Brand">Brand or manufacturer contains this, case-insensitive; null for any.</param>
/// <param name="DeviceType">GSMA device type, exactly; null for any.</param>
/// <param name="KnownOnly">Only models GSMA knows. An unknown TAC has no name to list it under.</param>
/// <param name="Page">From 1.</param>
/// <param name="PageSize">Rows per page.</param>
public sealed record NewModelQuery(
    DateOnly From, DateOnly To, string? Brand, string? DeviceType, bool KnownOnly, int Page, int PageSize);

/// <summary>A model and when the feed first named it.</summary>
/// <param name="Tac">The TAC.</param>
/// <param name="Brand">GSMA brand, or the manufacturer; null when GSMA does not know the TAC.</param>
/// <param name="Model">GSMA marketing name.</param>
/// <param name="DeviceType">GSMA device type.</param>
/// <param name="FirstSeen">The first day a daily file named one of its handsets.</param>
/// <param name="DaysSeen">Days, since then, with at least one of its handsets in the file.</param>
/// <param name="FirstDayImeis">Handsets (IMEIs) on that first day.</param>
/// <param name="Handsets">Its handsets now, estimated, from the latest delivery's device mart; null before its first delivery.</param>
/// <param name="Sims">Its SIMs now, estimated, likewise.</param>
public sealed record NewModelRow(
    string Tac, string? Brand, string? Model, string? DeviceType, DateOnly FirstSeen, int DaysSeen, long FirstDayImeis,
    long? Handsets, long? Sims);

/// <summary>One page of new models.</summary>
public sealed record NewModelPage(IReadOnlyList<NewModelRow> Rows, long Total, long ElapsedMs);

/// <summary>How many models first appeared in one period.</summary>
/// <param name="PeriodStart">The period's first day.</param>
/// <param name="Models">Models first seen in it.</param>
/// <param name="KnownModels">Of those, models GSMA knows.</param>
public sealed record ModelArrivalPeriod(DateOnly PeriodStart, long Models, long KnownModels);

/// <summary>When one model first appeared.</summary>
/// <param name="Tac">The TAC.</param>
/// <param name="InDump">The initial dump listed it: seen before the first daily file.</param>
/// <param name="FirstSeen">The first daily file that named it; null when no daily file has.</param>
/// <param name="DaysSeen">Days with at least one of its handsets in the file.</param>
public sealed record ModelFirstSeen(string Tac, bool InDump, DateOnly? FirstSeen, int DaysSeen);

/// <summary>Reads first appearances from the per-model day counts (analytics migration 025). Read-only.</summary>
public interface IModelArrivalReader
{
    /// <summary>Whether the table exists and covers the whole event log.</summary>
    Task<ModelArrivalState> GetStateAsync(CancellationToken ct);

    /// <summary>Models first seen in a period - not in the initial dump - newest first.</summary>
    Task<NewModelPage> ListAsync(NewModelQuery query, CancellationToken ct);

    /// <summary>How many models first appeared per month (or week), dump excluded.</summary>
    Task<IReadOnlyList<ModelArrivalPeriod>> ArrivalsAsync(bool weekly, CancellationToken ct);

    /// <summary>One model's first appearance, or null when the feed never named it.</summary>
    Task<ModelFirstSeen?> FirstSeenAsync(string tac, CancellationToken ct);
}

/// <summary>Network age: the time since a model, handset, SIM or number was first seen in this data.</summary>
/// <remarks>
/// Not a manufacturing age, and not how long anything has existed: the data starts with an initial
/// dump of the month to 2026-01-25, so anything first seen there is "at least" as old as the data and
/// no older is known.
/// </remarks>
public static class NetworkAge
{
    /// <summary>The first day of the daily files; anything seen in the dump was seen before it.</summary>
    public static readonly DateOnly DataStart = new(2026, 1, 26);

    /// <summary>Whole days from first seen to the data-through day, or null when unknown.</summary>
    public static int? Days(DateOnly? firstSeen, bool inDump, DateOnly? dataThrough) =>
        dataThrough is not { } through ? null
        : inDump ? through.DayNumber - DataStart.DayNumber + 1
        : firstSeen is { } first ? through.DayNumber - first.DayNumber
        : null;
}
