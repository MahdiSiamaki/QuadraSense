using System.Globalization;

namespace Sqm.Application.Identity;

/// <summary>The two ways a message reaches a person.</summary>
public enum ContactChannel
{
    /// <summary>A mail server.</summary>
    Email,

    /// <summary>An SMS gateway.</summary>
    Sms,
}

/// <summary>What is sent. SMS uses the text alone.</summary>
public sealed record OutgoingMessage(string Subject, string Text);

/// <summary>Sends a message to a contact.</summary>
/// <remarks>
/// The seam for a mail server and an SMS gateway, neither of which this system controls. A channel
/// with nothing configured says so through <see cref="IsAvailable"/>, and the sign-in page does not
/// offer it.
/// </remarks>
public interface IContactDelivery
{
    /// <summary>Whether this channel can send anything on this deployment.</summary>
    bool IsAvailable(ContactChannel channel);

    /// <summary>Sends one message.</summary>
    /// <exception cref="ContactDeliveryException">It could not be sent.</exception>
    Task SendAsync(
        ContactChannel channel, string destination, OutgoingMessage message, CancellationToken ct);
}

/// <summary>A message could not be sent. The message says why, for the log and never the client.</summary>
public sealed class ContactDeliveryException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ContactDeliveryException() : base("The message could not be sent.")
    {
    }

    /// <summary>Creates the exception with a reason.</summary>
    public ContactDeliveryException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a reason and its cause.</summary>
    public ContactDeliveryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Something an account's owner should hear about, whoever did it.</summary>
public enum AccountNotice
{
    /// <summary>The password was changed through recovery.</summary>
    PasswordReset,

    /// <summary>Two-step sign-in was turned on.</summary>
    TwoStepOn,

    /// <summary>Two-step sign-in was turned off, by the owner or an administrator.</summary>
    TwoStepOff,
}

/// <summary>The words of every message the system sends. English, by product decision.</summary>
/// <remarks>
/// Short enough for one SMS part where a code is involved, because every part is paid for. A code
/// comes first in the text, so a phone's notification shows it.
/// </remarks>
public static class AccountMessages
{
    private const string Product = "Quadrasense";

    /// <summary>A code to reset a password.</summary>
    public static OutgoingMessage ResetCode(string code, string username, TimeSpan lifetime) => new(
        $"{Product} password reset code",
        $"{code} is your {Product} password reset code for {username}. "
        + $"It expires in {Minutes(lifetime)} minutes. If you did not ask for it, ignore this "
        + "message: your password has not changed.");

    /// <summary>A code to prove a contact.</summary>
    public static OutgoingMessage VerifyCode(string code, TimeSpan lifetime) => new(
        $"{Product} verification code",
        $"{code} is your {Product} verification code. It expires in {Minutes(lifetime)} minutes.");

    /// <summary>Tells the owner something happened to the account.</summary>
    public static OutgoingMessage Notice(AccountNotice notice, string username) => notice switch
    {
        AccountNotice.PasswordReset => new(
            $"Your {Product} password was changed",
            $"The password of {Product} account {username} was just reset and every device was "
            + "signed out. If this was not you, contact your administrator now."),
        AccountNotice.TwoStepOn => new(
            $"Two-step sign-in is on for your {Product} account",
            $"Two-step sign-in was turned on for {Product} account {username}. If this was not "
            + "you, contact your administrator now."),
        AccountNotice.TwoStepOff => new(
            $"Two-step sign-in is off for your {Product} account",
            $"Two-step sign-in was turned off for {Product} account {username}. If this was not "
            + "you, contact your administrator now."),
        _ => throw new ArgumentOutOfRangeException(nameof(notice), notice, null),
    };

    private static string Minutes(TimeSpan lifetime) =>
        ((int)Math.Ceiling(lifetime.TotalMinutes)).ToString(CultureInfo.InvariantCulture);
}

/// <summary>Numbers as people write them, and the one form they are stored in.</summary>
/// <remarks>
/// The same number reaches this system as <c>0912 345 6789</c>, <c>+98 912 345 6789</c>,
/// <c>00989123456789</c> or with Persian digits from a phone's keyboard. A proved number has to be
/// found again from whichever of those the person types, so each becomes <c>+989123456789</c>.
/// </remarks>
public static class PhoneNumber
{
    private const int MinimumDigits = 8;
    private const int MaximumDigits = 15;

    /// <summary>The international form, or <see langword="null"/> if this is not a phone number.</summary>
    public static string? Normalise(string? raw, string defaultCountryCode)
    {
        ArgumentNullException.ThrowIfNull(defaultCountryCode);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = Digits.ToAscii(raw).Trim();
        var international = text.StartsWith('+');

        if (text.Any(c => !char.IsAsciiDigit(c) && !"+ -().".Contains(c, StringComparison.Ordinal)))
        {
            return null;
        }

        var digits = new string([.. text.Where(char.IsAsciiDigit)]);

        if (!international && digits.StartsWith("00", StringComparison.Ordinal))
        {
            digits = digits[2..];
        }
        else if (!international && digits.StartsWith('0'))
        {
            digits = defaultCountryCode + digits[1..];
        }
        else if (!international && !digits.StartsWith(defaultCountryCode, StringComparison.Ordinal))
        {
            digits = defaultCountryCode + digits;
        }

        return digits.Length is < MinimumDigits or > MaximumDigits ? null : "+" + digits;
    }
}

/// <summary>Digits from any script, as ASCII.</summary>
public static class Digits
{
    /// <summary>
    /// Turns Persian, Arabic and other decimal digits into 0-9 and leaves everything else alone.
    /// </summary>
    /// <remarks>
    /// The interface is English, but a phone's keyboard is not always: a code typed in Persian
    /// digits is the right code, and refusing it would spend one of its five attempts.
    /// </remarks>
    public static string ToAscii(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = char.IsDigit(c) && !char.IsAsciiDigit(c)
                    ? (char)('0' + (int)char.GetNumericValue(c))
                    : c;
            }
        });
    }
}
