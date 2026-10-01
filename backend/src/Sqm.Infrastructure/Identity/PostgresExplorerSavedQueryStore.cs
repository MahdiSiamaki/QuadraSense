using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Sqm.Application.Explorer;
using Sqm.Contracts.Explorer;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Infrastructure.Identity;

/// <summary>My Queries, in PostgreSQL. Every statement is scoped to the owner.</summary>
public sealed class PostgresExplorerSavedQueryStore : IExplorerSavedQueryStore
{
    /// <summary>
    /// The JSON the API speaks - camelCase, enums by name - so a stored query reads back exactly as
    /// the builder sent it.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private const string UniqueViolation = "23505";

    private readonly IdentityDataSource _db;
    private readonly int _commandTimeout;

    /// <summary>Creates the store over the operational database.</summary>
    public PostgresExplorerSavedQueryStore(IdentityDataSource db, IOptions<PostgresOptions> postgres)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        _db = db;
        _commandTimeout = postgres.Value.CommandTimeoutSeconds;
    }

    private const string Columns = """
        id AS Id, name AS Name, description AS Description, query::text AS Query,
        created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SavedExplorerQuery>> ListAsync(long ownerId, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM explorer.saved_query WHERE owner_user_id = @ownerId ORDER BY updated_at DESC, id DESC",
            new { ownerId }, commandTimeout: _commandTimeout, cancellationToken: ct)).ConfigureAwait(false);
        return [.. rows.Select(Map)];
    }

    /// <inheritdoc />
    public async Task<SavedExplorerQuery?> GetAsync(long ownerId, long id, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM explorer.saved_query WHERE owner_user_id = @ownerId AND id = @id",
            new { ownerId, id }, commandTimeout: _commandTimeout, cancellationToken: ct)).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    /// <inheritdoc />
    public async Task<(SavedQueryOutcome Outcome, SavedExplorerQuery? Query)> CreateAsync(
        long ownerId, string name, string description, ExplorerQueryRequest query, CancellationToken ct)
    {
        try
        {
            await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
            var row = await connection.QuerySingleAsync<Row>(new CommandDefinition(
                $"""
                INSERT INTO explorer.saved_query (owner_user_id, name, description, query)
                VALUES (@ownerId, @name, @description, @query::jsonb)
                RETURNING {Columns}
                """,
                new { ownerId, name = name.Trim(), description, query = JsonSerializer.Serialize(query, Json) },
                commandTimeout: _commandTimeout, cancellationToken: ct)).ConfigureAwait(false);
            return (SavedQueryOutcome.Saved, Map(row));
        }
        catch (PostgresException ex) when (ex.SqlState == UniqueViolation)
        {
            return (SavedQueryOutcome.NameTaken, null);
        }
    }

    /// <inheritdoc />
    public async Task<(SavedQueryOutcome Outcome, SavedExplorerQuery? Query)> UpdateAsync(
        long ownerId, long id, string name, string description, ExplorerQueryRequest query, CancellationToken ct)
    {
        try
        {
            await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
            var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
                $"""
                UPDATE explorer.saved_query
                SET name = @name, description = @description, query = @query::jsonb, updated_at = now()
                WHERE owner_user_id = @ownerId AND id = @id
                RETURNING {Columns}
                """,
                new { ownerId, id, name = name.Trim(), description, query = JsonSerializer.Serialize(query, Json) },
                commandTimeout: _commandTimeout, cancellationToken: ct)).ConfigureAwait(false);
            return row is null ? (SavedQueryOutcome.NotFound, null) : (SavedQueryOutcome.Saved, Map(row));
        }
        catch (PostgresException ex) when (ex.SqlState == UniqueViolation)
        {
            return (SavedQueryOutcome.NameTaken, null);
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(long ownerId, long id, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM explorer.saved_query WHERE owner_user_id = @ownerId AND id = @id",
            new { ownerId, id }, commandTimeout: _commandTimeout, cancellationToken: ct)).ConfigureAwait(false) > 0;
    }

    private static SavedExplorerQuery Map(Row row) => new(
        row.Id, row.Name, row.Description,
        JsonSerializer.Deserialize<ExplorerQueryRequest>(row.Query, Json)
            ?? throw new InvalidOperationException($"Saved query {row.Id} holds no query."),
        row.CreatedAt, row.UpdatedAt);

    private sealed record Row(long Id, string Name, string Description, string Query, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
}
