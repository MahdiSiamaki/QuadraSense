using Dapper;
using Npgsql;
using Sqm.Application.DataImport;

namespace Sqm.Infrastructure.DataImport;

/// <summary>TAC version lifecycle: register, list, activate, roll back.</summary>
public sealed partial class PostgresImportJobRepository
{
    public async Task CreateTacVersionAsync(
        long jobId,
        int analyticsVersionId,
        string versionLabel,
        DateOnly? datasetDate,
        int rowCount,
        int? diffAgainstAnalyticsVersionId,
        TacVersionDiff? diff,
        CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        // Created at READY, not DRAFT. The worker has already loaded, checked and diffed it, so
        // by the time this row exists there is nothing left to do but decide - and a status that
        // said DRAFT would suggest otherwise.
        const string Sql = """
            INSERT INTO imports.tac_version
                (job_id, analytics_version_id, version_label, dataset_date, status, row_count,
                 diff_against_id, tacs_added, tacs_updated, tacs_removed, tacs_unchanged,
                 affected_bindings)
            VALUES (@job, @analytics, @label, @datasetDate, 'READY', @rows,
                    (SELECT id FROM imports.tac_version WHERE analytics_version_id = @against),
                    @added, @updated, @removed, @unchanged, @affected)
            ON CONFLICT (version_label) DO UPDATE
               SET job_id = EXCLUDED.job_id,
                   analytics_version_id = EXCLUDED.analytics_version_id,
                   status = 'READY',
                   row_count = EXCLUDED.row_count,
                   tacs_added = EXCLUDED.tacs_added,
                   tacs_updated = EXCLUDED.tacs_updated,
                   tacs_removed = EXCLUDED.tacs_removed,
                   tacs_unchanged = EXCLUDED.tacs_unchanged,
                   affected_bindings = EXCLUDED.affected_bindings
            """;

        await connection.ExecuteAsync(Command(Sql, new
        {
            job = jobId,
            analytics = analyticsVersionId,
            label = versionLabel,
            datasetDate,
            rows = rowCount,
            against = diffAgainstAnalyticsVersionId,
            added = diff?.Added,
            updated = diff?.Updated,
            removed = diff?.Removed,
            unchanged = diff?.Unchanged,
            affected = diff?.AffectedActiveBindings,
        }, ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TacVersion>> ListTacVersionsAsync(CancellationToken ct)
    {
        const string Sql = """
            SELECT id                   AS Id,
                   job_id               AS JobId,
                   analytics_version_id AS AnalyticsVersionId,
                   version_label        AS VersionLabel,
                   dataset_date         AS DatasetDate,
                   status::text         AS StatusText,
                   row_count            AS RowCount,
                   diff_against_id      AS DiffAgainstId,
                   tacs_added           AS TacsAdded,
                   tacs_updated         AS TacsUpdated,
                   tacs_removed         AS TacsRemoved,
                   tacs_unchanged       AS TacsUnchanged,
                   affected_bindings    AS AffectedBindings,
                   created_at           AS CreatedAt,
                   activated_at         AS ActivatedAt,
                   activated_by         AS ActivatedBy,
                   superseded_at        AS SupersededAt
              FROM imports.tac_version
             ORDER BY created_at DESC
            """;

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        var rows = await connection.QueryAsync<TacVersionRow>(Command(Sql, null, ct))
            .ConfigureAwait(false);

        return [.. rows.Select(r => r.ToModel())];
    }

    /// <summary>
    /// Makes one version active and demotes the one it replaces, in a single transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns the analytics version number so the caller can point the analytics store at it.
    /// The order is deliberate: PostgreSQL commits first, and only then does the analytics store
    /// switch. If the switch fails, the operational record says ACTIVE while the analytics view
    /// still serves the old version - visible, correctable, and detectable by comparing the two.
    /// The opposite order would silently change what every screen shows with no record of why.
    /// </para>
    /// <para>
    /// A partial unique index on <c>(status = 'ACTIVE') WHERE status = 'ACTIVE'</c> means the
    /// database itself refuses a second active version, so this cannot produce two even if the
    /// demote below were wrong.
    /// </para>
    /// </remarks>
    public async Task<TacActivationResult?> ActivateTacVersionAsync(
        long tacVersionId, string actor, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        const string CandidateSql = """
            SELECT analytics_version_id, version_label, status::text
              FROM imports.tac_version
             WHERE id = @id
               FOR UPDATE
            """;

        var candidate = await connection.QuerySingleOrDefaultAsync<CandidateRow>(
            Command(CandidateSql, new { id = tacVersionId }, ct, transaction)).ConfigureAwait(false);

        if (candidate is null || candidate.analytics_version_id is null)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return null;
        }

        if (candidate.status is not ("READY" or "SUPERSEDED"))
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Version {candidate.version_label} is {candidate.status}. Only a version that is "
                + "READY, or one that was active before, can be activated.");
        }

        const string DemoteSql = """
            UPDATE imports.tac_version
               SET status = 'SUPERSEDED', superseded_at = now()
             WHERE status = 'ACTIVE' AND id <> @id
            RETURNING id, analytics_version_id, version_label
            """;

        var demoted = await connection.QuerySingleOrDefaultAsync<DemotedRow>(
            Command(DemoteSql, new { id = tacVersionId }, ct, transaction)).ConfigureAwait(false);

        const string PromoteSql = """
            UPDATE imports.tac_version
               SET status = 'ACTIVE', activated_at = now(), activated_by = @actor,
                   superseded_at = NULL
             WHERE id = @id
            """;

        await connection.ExecuteAsync(
            Command(PromoteSql, new { id = tacVersionId, actor }, ct, transaction))
            .ConfigureAwait(false);

        await WriteAuditCoreAsync(
            connection, transaction, actor,
            demoted is null ? "tac.activate" : "tac.activate.replacing",
            null, null, tacVersionId, null,
            new
            {
                activated = candidate.version_label,
                analyticsVersionId = candidate.analytics_version_id,
                replaced = demoted?.version_label,
            },
            ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return new TacActivationResult(
            tacVersionId,
            candidate.analytics_version_id.Value,
            candidate.version_label,
            demoted?.id,
            demoted?.version_label);
    }

    /// <summary>Records that the analytics store could not be switched after all.</summary>
    /// <remarks>
    /// Called when the activation commits in PostgreSQL but the analytics switch then fails.
    /// Leaving the operational record claiming ACTIVE would make the two databases disagree with
    /// nothing on screen to say so.
    /// </remarks>
    public async Task RevertTacActivationAsync(
        long tacVersionId, long? previouslyActiveId, string actor, string reason, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await connection.ExecuteAsync(Command(
            """
            UPDATE imports.tac_version
               SET status = 'READY', activated_at = NULL, activated_by = NULL
             WHERE id = @id
            """, new { id = tacVersionId }, ct, transaction)).ConfigureAwait(false);

        if (previouslyActiveId is { } previous)
        {
            await connection.ExecuteAsync(Command(
                """
                UPDATE imports.tac_version
                   SET status = 'ACTIVE', superseded_at = NULL
                 WHERE id = @id
                """, new { id = previous }, ct, transaction)).ConfigureAwait(false);
        }

        await WriteAuditCoreAsync(
            connection, transaction, actor, "tac.activate.reverted", null, null, tacVersionId,
            null, new { reason }, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private sealed record CandidateRow(int? analytics_version_id, string version_label, string status);

    private sealed record DemotedRow(long id, int? analytics_version_id, string version_label);

    private sealed record TacVersionRow(
        long Id, long JobId, int? AnalyticsVersionId, string VersionLabel, DateOnly? DatasetDate,
        string StatusText, int? RowCount, long? DiffAgainstId, int? TacsAdded, int? TacsUpdated,
        int? TacsRemoved, int? TacsUnchanged, long? AffectedBindings, DateTimeOffset CreatedAt,
        DateTimeOffset? ActivatedAt, string? ActivatedBy, DateTimeOffset? SupersededAt)
    {
        public TacVersion ToModel() => new(
            Id, JobId, VersionLabel, DatasetDate,
            Enum.Parse<TacVersionStatus>(StatusText, ignoreCase: true),
            RowCount, DiffAgainstId, TacsAdded, TacsUpdated, TacsRemoved, TacsUnchanged,
            CreatedAt, ActivatedAt, ActivatedBy, SupersededAt);
    }
}

/// <summary>Schema-version resolution.</summary>
public sealed partial class PostgresImportJobRepository
{
    public async Task<SchemaResolution> ResolveSchemaAsync(
        long jobId, string sourceCode, IReadOnlyList<string> columns, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(columns);

        var hash = SchemaFingerprint.Compute(columns);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        const string MatchSql = """
            SELECT id, version FROM imports.schema_version
             WHERE source_code = @source AND header_hash = @hash
            """;

        var match = await connection.QuerySingleOrDefaultAsync<SchemaRow>(
            Command(MatchSql, new { source = sourceCode, hash }, ct, transaction))
            .ConfigureAwait(false);

        if (match is not null)
        {
            await AttachSchemaAsync(connection, transaction, jobId, match.id, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);

            return new SchemaResolution(
                SchemaVerdict.Known, match.id, match.version,
                $"Header matches schema {match.version}.");
        }

        const string CurrentSql = """
            SELECT id, version, columns FROM imports.schema_version
             WHERE source_code = @source AND is_current
             ORDER BY id DESC LIMIT 1
            """;

        var current = await connection.QuerySingleOrDefaultAsync<CurrentSchemaRow>(
            Command(CurrentSql, new { source = sourceCode }, ct, transaction)).ConfigureAwait(false);

        // Nothing on record yet. The first file a source ever delivers defines its contract;
        // there is nothing to compare it against and refusing it would mean no source could ever
        // be onboarded.
        if (current is null)
        {
            var firstId = await RegisterSchemaAsync(
                connection, transaction, sourceCode, "v1", columns, hash, ct).ConfigureAwait(false);

            await AttachSchemaAsync(connection, transaction, jobId, firstId, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);

            return new SchemaResolution(
                SchemaVerdict.Known, firstId, "v1",
                $"First file for {sourceCode}; its {columns.Count} columns are now the contract.");
        }

        var (verdict, explanation) = SchemaFingerprint.Compare(current.columns, columns);

        if (verdict == SchemaVerdict.Rejected)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return new SchemaResolution(verdict, null, null, explanation);
        }

        var label = NextLabel(current.version);
        var newId = await RegisterSchemaAsync(
            connection, transaction, sourceCode, label, columns, hash, ct).ConfigureAwait(false);

        await AttachSchemaAsync(connection, transaction, jobId, newId, ct).ConfigureAwait(false);
        await AppendEventCoreAsync(
            connection, transaction, jobId, "warning", "VALIDATING",
            $"New schema version {label}. {explanation}",
            new { previous = current.version, added = columns.Skip(current.columns.Length) }, ct)
            .ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new SchemaResolution(verdict, newId, label, explanation);
    }

    private async Task<int> RegisterSchemaAsync(
        NpgsqlConnection connection,
        System.Data.IDbTransaction transaction,
        string sourceCode,
        string version,
        IReadOnlyList<string> columns,
        string hash,
        CancellationToken ct)
    {
        // The previous version stops being current but is not deleted: a job imported under it
        // should still be explainable years later, and that needs the contract it ran against.
        await connection.ExecuteAsync(Command(
            "UPDATE imports.schema_version SET is_current = false WHERE source_code = @source",
            new { source = sourceCode }, ct, transaction)).ConfigureAwait(false);

        const string Sql = """
            INSERT INTO imports.schema_version
                (source_code, version, columns, header_hash, is_current)
            VALUES (@source, @version, @columns, @hash, true)
            RETURNING id
            """;

        return await connection.ExecuteScalarAsync<int>(Command(Sql, new
        {
            source = sourceCode,
            version,
            columns = columns.ToArray(),
            hash,
        }, ct, transaction)).ConfigureAwait(false);
    }

    private async Task AttachSchemaAsync(
        NpgsqlConnection connection,
        System.Data.IDbTransaction transaction,
        long jobId,
        int schemaVersionId,
        CancellationToken ct)
    {
        await connection.ExecuteAsync(Command(
            "UPDATE imports.import_job SET schema_version_id = @schema WHERE id = @job",
            new { job = jobId, schema = schemaVersionId }, ct, transaction)).ConfigureAwait(false);
    }

    /// <summary>v1 -> v2. Falls back to a timestamp if the label is not in that shape.</summary>
    private static string NextLabel(string current) =>
        current.StartsWith('v') && int.TryParse(current[1..], out var n)
            ? $"v{n + 1}"
            : $"v{DateTime.UtcNow:yyyyMMddHHmmss}";

    private sealed record SchemaRow(int id, string version);

    private sealed record CurrentSchemaRow(int id, string version, string[] columns);
}
