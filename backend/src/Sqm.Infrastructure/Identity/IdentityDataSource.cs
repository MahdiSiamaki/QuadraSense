using System.Data;
using System.Globalization;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Infrastructure.Identity;

/// <summary>
/// One connection pool shared by every identity repository.
/// </summary>
/// <remarks>
/// <para>
/// The import repository builds its own <see cref="NpgsqlDataSource"/>, which was right when it
/// was the only one. Four more repositories each building their own would mean five pools against
/// the same database in one process, five sets of idle connections, and five places to change a
/// timeout. They share this instead.
/// </para>
/// <para>
/// The Dapper type handlers are registered here as well as in the import repository. Dapper's
/// registry is global and registering the same handler twice is harmless, but relying on another
/// class's static constructor having run is not - it only has if something happened to touch that
/// class first, which is exactly the kind of ordering dependency that works in tests and fails at
/// startup.
/// </para>
/// </remarks>
public sealed class IdentityDataSource : IAsyncDisposable
{
    static IdentityDataSource()
    {
        SqlMapper.AddTypeHandler(new IdentityDateTimeOffsetHandler());
    }

    private readonly NpgsqlDataSource _dataSource;
    private readonly int _commandTimeout;

    /// <summary>Builds the pool from configuration.</summary>
    public IdentityDataSource(IOptions<PostgresOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = options.Value;

        if (string.IsNullOrWhiteSpace(settings.ConnectionString))
        {
            throw new InvalidOperationException(
                $"{PostgresOptions.SectionName}:ConnectionString is not configured.");
        }

        _dataSource = new NpgsqlDataSourceBuilder(settings.ConnectionString).Build();
        _commandTimeout = settings.CommandTimeoutSeconds;
    }

    /// <summary>Opens a pooled connection.</summary>
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct) =>
        await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

    /// <summary>Builds a command with this store's timeout applied.</summary>
    public CommandDefinition Command(
        string sql, object? parameters, CancellationToken ct, IDbTransaction? transaction = null) =>
        new(sql, parameters, transaction, _commandTimeout, cancellationToken: ct);

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _dataSource.DisposeAsync().ConfigureAwait(false);

    /// <summary>
    /// Reads a <c>timestamptz</c> into a <see cref="DateTimeOffset"/>.
    /// </summary>
    /// <remarks>
    /// Npgsql surfaces <c>timestamptz</c> as a UTC <see cref="DateTime"/>, and Dapper matches a
    /// record's constructor by comparing parameter types against the reader's field types - so a
    /// model using <see cref="DateTimeOffset"/> fails to materialise with an error about a
    /// missing parameterless constructor, which says nothing about the real mismatch. The models
    /// keep <see cref="DateTimeOffset"/> because these values reach a browser in another
    /// timezone, and a <see cref="DateTime"/> that merely promises to be UTC is one careless
    /// conversion away from being wrong by hours.
    /// </remarks>
    private sealed class IdentityDateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        {
            parameter.DbType = DbType.DateTimeOffset;
            parameter.Value = value;
        }

        public override DateTimeOffset Parse(object value) => value switch
        {
            DateTimeOffset offset => offset,
            DateTime timestamp => new DateTimeOffset(
                DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)),
            string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture),
            _ => throw new InvalidCastException(
                $"cannot read a timestamp from {value?.GetType().Name ?? "null"}"),
        };
    }
}
