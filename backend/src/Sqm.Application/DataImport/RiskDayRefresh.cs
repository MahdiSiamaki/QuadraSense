namespace Sqm.Application.DataImport;

/// <summary>What the risk day step wrote for one day (analytics migration 023).</summary>
/// <param name="Changes">Numbers whose SIM changed that day, by the dashboard's same-day definition.</param>
/// <param name="SetAside">
/// Of those, the changes that will not be listed as behaviour: set aside because the feed listed one of
/// their SIMs under several numbers that day, or - on a day whose feed quality was not measured - all
/// of them, because none could be screened.
/// </param>
/// <param name="QualityMeasured">
/// False when the day's feed-quality rows were missing, so no change could be screened; they are kept
/// as 'dq_unavailable' and counted nowhere until the day is refreshed.
/// </param>
public sealed record RiskDayRefresh(long Changes, long SetAside, bool QualityMeasured);
