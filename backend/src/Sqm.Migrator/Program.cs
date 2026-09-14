using System.Globalization;
using Sqm.Migrator;

// Forward-only schema migrator for both stores.
//
// Usage:
//   dotnet run --project backend/src/Sqm.Migrator -- \
//       --target postgres --dir db/operational/migrations [--connection <cs>] [--dry-run]
//
// The connection string comes from --connection, or from SQM_POSTGRES_CONNECTION /
// SQM_CLICKHOUSE_CONNECTION. It is never read from a file in the repository.
//
// There is no "down" migration and there will not be one. A rollback script is written when the
// schema is healthy and run when it is not, which is the worst possible combination; the honest
// recovery paths are a forward fix or a restore from backup.

try
{
    return await Run(args, CancellationToken.None).ConfigureAwait(false);
}
catch (Exception ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"migration failed: {ex.Message}");
    return 1;
}

static async Task<int> Run(string[] args, CancellationToken cancellationToken)
{
    var options = CommandLine.Parse(args);
    if (options is null)
    {
        Console.Error.WriteLine(CommandLine.Usage);
        return 2;
    }

    var migrations = Migration.Discover(options.Directory);

    await using IMigrationTarget target = options.Target switch
    {
        TargetKind.Postgres => new PostgresTarget(options.ConnectionString),
        TargetKind.ClickHouse => new ClickHouseTarget(options.ConnectionString),
        _ => throw new InvalidOperationException($"unknown target: {options.Target}"),
    };

    await target.OpenAsync(cancellationToken).ConfigureAwait(false);
    await target.EnsureHistoryTableAsync(cancellationToken).ConfigureAwait(false);

    var applied = await target.GetAppliedAsync(cancellationToken).ConfigureAwait(false);

    Console.WriteLine($"{target.Description}: {migrations.Count} migration files, "
        + $"{applied.Count} already applied");

    if (!target.IsTransactional)
    {
        Console.WriteLine("  note: this store has no DDL transactions; a failure can leave a "
            + "migration partly applied");
    }

    // Checksum every already-applied migration before running anything. An edited migration is a
    // different migration: the database it ran against no longer matches what is in the
    // repository, and every later assumption about the schema is suspect. Catching it here is a
    // refusal to start; catching it later is a debugging session.
    var tampered = migrations
        .Where(m => applied.TryGetValue(m.Version, out var checksum) && checksum != m.Checksum)
        .ToList();

    if (tampered.Count > 0)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("refusing to run: these migrations changed after being applied");
        foreach (var migration in tampered)
        {
            Console.Error.WriteLine($"  {migration.Name}");
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("Applied migrations are immutable. Write a new migration instead.");
        return 1;
    }

    var pending = migrations.Where(m => !applied.ContainsKey(m.Version)).ToList();

    if (pending.Count == 0)
    {
        Console.WriteLine("  up to date");
        return 0;
    }

    Console.WriteLine($"  {pending.Count} pending:");
    foreach (var migration in pending)
    {
        Console.WriteLine($"    {migration.Name}");
    }

    if (options.DryRun)
    {
        Console.WriteLine();
        Console.WriteLine("dry run: nothing was applied");
        return 0;
    }

    Console.WriteLine();

    foreach (var migration in pending)
    {
        Console.WriteLine($"  applying {migration.Name}");
        await target.ApplyAsync(migration, Console.WriteLine, cancellationToken).ConfigureAwait(false);
    }

    Console.WriteLine();
    Console.WriteLine($"applied {pending.Count} migration(s)");
    return 0;
}

internal enum TargetKind
{
    Postgres,
    ClickHouse,
}

internal sealed record Options(
    TargetKind Target, string Directory, string ConnectionString, bool DryRun);

internal static class CommandLine
{
    public const string Usage = """
        usage: Sqm.Migrator --target postgres|clickhouse --dir <migrations-directory>
                            [--connection <connection-string>] [--dry-run]

        The connection string falls back to SQM_POSTGRES_CONNECTION or SQM_CLICKHOUSE_CONNECTION.
        """;

    public static Options? Parse(string[] args)
    {
        string? target = null;
        string? directory = null;
        string? connection = null;
        var dryRun = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--target" when i + 1 < args.Length:
                    target = args[++i];
                    break;
                case "--dir" when i + 1 < args.Length:
                    directory = args[++i];
                    break;
                case "--connection" when i + 1 < args.Length:
                    connection = args[++i];
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                default:
                    Console.Error.WriteLine($"unrecognised argument: {args[i]}");
                    return null;
            }
        }

        if (target is null || directory is null)
        {
            return null;
        }

        var kind = target.ToLowerInvariant() switch
        {
            "postgres" or "postgresql" => TargetKind.Postgres,
            "clickhouse" => TargetKind.ClickHouse,
            _ => (TargetKind?)null,
        };

        if (kind is null)
        {
            Console.Error.WriteLine($"unknown target '{target}'; expected postgres or clickhouse");
            return null;
        }

        var variable = kind == TargetKind.Postgres
            ? "SQM_POSTGRES_CONNECTION"
            : "SQM_CLICKHOUSE_CONNECTION";

        connection ??= Environment.GetEnvironmentVariable(variable);

        if (string.IsNullOrWhiteSpace(connection))
        {
            Console.Error.WriteLine(
                $"no connection string: pass --connection or set {variable}");
            return null;
        }

        return new Options(
            kind.Value,
            Path.GetFullPath(directory, Directory.GetCurrentDirectory()),
            connection,
            dryRun);
    }
}
