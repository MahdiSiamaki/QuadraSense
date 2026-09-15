using System.Globalization;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.Options;
using Sqm.Application.Abstractions;
using Sqm.Infrastructure.DataImport;
using Sqm.Infrastructure.Identity;

namespace Sqm.Infrastructure.Catalog;

/// <summary>Device photographs, held in <c>catalog.device_image</c>.</summary>
/// <remarks>
/// <para>
/// Every column is aliased to its constructor parameter name in these queries. That is not style:
/// <b>Dapper strips underscores when mapping to properties and does not when matching constructor
/// parameters</b>, so <c>content_type</c> binds to a settable <c>ContentType</c> property and
/// silently fails to bind to a positional record's <c>contentType</c> parameter - reporting a
/// missing constructor rather than a missing column. That cost an hour once already.
/// </para>
/// </remarks>
public sealed class PostgresDeviceImageStore : IDeviceImageStore
{
    private readonly IdentityDataSource _db;
    private readonly int _commandTimeout;

    /// <summary>Creates the store.</summary>
    /// <param name="db">Connection source for the operational database.</param>
    /// <param name="postgres">Options carrying the command timeout.</param>
    public PostgresDeviceImageStore(IdentityDataSource db, IOptions<PostgresOptions> postgres)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        _db = db;
        _commandTimeout = postgres.Value.CommandTimeoutSeconds;
    }

    /// <inheritdoc />
    public async Task<DeviceImage?> GetAsync(string tac, CancellationToken ct)
    {
        const string Sql = """
            SELECT content_type AS ContentType,
                   bytes        AS Bytes,
                   sha256       AS Sha256,
                   updated_at   AS UpdatedAt
            FROM catalog.device_image
            WHERE tac = @tac
            """;

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var row = await connection.QuerySingleOrDefaultAsync<ImageRow>(
            new CommandDefinition(Sql, new { tac }, commandTimeout: _commandTimeout,
                cancellationToken: ct)).ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        return new DeviceImage(tac, row.ContentType, row.Bytes, ToETag(row.Sha256), row.UpdatedAt);
    }

    /// <inheritdoc />
    public async Task<DeviceImageInfo?> GetInfoAsync(string tac, CancellationToken ct)
    {
        // octet_length rather than the bytes themselves: this is called to render an admin panel,
        // and transferring half a megabyte to display "112 KB" would be an odd way to do it.
        const string Sql = """
            SELECT i.tac                   AS Tac,
                   i.content_type          AS ContentType,
                   octet_length(i.bytes)   AS ByteSize,
                   i.source_note           AS SourceNote,
                   u.username              AS UploadedBy,
                   i.updated_at            AS UpdatedAt
            FROM catalog.device_image AS i
            LEFT JOIN auth.user_account AS u ON u.id = i.uploaded_by
            WHERE i.tac = @tac
            """;

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<DeviceImageInfo>(
            new CommandDefinition(Sql, new { tac }, commandTimeout: _commandTimeout,
                cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> GetPresentAsync(
        IReadOnlyList<string> tacs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tacs);

        if (tacs.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        const string Sql = "SELECT tac FROM catalog.device_image WHERE tac = ANY(@tacs)";

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var found = await connection.QueryAsync<string>(
            new CommandDefinition(Sql, new { tacs = tacs.ToArray() },
                commandTimeout: _commandTimeout, cancellationToken: ct)).ConfigureAwait(false);

        return new HashSet<string>(found, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async Task SaveAsync(
        string tac, string contentType, byte[] bytes, string sourceNote, long userId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        // The hash is computed here rather than taken from the caller, because it becomes the
        // HTTP ETag and an ETag that does not follow the bytes is worse than no ETag at all -
        // it makes browsers keep an image that has been replaced.
        var sha = SHA256.HashData(bytes);

        const string Sql = """
            INSERT INTO catalog.device_image
                (tac, content_type, bytes, sha256, source_note, uploaded_by, uploaded_at, updated_at)
            VALUES (@tac, @contentType, @bytes, @sha, @sourceNote, @userId, now(), now())
            ON CONFLICT (tac) DO UPDATE SET
                content_type = EXCLUDED.content_type,
                bytes        = EXCLUDED.bytes,
                sha256       = EXCLUDED.sha256,
                source_note  = EXCLUDED.source_note,
                uploaded_by  = EXCLUDED.uploaded_by,
                updated_at   = now()
            """;

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            Sql,
            new { tac, contentType, bytes, sha, sourceNote, userId },
            commandTimeout: _commandTimeout,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string tac, CancellationToken ct)
    {
        const string Sql = "DELETE FROM catalog.device_image WHERE tac = @tac";

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            Sql, new { tac }, commandTimeout: _commandTimeout, cancellationToken: ct))
            .ConfigureAwait(false);

        return affected > 0;
    }

    /// <summary>
    /// Renders a content hash as a strong entity tag.
    /// </summary>
    /// <remarks>
    /// Sixteen hex characters of a SHA-256 is 64 bits, which is far more than enough to tell two
    /// device photographs apart and short enough to keep the header small on a page that requests
    /// forty of them.
    /// </remarks>
    private static string ToETag(byte[] sha256) =>
        "\"" + Convert.ToHexString(sha256.AsSpan(0, 8)).ToLower(CultureInfo.InvariantCulture) + "\"";

    /// <summary>
    /// A class with settable properties, not a positional record.
    /// </summary>
    /// <remarks>
    /// Deliberate: Dapper's property mapping strips underscores and its constructor matching does
    /// not. The columns are aliased above anyway, so either would work here - but the property
    /// path is the one that keeps working if somebody removes an alias.
    /// </remarks>
    private sealed class ImageRow
    {
        public string ContentType { get; set; } = string.Empty;

        public byte[] Bytes { get; set; } = [];

        public byte[] Sha256 { get; set; } = [];

        public DateTimeOffset UpdatedAt { get; set; }
    }
}
