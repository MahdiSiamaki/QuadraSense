namespace Sqm.Migrator;

/// <summary>A database the migrator can apply migrations to.</summary>
/// <remarks>
/// The two stores differ in ways that matter here, which is why this is an interface rather than
/// a switch on a connection string. PostgreSQL applies a whole migration inside one transaction,
/// so a failure halfway through leaves no trace. ClickHouse has no DDL transactions: it applies
/// statement by statement, and a failure can leave the schema part-migrated. The migrator has to
/// say which guarantee the operator is getting.
/// </remarks>
internal interface IMigrationTarget : IAsyncDisposable
{
    /// <summary>Human-readable name for output, e.g. <c>PostgreSQL</c>.</summary>
    string Description { get; }

    /// <summary>Whether a failed migration rolls back cleanly.</summary>
    bool IsTransactional { get; }

    Task OpenAsync(CancellationToken cancellationToken);

    /// <summary>Creates the bookkeeping table if it is not already there.</summary>
    Task EnsureHistoryTableAsync(CancellationToken cancellationToken);

    /// <summary>Versions already applied, mapped to the checksum recorded when they ran.</summary>
    Task<IReadOnlyDictionary<string, string>> GetAppliedAsync(CancellationToken cancellationToken);

    /// <summary>Applies one migration and records it. Throws on failure.</summary>
    Task ApplyAsync(Migration migration, Action<string> progress, CancellationToken cancellationToken);

    /// <summary>
    /// Records a migration as applied without executing it.
    /// </summary>
    /// <remarks>
    /// For adopting a schema that already exists. Both stores here were built by applying SQL
    /// files by hand before this tool was written, and re-running those files would at best be a
    /// no-op and at worst drop a populated table.
    /// </remarks>
    Task RecordWithoutRunningAsync(Migration migration, CancellationToken cancellationToken);
}
