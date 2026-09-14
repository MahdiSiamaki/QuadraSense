using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.Extensions.Options;
using Sqm.Application.Identity;

namespace Sqm.Infrastructure.Identity;

/// <summary>
/// Argon2id password hashing, storing a self-describing PHC string.
/// </summary>
/// <remarks>
/// <para>
/// The stored value looks like
/// <c>$argon2id$v=19$m=19456,t=2,p=1$c2FsdHNhbHRzYWx0c2E$aGFzaGhhc2hoYXNoaGFzaA</c>: algorithm,
/// version, parameters, salt, hash. Everything needed to verify is in the string, which is what
/// allows the work factor to be raised later without a migration and without locking anyone out.
/// A bare hex hash plus a separate salt column cannot do that, and systems that store one
/// generally never raise their work factor at all.
/// </para>
/// <para>
/// Verification is constant-time in the comparison and deliberately equal-cost in the miss case -
/// see <see cref="BurnTime"/>. Both matter more than they look: an attacker who can tell "no such
/// user" from "wrong password" by timing gets a free list of valid usernames, and a
/// non-constant-time compare leaks the hash a byte at a time.
/// </para>
/// </remarks>
public sealed class Argon2PasswordHasher : IPasswordHasher
{
    private const string Algorithm = "argon2id";
    private const int Version = 19;

    private readonly PasswordOptions _options;

    /// <summary>Creates the hasher.</summary>
    public Argon2PasswordHasher(IOptions<AuthOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value.Password;
    }

    /// <inheritdoc />
    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var salt = RandomNumberGenerator.GetBytes(_options.SaltLength);
        var hash = Derive(password, salt, _options.MemoryKib, _options.Iterations,
            _options.Parallelism, _options.HashLength);

        return string.Create(CultureInfo.InvariantCulture,
            $"${Algorithm}$v={Version}$m={_options.MemoryKib},t={_options.Iterations},"
            + $"p={_options.Parallelism}${ToB64(salt)}${ToB64(hash)}");
    }

    /// <inheritdoc />
    public PasswordVerification Verify(string password, string storedHash)
    {
        ArgumentNullException.ThrowIfNull(password);

        // A malformed row fails one login. It does not throw, because an exception here is a 500
        // on the login endpoint for every user whose row happens to be next in the log.
        if (!TryParse(storedHash, out var parsed))
        {
            BurnTime();
            return PasswordVerification.Failed;
        }

        var computed = Derive(password, parsed.Salt, parsed.MemoryKib, parsed.Iterations,
            parsed.Parallelism, parsed.Hash.Length);

        if (!CryptographicOperations.FixedTimeEquals(computed, parsed.Hash))
        {
            return PasswordVerification.Failed;
        }

        // Only ever upgrades. A hash stored with stronger parameters than the current
        // configuration is left alone: rehashing it would quietly weaken a password because
        // someone lowered a setting, which is precisely the change that should not take effect
        // retroactively.
        var weaker = parsed.MemoryKib < _options.MemoryKib
                     || parsed.Iterations < _options.Iterations
                     || parsed.Hash.Length < _options.HashLength;

        return weaker ? PasswordVerification.SuccessNeedsRehash : PasswordVerification.Success;
    }

    /// <inheritdoc />
    public void BurnTime()
    {
        // A real derivation against a throwaway salt. Not a Thread.Sleep: the point is to spend
        // the same kind of work, and a sleep is both distinguishable under load and a way to
        // pin request threads doing nothing.
        var salt = RandomNumberGenerator.GetBytes(_options.SaltLength);
        var burned = Derive("\0burn\0", salt, _options.MemoryKib, _options.Iterations,
            _options.Parallelism, _options.HashLength);
        CryptographicOperations.ZeroMemory(burned);
    }

    private static byte[] Derive(
        string password, byte[] salt, int memoryKib, int iterations, int parallelism, int length)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };

        return argon2.GetBytes(length);
    }

    /// <summary>Base64url without padding, as the PHC string format specifies.</summary>
    private static string ToB64(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FromB64(string value, [NotNullWhen(true)] out byte[]? bytes)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };

        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            bytes = null;
            return false;
        }
    }

    private static bool TryParse(string? stored, [NotNullWhen(true)] out ParsedHash? parsed)
    {
        parsed = null;
        if (string.IsNullOrEmpty(stored))
        {
            return false;
        }

        // $argon2id$v=19$m=19456,t=2,p=1$<salt>$<hash>
        var parts = stored.Split('$');
        if (parts.Length != 6 || parts[0].Length != 0)
        {
            return false;
        }

        if (!string.Equals(parts[1], Algorithm, StringComparison.Ordinal))
        {
            return false;
        }

        if (!parts[2].StartsWith("v=", StringComparison.Ordinal)
            || !int.TryParse(parts[2].AsSpan(2), CultureInfo.InvariantCulture, out var version)
            || version != Version)
        {
            return false;
        }

        int memory = 0, iterations = 0, parallelism = 0;
        foreach (var setting in parts[3].Split(','))
        {
            if (setting.Length < 3 || setting[1] != '=')
            {
                return false;
            }

            if (!int.TryParse(setting.AsSpan(2), CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            switch (setting[0])
            {
                case 'm': memory = value; break;
                case 't': iterations = value; break;
                case 'p': parallelism = value; break;
                default: return false;
            }
        }

        if (memory <= 0 || iterations <= 0 || parallelism <= 0)
        {
            return false;
        }

        if (!FromB64(parts[4], out var salt) || !FromB64(parts[5], out var hash))
        {
            return false;
        }

        if (salt.Length == 0 || hash.Length == 0)
        {
            return false;
        }

        parsed = new ParsedHash(memory, iterations, parallelism, salt, hash);
        return true;
    }

    private sealed record ParsedHash(
        int MemoryKib, int Iterations, int Parallelism, byte[] Salt, byte[] Hash);
}
