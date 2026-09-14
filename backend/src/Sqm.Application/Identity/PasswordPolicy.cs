namespace Sqm.Application.Identity;

/// <summary>Checks a proposed password against the configured rules.</summary>
/// <remarks>
/// <para>
/// One implementation, used by the API to reject a bad password with a useful message and by the
/// directory to refuse one that reached it any other way. Two copies of a rule is two rules.
/// </para>
/// <para>
/// The rules follow NIST SP 800-63B, which is worth stating because they look lax next to the
/// familiar ones: length is the requirement that matters, character-class rules measurably
/// reduce entropy by pushing everyone towards <c>Password1!</c>, and scheduled expiry produces
/// <c>Summer2026!</c> followed by <c>Autumn2026!</c>. Nothing here forces a rotation.
/// </para>
/// </remarks>
public static class PasswordPolicy
{
    /// <summary>Validates a password.</summary>
    /// <returns>A message to show the user, or <see langword="null"/> when it is acceptable.</returns>
    public static string? Validate(
        string? password, PasswordPolicyOptions options, string? username, string? displayName)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrEmpty(password))
        {
            return "Enter a password.";
        }

        if (password.Length < options.MinimumLength)
        {
            return $"Use at least {options.MinimumLength} characters.";
        }

        if (password.Length > options.MaximumLength)
        {
            return $"Use at most {options.MaximumLength} characters.";
        }

        // Trailing or leading whitespace is almost always a paste accident, and it produces a
        // password the person cannot retype. Inner spaces are fine and passphrases are encouraged.
        if (password.Trim().Length != password.Length)
        {
            return "Remove the space at the start or end.";
        }

        if (!options.RejectContainingUsername)
        {
            return null;
        }

        if (Contains(password, username) || Contains(password, displayName))
        {
            return "Do not include your name or username in your password.";
        }

        return null;
    }

    /// <summary>
    /// Whether the password contains a meaningful part of the given name.
    /// </summary>
    /// <remarks>
    /// Words of three characters or fewer are ignored. Without that, a display name of "Ali Reza"
    /// rejects any password containing "ali" - including "quality" and "generalise" - which
    /// teaches people that the rule is arbitrary and to work around it.
    /// </remarks>
    private static bool Contains(string password, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        foreach (var part in name.Split([' ', '.', '-', '_', '@'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length > 3 && password.Contains(part, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Thrown when a password does not meet the policy.</summary>
public sealed class PasswordPolicyException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public PasswordPolicyException() : base("That password does not meet the policy.")
    {
    }

    /// <summary>Creates the exception.</summary>
    public PasswordPolicyException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public PasswordPolicyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
