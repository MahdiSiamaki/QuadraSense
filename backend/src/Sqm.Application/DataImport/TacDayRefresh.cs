namespace Sqm.Application.DataImport;

/// <summary>What the model day step wrote for one day, or for the initial dump (analytics migration 025).</summary>
/// <param name="Models">Models (TACs) with at least one countable IMEI.</param>
/// <param name="Imeis">Countable IMEIs, summed over the models.</param>
public sealed record TacDayRefresh(long Models, long Imeis);
