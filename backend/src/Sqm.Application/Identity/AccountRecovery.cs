namespace Sqm.Application.Identity;

/// <summary>Gets a person back into their own account.</summary>
/// <remarks>
/// <para>
/// The whole design is one rule: nothing a caller can observe may depend on whether an account
/// exists. <c>POST recovery/start</c> only places a request on <see cref="IAccountOutbox"/> and
/// answers, so <see cref="SendCodeAsync"/> runs after the response has gone, and its database
/// reads, its hash and its call to the gateway cannot be timed. <see cref="CompleteAsync"/> does one
/// hash verification for a good code, a bad code and no account at all, and gives one answer.
/// </para>
/// <para>
/// A reset does not sign anyone in. The person then signs in with the new password, and two-step
/// sign-in applies as it always does, so recovery cannot be used to step around the second factor.
/// </para>
/// </remarks>
public interface IAccountRecovery
{
    /// <summary>Sends a code to the account's proved contact, if there is an account and a contact.</summary>
    /// <param name="identifier">A username, or a proved e-mail address or phone number.</param>
    /// <param name="channel">The channel the person chose.</param>
    /// <param name="context">Where the request came from.</param>
    /// <param name="ct">Cancellation.</param>
    Task SendCodeAsync(
        string identifier, ContactChannel channel, AuthenticationContext context, CancellationToken ct);

    /// <summary>Tells an account's owner, on every proved contact, that something happened.</summary>
    Task SendNoticeAsync(long userId, AccountNotice notice, CancellationToken ct);

    /// <summary>Sets a new password, given a live code.</summary>
    /// <returns>
    /// <see langword="false"/> for every failure: no account, no code, wrong code, expired, void.
    /// The caller must not be able to tell them apart.
    /// </returns>
    /// <exception cref="PasswordPolicyException">The new password breaks the rules.</exception>
    Task<bool> CompleteAsync(
        string identifier, string code, string newPassword, AuthenticationContext context,
        CancellationToken ct);
}

/// <summary>Proves that a person controls the contact on their profile.</summary>
/// <remarks>
/// Asks for the password before sending, so that a session left open on a desk cannot point
/// recovery at someone else's phone.
/// </remarks>
public interface IContactVerification
{
    /// <summary>Sends a code to the contact on the signed-in user's profile.</summary>
    Task<ContactCodeOutcome> SendCodeAsync(
        long userId, ContactChannel channel, string currentPassword, AuthenticationContext context,
        CancellationToken ct);

    /// <summary>Marks the contact as proved, given the code sent to it.</summary>
    Task<ContactConfirmOutcome> ConfirmAsync(
        long userId, ContactChannel channel, string code, AuthenticationContext context,
        CancellationToken ct);
}

/// <summary>What became of a request to send a verification code.</summary>
public enum ContactCodeOutcome
{
    /// <summary>On its way.</summary>
    Sent,

    /// <summary>The password given was not this account's.</summary>
    WrongPassword,

    /// <summary>The profile has no usable address or number for this channel.</summary>
    NoContact,

    /// <summary>It is already proved.</summary>
    AlreadyVerified,

    /// <summary>Another code went out too recently, or too many have gone this hour.</summary>
    TooSoon,

    /// <summary>The mail server or gateway refused.</summary>
    DeliveryFailed,
}

/// <summary>What became of a verification code.</summary>
public enum ContactConfirmOutcome
{
    /// <summary>The contact is proved.</summary>
    Verified,

    /// <summary>Wrong, expired, void, or sent to a contact that has since changed.</summary>
    InvalidCode,

    /// <summary>Another account has already proved this contact.</summary>
    InUse,
}

/// <summary>Work to be done after the response has gone.</summary>
public abstract record AccountWork;

/// <summary>Somebody asked for a recovery code.</summary>
public sealed record RecoveryCodeWork(
    string Identifier, ContactChannel Channel, AuthenticationContext Context) : AccountWork;

/// <summary>An account's owner should be told something.</summary>
public sealed record AccountNoticeWork(long UserId, AccountNotice Notice) : AccountWork;

/// <summary>A queue of <see cref="AccountWork"/>, drained in the background.</summary>
/// <remarks>
/// In memory, because what it carries is worth nothing after a restart: a person whose request was
/// lost asks again. A queue in the database would survive that, and would turn every anonymous
/// request into a write.
/// </remarks>
public interface IAccountOutbox
{
    /// <summary>Queues the work.</summary>
    /// <returns><see langword="false"/> when the queue is full and the work was dropped.</returns>
    bool TryEnqueue(AccountWork work);
}
