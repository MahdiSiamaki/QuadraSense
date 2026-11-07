using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Sqm.Application.Identity;

/// <summary>Two-step sign-in with an authenticator app.</summary>
/// <remarks>
/// <para>
/// An authenticator app and not a code by SMS or e-mail, for two reasons. It needs no gateway, on
/// a network that has none. And the phone and mailbox that could receive a code are the ones that
/// can already reset the password, so a code sent there would not be a second factor at all:
/// whoever holds the phone would hold both. ADR-022.
/// </para>
/// <para>
/// Between the two steps there is no session, only a challenge: proof that somebody knew the
/// password a moment ago. Every failure of the second step counts against the account's lockout,
/// so guessing a code costs the same as guessing a password.
/// </para>
/// </remarks>
public interface ITwoStepService
{
    /// <summary>Whether this deployment can offer it, which needs a key in configuration.</summary>
    bool IsAvailable { get; }

    /// <summary>Records that the password step succeeded, and returns the ticket for the next.</summary>
    Task<TwoStepChallenge> BeginChallengeAsync(long userId, bool remember, CancellationToken ct);

    /// <summary>Takes a code from the authenticator, or a recovery code.</summary>
    Task<TwoStepResult> CompleteChallengeAsync(
        string ticket, string code, AuthenticationContext context, CancellationToken ct);

    /// <summary>Where this person stands.</summary>
    Task<TwoStepStatus> GetStatusAsync(long userId, CancellationToken ct);

    /// <summary>Creates a new secret to put in an authenticator. Nothing changes until it is confirmed.</summary>
    /// <returns><see langword="null"/> when the password is wrong.</returns>
    /// <exception cref="TwoStepRefusedException">It is already on, or not available here.</exception>
    Task<TwoStepSetup?> BeginSetupAsync(
        long userId, string currentPassword, AuthenticationContext context, CancellationToken ct);

    /// <summary>Turns it on, given a code proving the authenticator holds the secret.</summary>
    /// <returns>The recovery codes, shown once; <see langword="null"/> when the code is wrong.</returns>
    Task<IReadOnlyList<string>?> ConfirmSetupAsync(
        long userId, string code, Guid currentSessionId, AuthenticationContext context,
        CancellationToken ct);

    /// <summary>Replaces the recovery codes.</summary>
    /// <returns>The new codes, shown once; <see langword="null"/> when the password is wrong.</returns>
    Task<IReadOnlyList<string>?> RegenerateRecoveryCodesAsync(
        long userId, string currentPassword, AuthenticationContext context, CancellationToken ct);

    /// <summary>Turns it off, given the password and a code.</summary>
    /// <remarks>Both, so that neither a stolen password nor an open session is enough.</remarks>
    Task<bool> DisableAsync(
        long userId, string currentPassword, string code, AuthenticationContext context,
        CancellationToken ct);

    /// <summary>An administrator turns it off for someone who has lost their authenticator.</summary>
    /// <remarks>Ends every session of that account.</remarks>
    Task<bool> ResetAsync(
        long userId, string actor, AuthenticationContext context, CancellationToken ct);
}

/// <summary>Thrown when two-step sign-in cannot be set up as asked.</summary>
public sealed class TwoStepRefusedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TwoStepRefusedException() : base("Two-step sign-in cannot be set up.")
    {
    }

    /// <summary>Creates the exception with what to show the person.</summary>
    public TwoStepRefusedException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with what to show the person, and its cause.</summary>
    public TwoStepRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The ticket that carries a sign-in from the password to the second step.</summary>
public sealed record TwoStepChallenge(string Ticket, DateTimeOffset ExpiresAt);

/// <summary>A new secret, for the person's authenticator.</summary>
/// <param name="Secret">Base32, for typing in by hand.</param>
/// <param name="Uri">The <c>otpauth://</c> address a QR code carries.</param>
public sealed record TwoStepSetup(string Secret, string Uri);

/// <summary>Where a person stands.</summary>
/// <param name="Available">This deployment can offer it.</param>
/// <param name="Enabled">It is on for this account.</param>
/// <param name="EnabledAt">Since when.</param>
/// <param name="RecoveryCodesLeft">Codes not yet used.</param>
/// <param name="Required">A role this person holds demands it.</param>
public sealed record TwoStepStatus(
    bool Available, bool Enabled, DateTimeOffset? EnabledAt, int RecoveryCodesLeft, bool Required);

/// <summary>How a second step ended.</summary>
public enum TwoStepResultStatus
{
    /// <summary>Signed in.</summary>
    Success,

    /// <summary>The code was wrong; the same ticket may try again.</summary>
    InvalidCode,

    /// <summary>The ticket is gone: too old, used, or out of attempts. Start again.</summary>
    ChallengeExpired,

    /// <summary>Too many failures on this account.</summary>
    AccountLocked,
}

/// <summary>The outcome of a second step.</summary>
public sealed record TwoStepResult(
    TwoStepResultStatus Status,
    long? UserId = null,
    bool Remember = false,
    DateTimeOffset? LockedUntil = null);

/// <summary>Time-based one-time passwords, RFC 6238, as authenticator apps compute them.</summary>
/// <remarks>
/// SHA-1, six digits, thirty seconds: the parameters every authenticator app honours. Some ignore
/// any others without saying so, and then show a code that never works. HMAC-SHA-1 has no known
/// weakness in this use, which needs the keyed function to be unpredictable, not collision-free.
/// </remarks>
public static class Totp
{
    private const int StepSeconds = 30;
    private const int Modulus = 1_000_000;

    /// <summary>Length of a secret in bytes, the size RFC 4226 recommends.</summary>
    public const int SecretLength = 20;

    /// <summary>The time step a moment falls in.</summary>
    public static long StepAt(DateTimeOffset moment) => moment.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>The code for one time step.</summary>
    public static string Code(ReadOnlySpan<byte> secret, long step)
    {
        Span<byte> counter = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);

        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
#pragma warning disable CA5350 // RFC 6238's default, and the only one every authenticator honours.
        HMACSHA1.HashData(secret, counter, hash);
#pragma warning restore CA5350

        var offset = hash[^1] & 0x0F;
        var value = BinaryPrimitives.ReadInt32BigEndian(hash[offset..]) & 0x7FFFFFFF;

        return (value % Modulus).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The step a code belongs to, trying the current one and one each side, or
    /// <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// One step each way forgives a phone's clock being thirty seconds out, and a code typed as it
    /// changes. The window is what makes a code guessable three ways rather than one, which is why
    /// it stays this narrow.
    /// </remarks>
    public static long? Match(ReadOnlySpan<byte> secret, string code, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(code);

        var offered = Encoding.ASCII.GetBytes(code);
        var current = StepAt(now);

        for (var step = current - 1; step <= current + 1; step++)
        {
            var expected = Encoding.ASCII.GetBytes(Code(secret, step));
            if (CryptographicOperations.FixedTimeEquals(expected, offered))
            {
                return step;
            }
        }

        return null;
    }

    /// <summary>The address an authenticator app reads from a QR code.</summary>
    public static string Uri(string issuer, string account, string base32Secret)
    {
        var label = System.Uri.EscapeDataString($"{issuer}:{account}");
        var name = System.Uri.EscapeDataString(issuer);

        return $"otpauth://totp/{label}?secret={base32Secret}&issuer={name}"
            + $"&algorithm=SHA1&digits=6&period={StepSeconds}";
    }
}

/// <summary>Base32 as RFC 4648 has it, the form an authenticator app takes a secret in.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Encodes without padding.</summary>
    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        var text = new StringBuilder((bytes.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;

        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;

            while (bits >= 5)
            {
                text.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }

            buffer &= (1 << bits) - 1;
        }

        if (bits > 0)
        {
            text.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return text.ToString();
    }
}

/// <summary>Single-use codes for the day the authenticator is lost.</summary>
/// <remarks>
/// Sixteen characters of Crockford's alphabet, which leaves out I, L, O and U so that a code copied
/// from paper cannot be misread: 80 random bits. That is why a plain SHA-256 of one is enough, the
/// same reasoning as a session token, and why a code can be found by its hash.
/// </remarks>
public static class RecoveryCodes
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int Length = 16;
    private const int GroupLength = 4;

    /// <summary>A new code, grouped for reading: <c>7K2M-9QXT-4HNP-W3RD</c>.</summary>
    public static string Generate()
    {
        var chars = RandomNumberGenerator.GetItems<char>(Alphabet, Length);
        return string.Join('-', chars.Chunk(GroupLength).Select(g => new string(g)));
    }

    /// <summary>
    /// The characters that count, whatever way the person typed them, or <see langword="null"/>
    /// if this cannot be a recovery code.
    /// </summary>
    public static string? Normalise(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var text = new string([.. input
            .Where(c => !char.IsWhiteSpace(c) && c != '-')
            .Select(c => char.ToUpperInvariant(c) switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                var upper => upper,
            })]);

        return text.Length == Length && text.All(c => Alphabet.Contains(c, StringComparison.Ordinal))
            ? text
            : null;
    }

    /// <summary>What is stored.</summary>
    public static byte[] Hash(string normalised)
    {
        ArgumentNullException.ThrowIfNull(normalised);
        return SHA256.HashData(Encoding.ASCII.GetBytes(normalised));
    }
}
