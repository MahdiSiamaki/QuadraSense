using Sqm.Application.Risk;

namespace Sqm.Infrastructure.Risk;

/// <summary>The statements the worker that builds the risk snapshot and the API that reads it must agree on.</summary>
internal static class RiskSnapshotSql
{
    /// <summary>
    /// The fingerprint of everything a snapshot run reads or is shaped by: the history's day ledger,
    /// each day's feed-quality measurement, the active GSMA version, the measures' definition and the
    /// storage floors.
    /// </summary>
    /// <param name="prefix">The database and a dot, or empty for the connection's own database.</param>
    /// <param name="tacVersion">The active GSMA version, read first: as a literal it hashes as the worker always hashed it.</param>
    /// <param name="floors">The storage floors.</param>
    /// <remarks>
    /// One text for both sides. The worker publishes a run with the value it was planned under; the API
    /// calls the run stale when today's value differs. Two copies of this statement would disagree the
    /// first time one was edited, and every run would read as stale - or none would.
    /// </remarks>
    public static string Fingerprint(string prefix, int tacVersion, RiskFloorOptions floors) => $"""
        SELECT cityHash64(
            (SELECT groupArray((data_date, state, events, updated_at))
               FROM (SELECT data_date, state, events, updated_at FROM {prefix}binding_history_day FINAL ORDER BY data_date)),
            (SELECT groupArray((data_date, computed_at, tac_version_id))
               FROM (SELECT data_date, computed_at, tac_version_id FROM {prefix}dq_daily FINAL ORDER BY data_date)),
            {tacVersion},
            {RiskComputeOptions.DefinitionVersion},
            {floors.SimImeis30}, {floors.ImeiSims30}, {floors.ImeiSimsEver}, {floors.ImeiSimsNotRemoved})
        """;

    /// <summary>The active GSMA version.</summary>
    public static string TacVersion(string prefix) =>
        $"SELECT version_id FROM {prefix}tac_active FINAL ORDER BY activated_at DESC LIMIT 1";
}
