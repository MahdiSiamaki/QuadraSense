namespace Sqm.Application.Identity;

/// <summary>Hashes and verifies passwords.</summary>
/// <remarks>
/// The contract is deliberately narrow, and the hash it produces is self-describing (a PHC
/// string carrying the algorithm and its parameters). That is what makes
/// <see cref="PasswordVerification.SuccessNeedsRehash"/> possible: work factors have to rise over
/// the life of a system, and a scheme that cannot raise them without invalidating every existing
/// password will simply never raise them.
/// </remarks>
public interface IPasswordHasher
{
    /// <summary>Hashes a password with the current parameters and a fresh random salt.</summary>
    /// <returns>A PHC string, safe to store as-is.</returns>
    string Hash(string password);

    /// <summary>Verifies a password against a stored hash.</summary>
    /// <remarks>
    /// Must not throw on a malformed or unrecognised stored hash - it returns
    /// <see cref="PasswordVerification.Failed"/>. A corrupt row should fail one login, not take
    /// down the login endpoint for everyone.
    /// </remarks>
    PasswordVerification Verify(string password, string storedHash);

    /// <summary>
    /// Burns roughly the same time as a real verification, for a user that does not exist.
    /// </summary>
    /// <remarks>
    /// Without this, a missing username returns in microseconds and a wrong password takes as
    /// long as an Argon2 hash. That difference is measurable over the network and turns the login
    /// endpoint into a username oracle - which then makes a password-spray attack far cheaper,
    /// because the attacker only sprays accounts that exist.
    /// </remarks>
    void BurnTime();
}

/// <summary>The three outcomes of verifying a password.</summary>
public enum PasswordVerification
{
    /// <summary>Wrong password, or a stored hash this hasher cannot read.</summary>
    Failed,

    /// <summary>Correct, and stored with the current parameters.</summary>
    Success,

    /// <summary>
    /// Correct, but stored with weaker parameters than the current ones. The caller should
    /// rehash and store while it has the plaintext - the only moment it ever will.
    /// </summary>
    SuccessNeedsRehash,
}
