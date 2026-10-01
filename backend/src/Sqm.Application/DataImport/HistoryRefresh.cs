namespace Sqm.Application.DataImport;

/// <summary>How the binding history took in a day.</summary>
public enum HistoryRefreshKind
{
    /// <summary>The history tables do not exist yet: migration 022 is not applied.</summary>
    NotDeployed,

    /// <summary>A day the history had never seen: its events were added.</summary>
    Added,

    /// <summary>The day had been seen before, or its month was unfinished: the month was rebuilt.</summary>
    RebuiltMonth,
}

/// <summary>What the history step did with a day.</summary>
/// <param name="Kind">Which path it took.</param>
/// <param name="Events">Events taken in: the day's when added, the month's when rebuilt.</param>
/// <param name="Reason">Why the month was rebuilt, in the operator's terms; null otherwise.</param>
public sealed record HistoryRefresh(HistoryRefreshKind Kind, long Events, string? Reason)
{
    /// <summary>The history tables are not there.</summary>
    public static HistoryRefresh NotDeployed { get; } = new(HistoryRefreshKind.NotDeployed, 0, null);
}
