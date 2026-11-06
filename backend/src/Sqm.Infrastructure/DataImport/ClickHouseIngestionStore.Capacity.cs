using Sqm.Application.DataImport;

namespace Sqm.Infrastructure.DataImport;

/// <summary>What ClickHouse says it has to give.</summary>
/// <remarks>
/// <para>
/// <c>max_server_memory_usage</c> rather than the cgroup figures: ClickHouse has already worked it out
/// from wherever it runs. <c>CGroupMemoryTotal</c> reads 0 on the laptop and <c>CGroupMaxCPU</c> does not
/// exist there, because Docker Desktop's limit is the whole VM. The same goes for the cores, which
/// <c>max_threads</c> holds as <c>auto(n)</c>: 12 on the laptop, 6 on the VM, where cgroups limit it.
/// </para>
/// <para>
/// The peak comes from the runs themselves, because nothing else knows it: <c>planning_spell_stage</c>
/// holds 1.59 GiB on the VM and 828 MiB on the laptop for the same work. 0.18 s to read on the VM.
/// </para>
/// </remarks>
public sealed partial class ClickHouseIngestionStore : IEngineCapacityReader
{
    /// <inheritdoc />
    public async Task<EngineCapacity> ReadCapacityAsync(CancellationToken ct)
    {
        var memory = await ScalarAsync(
            "SELECT toInt64(value) FROM system.server_settings WHERE name = 'max_server_memory_usage'",
            ct).ConfigureAwait(false);

        var cores = await ScalarAsync("SELECT toInt64(getSetting('max_threads'))", ct).ConfigureAwait(false);

        var peak = await ScalarAsync($"""
            SELECT toInt64(max(memory_usage))
            FROM system.query_log
            WHERE event_date >= today() - 14 AND type = 'QueryFinish' AND query_kind = 'Insert'
              AND match(query, '^\\s*INSERT INTO {_database}\\.(quality_chunk|risk_|planning_)')
            """, ct).ConfigureAwait(false);

        return new EngineCapacity(
            memory ?? 0,
            (int)(cores ?? 1),
            peak is > 0 ? peak.Value : _options.BoundedMemoryBytes);
    }
}
