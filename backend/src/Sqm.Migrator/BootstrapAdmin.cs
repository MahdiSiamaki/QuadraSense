using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Sqm.Application.Identity;
using Sqm.Infrastructure.DataImport;
using Sqm.Infrastructure.Identity;

namespace Sqm.Migrator;

/// <summary>Creates the first administrator, once, on a fresh database.</summary>
/// <remarks>
/// <para>
/// <b>Why this lives in the migrator.</b> It is a one-time deploy step run against a database
/// that has just been created, which is exactly what this tool already is. The alternatives were
/// worse: a seeded account with a known password is a backdoor that outlives every intention to
/// change it, and an API that creates an administrator when none exists is an unauthenticated
/// privilege-escalation endpoint that only looks safe while the check is correct.
/// </para>
/// <para>
/// <b>The password is never an argument.</b> Command-line arguments appear in shell history, in
/// <c>ps</c> output for every user on the machine, and in the process list a container runtime
/// exposes. It is read from stdin, or from <c>SQM_BOOTSTRAP_PASSWORD</c> for an automated deploy
/// where a secret manager supplies the environment.
/// </para>
/// <para>
/// It refuses to run if any active user can already administer the system. Bootstrapping is for
/// an empty system; on a live one it would be a way to mint an administrator without any existing
/// administrator agreeing.
/// </para>
/// </remarks>
internal static class BootstrapAdmin
{
    public const string Usage = """
        usage: Sqm.Migrator --create-admin --username <name> [--display-name <name>]
                            [--connection <connection-string>]

        Creates the first administrator on a database that has no working administrator yet.
        Refuses to run otherwise.

        The password is read from stdin, or from SQM_BOOTSTRAP_PASSWORD. It is never taken as a
        command-line argument, because arguments are visible in shell history and process lists.
        """;

    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        string? username = null;
        string? displayName = null;
        string? connection = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--username" when i + 1 < args.Length:
                    username = args[++i];
                    break;
                case "--display-name" when i + 1 < args.Length:
                    displayName = args[++i];
                    break;
                case "--connection" when i + 1 < args.Length:
                    connection = args[++i];
                    break;
                default:
                    break;
            }
        }

        connection ??= Environment.GetEnvironmentVariable("SQM_POSTGRES_CONNECTION");

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(connection))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        // Fully qualified: this assembly has its own `Options` record for migration arguments.
        var options = Microsoft.Extensions.Options.Options.Create(
            new PostgresOptions { ConnectionString = connection });
        var authOptions = Microsoft.Extensions.Options.Options.Create(new AuthOptions());

        await using var dataSource = new IdentityDataSource(options);

        var directory = new PostgresUserDirectory(
            dataSource,
            new Argon2PasswordHasher(authOptions),
            authOptions,
            options,
            NullLogger<PostgresUserDirectory>.Instance);

        if (await directory.AdministrationIsReachableAsync(ct).ConfigureAwait(false))
        {
            Console.Error.WriteLine(
                "refusing: this database already has an active user who can manage users and "
                + "roles.");
            Console.Error.WriteLine(
                "Bootstrapping is for an empty system. Ask that administrator to create the "
                + "account.");
            return 1;
        }

        var password = ReadPassword();
        if (password is null)
        {
            Console.Error.WriteLine("no password supplied");
            return 2;
        }

        var problem = PasswordPolicy.Validate(
            password, authOptions.Value.Policy, username, displayName);

        if (problem is not null)
        {
            Console.Error.WriteLine($"password rejected: {problem}");
            return 2;
        }

        try
        {
            var id = await directory.CreateAsync(
                new NewUser(
                    username,
                    string.IsNullOrWhiteSpace(displayName) ? username : displayName,
                    Email: null, JobTitle: null, Phone: null,
                    RoleCodes: ["administrator"],
                    // False: there is nobody to reset it if the first sign-in goes wrong, and the
                    // person running this command chose the password seconds ago.
                    MustChangePassword: false),
                password,
                actor: "bootstrap",
                new AuthenticationContext(Ip: null, UserAgent: "Sqm.Migrator", CorrelationId: null),
                ct).ConfigureAwait(false);

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"created administrator '{username}' (id {id})"));
            Console.WriteLine("Sign in and create the other accounts from the Users page.");
            return 0;
        }
        catch (DuplicateUsernameException)
        {
            // Reachable: a deactivated or non-administrator account can hold the name while
            // AdministrationIsReachableAsync still reports false.
            Console.Error.WriteLine($"'{username}' already exists.");
            return 1;
        }
        catch (PostgresException ex)
        {
            Console.Error.WriteLine($"database refused the account: {ex.MessageText}");
            return 1;
        }
    }

    /// <summary>Reads the password from the environment, or from stdin without echoing it.</summary>
    private static string? ReadPassword()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("SQM_BOOTSTRAP_PASSWORD");
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            return fromEnvironment;
        }

        if (Console.IsInputRedirected)
        {
            // Piped in. Trimmed because `echo` adds a newline and a password with a trailing
            // newline is one nobody can type back.
            return Console.In.ReadToEnd().TrimEnd('\r', '\n');
        }

        Console.Write("Password: ");
        var builder = new StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                builder.Append(key.KeyChar);
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }
}
