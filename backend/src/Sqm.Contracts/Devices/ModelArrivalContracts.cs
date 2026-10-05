namespace Sqm.Contracts.Devices;

/// <summary>Whether first appearances can be shown, and as of when.</summary>
/// <param name="Ready">True when every day of the event log and the initial dump are written.</param>
/// <param name="NotReady">Why not, when not.</param>
/// <param name="DataThrough">yyyy-MM-dd, the latest day written.</param>
/// <param name="DataStart">yyyy-MM-dd, the first daily file: anything in the initial dump was seen before it.</param>
public sealed record ModelArrivalStatus(bool Ready, string? NotReady, string? DataThrough, string DataStart);

/// <summary>A model and when the feed first named it.</summary>
/// <param name="Tac">The TAC.</param>
/// <param name="Brand">GSMA brand or manufacturer; null when GSMA does not know the TAC.</param>
/// <param name="Model">GSMA marketing name.</param>
/// <param name="DeviceType">GSMA device type.</param>
/// <param name="FirstSeen">yyyy-MM-dd: the first daily file that named one of its handsets.</param>
/// <param name="NetworkAgeDays">Days from then to the data-through day. Not the handsets' age.</param>
/// <param name="DaysSeen">Days since then with at least one of its handsets in the file.</param>
/// <param name="FirstDayImeis">Handsets (IMEIs) on its first day.</param>
/// <param name="Handsets">Handsets now, estimated, from the latest delivery; null before its first delivery.</param>
/// <param name="Sims">SIMs now, estimated, likewise.</param>
public sealed record NewModelInfo(
    string Tac, string? Brand, string? Model, string? DeviceType, string FirstSeen, int? NetworkAgeDays,
    int DaysSeen, long FirstDayImeis, long? Handsets, long? Sims);

/// <summary>A page of models first seen in a period.</summary>
public sealed record NewModelsResponse(
    IReadOnlyList<NewModelInfo> Rows, long Total, int Page, int PageSize, string From, string To, string? DataThrough);

/// <summary>Models first seen in one month or week.</summary>
/// <param name="PeriodStart">yyyy-MM-dd.</param>
/// <param name="Models">Models first seen.</param>
/// <param name="KnownModels">Of those, known to GSMA.</param>
public sealed record ModelArrivalPeriodInfo(string PeriodStart, long Models, long KnownModels);

/// <summary>First appearances per period. The first weeks after the data starts fill in models the dump's month missed.</summary>
public sealed record ModelArrivalsResponse(string Grain, IReadOnlyList<ModelArrivalPeriodInfo> Periods, string DataStart);

/// <summary>One model's first appearance and network age.</summary>
/// <param name="Tac">The TAC.</param>
/// <param name="InDump">Seen in the initial dump: before the first daily file.</param>
/// <param name="FirstSeen">yyyy-MM-dd of the first daily file that named it; null when none has.</param>
/// <param name="NetworkAgeDays">Days since first seen; for a model in the dump, at least this many.</param>
/// <param name="AtLeast">True when the age is a lower bound - the model was in the dump.</param>
/// <param name="DaysSeen">Days with at least one of its handsets in the file.</param>
/// <param name="DataThrough">yyyy-MM-dd the age is counted to.</param>
public sealed record ModelNetworkAgeResponse(
    string Tac, bool InDump, string? FirstSeen, int? NetworkAgeDays, bool AtLeast, int DaysSeen, string? DataThrough);
