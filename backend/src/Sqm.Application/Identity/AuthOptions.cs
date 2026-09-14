namespace Sqm.Application.Identity;

/// <summary>Everything tunable about authentication, in one place.</summary>
/// <remarks>
/// The defaults here are the ones the product owner's answers imply: a strictly internal
/// deployment, local accounts, no MFA. They are stated as values rather than buried in code so
/// that a different deployment is a configuration change, and so that a reviewer can see the
/// whole policy without reading the implementation.
/// </remarks>
public sealed class AuthOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Auth";

    /// <summary>Password hashing parameters.</summary>
    public PasswordOptions Password { get; init; } = new();

    /// <summary>Session lifetime and cookie behaviour.</summary>
    public SessionOptions Session { get; init; } = new();

    /// <summary>Lockout policy.</summary>
    public LockoutOptions Lockout { get; init; } = new();

    /// <summary>Password strength rules applied when a password is set.</summary>
    public PasswordPolicyOptions Policy { get; init; } = new();
}

/// <summary>Argon2id parameters.</summary>
/// <remarks>
/// <para>
/// Argon2id, not PBKDF2 or bcrypt: it is memory-hard, which is what makes a GPU or ASIC attack
/// expensive rather than merely slow. OWASP lists it first among password storage algorithms.
/// </para>
/// <para>
/// The defaults are OWASP's second configuration (m=19 MiB, t=2, p=1), chosen over the first
/// (m=46 MiB, t=1) because the cost of this system's login path is dominated by memory bandwidth
/// on a shared container, and measured cost matters more than a recommendation's ordering. The
/// measurement is recorded in ADR-006; the numbers were taken on the development machine and
/// should be re-taken on production hardware.
/// </para>
/// <para>
/// Every hash records the parameters that produced it, so raising these later does not invalidate
/// existing passwords - they are rehashed on the owner's next successful sign-in.
/// </para>
/// </remarks>
public sealed class PasswordOptions
{
    /// <summary>Memory cost in kibibytes.</summary>
    public int MemoryKib { get; init; } = 19_456;

    /// <summary>Time cost: passes over memory.</summary>
    public int Iterations { get; init; } = 2;

    /// <summary>Lanes. One, because the login path is not the place to spend cores.</summary>
    public int Parallelism { get; init; } = 1;

    /// <summary>Derived key length in bytes.</summary>
    public int HashLength { get; init; } = 32;

    /// <summary>Salt length in bytes. 16 is the PHC recommendation.</summary>
    public int SaltLength { get; init; } = 16;
}

/// <summary>Session lifetime and cookie behaviour.</summary>
public sealed class SessionOptions
{
    /// <summary>
    /// How long a session survives without use.
    /// </summary>
    /// <remarks>
    /// Eight hours covers a working day with a lunch break, on an internal network where the
    /// threat is an unattended desk rather than a stolen token. On an internet-facing deployment
    /// this should drop to 30-60 minutes.
    /// </remarks>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromHours(8);

    /// <summary>
    /// How long a session may live however actively it is used.
    /// </summary>
    /// <remarks>
    /// A ceiling that no amount of activity extends, so a session cannot become permanent. It
    /// also bounds how long a stolen cookie is worth anything, which the idle timeout alone does
    /// not - an attacker using the cookie keeps refreshing the idle window.
    /// </remarks>
    public TimeSpan AbsoluteTimeout { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How stale <c>last_seen_at</c> is allowed to get before a request writes it.
    /// </summary>
    /// <remarks>
    /// Without this, every GET becomes a write transaction. With it, the idle window is accurate
    /// to within this interval, which is the accuracy it needs against an eight-hour timeout.
    /// </remarks>
    public TimeSpan TouchInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Name of the session cookie.</summary>
    /// <remarks>
    /// The <c>__Host-</c> prefix is enforced by the browser: it refuses the cookie unless it is
    /// Secure, has no Domain attribute and has Path=/. That makes it impossible for a sibling
    /// subdomain to set or overwrite this cookie, which is the one attack a plain cookie name
    /// leaves open.
    /// </remarks>
    public string CookieName { get; init; } = "__Host-sqm_session";

    /// <summary>Name of the CSRF cookie, readable by the SPA.</summary>
    public string CsrfCookieName { get; init; } = "__Host-sqm_csrf";

    /// <summary>Header the SPA echoes the CSRF value in.</summary>
    public string CsrfHeaderName { get; init; } = "X-CSRF-Token";

    /// <summary>
    /// Whether to mark cookies Secure. Off only for plain-HTTP local development.
    /// </summary>
    /// <remarks>
    /// When false, the <c>__Host-</c> prefix is dropped too, because a browser would reject the
    /// cookie outright - silently, with no error a developer could act on.
    /// </remarks>
    public bool RequireSecureCookies { get; init; } = true;

    /// <summary>How long ended sessions are kept before the sweeper deletes them.</summary>
    /// <remarks>
    /// They are kept so a user can see "signed out from this device yesterday" on their profile,
    /// which is how someone notices a session they did not start. The audit log keeps the record
    /// permanently either way.
    /// </remarks>
    public TimeSpan RetainEndedSessionsFor { get; init; } = TimeSpan.FromDays(30);
}

/// <summary>Lockout policy for repeated failed sign-ins.</summary>
/// <remarks>
/// Per-account only. Per-IP throttling is the control for an internet-facing deployment, where
/// an attacker sprays one password across many accounts and never trips a per-account counter;
/// on a strictly internal network it mostly locks out an office behind one NAT address. If the
/// deployment posture changes, this is the first thing to revisit.
/// </remarks>
public sealed class LockoutOptions
{
    /// <summary>Consecutive failures before the account locks.</summary>
    public int MaxFailedAttempts { get; init; } = 5;

    /// <summary>Lock duration for the first lockout.</summary>
    public TimeSpan InitialLockout { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Ceiling on the lock duration, which doubles for each further failure past the threshold.
    /// </summary>
    /// <remarks>
    /// A cap, not a compromise: doubling without one produces locks measured in days, which is a
    /// denial of service any colleague can perform on any other by typing a wrong password
    /// twenty times. Fifteen minutes already makes online guessing hopeless.
    /// </remarks>
    public TimeSpan MaxLockout { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>Rules applied when a password is chosen.</summary>
/// <remarks>
/// Follows NIST SP 800-63B: length is the requirement that matters, composition rules are not,
/// and scheduled expiry is actively harmful because it produces Summer2026! followed by
/// Autumn2026!. Nothing here forces a rotation; an administrator can still require a change for
/// a specific account when there is a reason to.
/// </remarks>
public sealed class PasswordPolicyOptions
{
    /// <summary>Minimum length in characters.</summary>
    public int MinimumLength { get; init; } = 12;

    /// <summary>Maximum length, to bound the work a single request can ask for.</summary>
    public int MaximumLength { get; init; } = 256;

    /// <summary>
    /// Reject passwords that contain the username or the display name.
    /// </summary>
    /// <remarks>
    /// The one check worth making beyond length: it is the first thing anyone guessing a
    /// password for a known account tries.
    /// </remarks>
    public bool RejectContainingUsername { get; init; } = true;
}
