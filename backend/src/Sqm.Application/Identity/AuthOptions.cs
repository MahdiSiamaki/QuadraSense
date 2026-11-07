namespace Sqm.Application.Identity;

/// <summary>Everything tunable about authentication, in one place.</summary>
/// <remarks>
/// The defaults here are the ones the product owner's answers imply: a strictly internal
/// deployment, local accounts, two-step sign-in offered and demanded of nobody until a role asks
/// for it (ADR-022). They are stated as values rather than buried in code so
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
    public SessionSettings Session { get; init; } = new();

    /// <summary>Lockout policy.</summary>
    public LockoutOptions Lockout { get; init; } = new();

    /// <summary>Password strength rules applied when a password is set.</summary>
    public PasswordPolicyOptions Policy { get; init; } = new();

    /// <summary>One-time codes: how long they live and how often they may be sent.</summary>
    public RecoveryOptions Recovery { get; init; } = new();

    /// <summary>The authenticator second step.</summary>
    public TwoStepOptions TwoStep { get; init; } = new();

    /// <summary>How a message reaches a person: mail server, SMS gateway.</summary>
    public DeliveryOptions Delivery { get; init; } = new();
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
/// <remarks>
/// Named Settings rather than Options, unlike its siblings, because
/// <c>Microsoft.AspNetCore.Builder.SessionOptions</c> exists and is in scope wherever ASP.NET's
/// implicit usings are on. Two types with one name, distinguished only by namespace, in the file
/// that decides how sessions work, is a trap worth one inconsistent suffix.
/// </remarks>
public sealed class SessionSettings
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
    /// How long a session started with "remember me" lives, however it is used.
    /// </summary>
    /// <remarks>
    /// One number rather than an idle limit and an absolute limit of its own: a person who ticks
    /// the box is told how long it lasts, and that should be true. It is also the lifetime of both
    /// cookies, so the session cookie cannot outlive the CSRF cookie and block the next sign-in
    /// (see <c>CookieNames.ClearSession</c>). Seven days: a working week, and no more.
    /// </remarks>
    public TimeSpan RememberedLifetime { get; init; } = TimeSpan.FromDays(7);

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

/// <summary>One-time codes: their terms, and how often one may be sent.</summary>
/// <remarks>
/// The first two are the terms of MCI's security red lines for one-time passwords (section 2-3,
/// row 2): six digits, random, void after five failures, expired after ten minutes. Six digits is
/// not a setting, because nothing would ever want it different.
/// </remarks>
public sealed class RecoveryOptions
{
    /// <summary>How long a code works after it is sent.</summary>
    public TimeSpan CodeLifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Wrong guesses after which a code is void, however long it has left.</summary>
    public int MaxCodeAttempts { get; init; } = 5;

    /// <summary>Quiet time between two codes to one account.</summary>
    public TimeSpan ResendCooldown { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Codes one account may be sent in an hour.</summary>
    /// <remarks>
    /// The per-address limiter stops one machine. This stops many machines filling one person's
    /// phone, and it is what bounds an SMS bill, because every code sent is paid for.
    /// </remarks>
    public int MaxSendsPerHour { get; init; } = 5;

    /// <summary>The country a number written the local way belongs to.</summary>
    public string DefaultCountryCode { get; init; } = "98";
}

/// <summary>The authenticator second step.</summary>
public sealed class TwoStepOptions
{
    /// <summary>
    /// Base64 of 32 random bytes, the AES key that protects every authenticator secret.
    /// </summary>
    /// <remarks>
    /// Empty means the feature is off: nothing is offered and no role can demand it, so a
    /// deployment without a key behaves as it did before. It lives in configuration and never in
    /// the database, so a stolen backup does not carry it. Losing it leaves every authenticator
    /// unreadable; recovery codes and an administrator's reset still work.
    /// </remarks>
    public string? SecretKey { get; init; }

    /// <summary>The name an authenticator app shows beside the code.</summary>
    public string Issuer { get; init; } = "Quadrasense";

    /// <summary>How long a person has between a good password and the second step.</summary>
    public TimeSpan ChallengeLifetime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Wrong codes one password entry may be followed by.</summary>
    public int MaxChallengeAttempts { get; init; } = 5;

    /// <summary>How many recovery codes a person is given.</summary>
    public int RecoveryCodeCount { get; init; } = 10;

    /// <summary>Whether a key is configured.</summary>
    public bool IsAvailable => !string.IsNullOrWhiteSpace(SecretKey);
}

/// <summary>How a message reaches a person.</summary>
/// <remarks>
/// A channel with nothing configured is off, and the sign-in page does not offer it. That is the
/// state of a deployment that has been told about neither a mail server nor a gateway, and it is
/// a supported one: an administrator's reset keeps working.
/// </remarks>
public sealed class DeliveryOptions
{
    /// <summary>The mail server.</summary>
    public SmtpDeliveryOptions Email { get; init; } = new();

    /// <summary>The SMS gateway.</summary>
    public SmsGatewayOptions Sms { get; init; } = new();

    /// <summary>
    /// Writes every message, code included, to the log instead of sending it. Development only.
    /// </summary>
    /// <remarks>
    /// Refused outside the Development environment, whatever this says: a code in a log file is a
    /// password in a log file.
    /// </remarks>
    public bool WriteToLog { get; init; }
}

/// <summary>A mail server, reached over SMTP.</summary>
public sealed class SmtpDeliveryOptions
{
    /// <summary>Host name. Empty turns e-mail off.</summary>
    public string? Host { get; init; }

    /// <summary>Port: 587 for STARTTLS, 465 for TLS, 25 for a relay that asks for neither.</summary>
    public int Port { get; init; } = 587;

    /// <summary><c>StartTls</c>, <c>Tls</c> or <c>None</c>.</summary>
    /// <remarks>
    /// <c>None</c> exists for a relay inside the data centre that speaks nothing else. A code then
    /// crosses that network in clear, so it should be a decision and not a default.
    /// </remarks>
    public string Security { get; init; } = "StartTls";

    /// <summary>Account to sign in to the server with. Empty for a relay that needs none.</summary>
    public string? Username { get; init; }

    /// <summary>Its password.</summary>
    public string? Password { get; init; }

    /// <summary>The address messages come from.</summary>
    public string? From { get; init; }

    /// <summary>The name shown beside it.</summary>
    public string FromName { get; init; } = "Quadrasense";

    /// <summary>How long to wait for the server.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Whether enough is configured to send anything.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(From);
}

/// <summary>An SMS gateway, reached over HTTP.</summary>
/// <remarks>
/// <para>
/// Gateways have no common protocol, so this describes the request rather than naming a product:
/// a URL, a method, headers and a body with <c>{to}</c>, <c>{toDigits}</c>, <c>{toNational}</c> and
/// <c>{text}</c> in it. Each value is escaped for where it lands: percent-encoded in the URL and
/// in a form body, JSON-escaped in a JSON body.
/// </para>
/// <para>
/// A gateway that speaks SMPP rather than HTTP needs its own implementation of
/// <c>IContactDelivery</c>; nothing above that interface would change.
/// </para>
/// </remarks>
public sealed class SmsGatewayOptions
{
    /// <summary>The gateway's address. Empty turns SMS off.</summary>
    public string? Url { get; init; }

    /// <summary>HTTP method.</summary>
    public string Method { get; init; } = "POST";

    /// <summary>Headers to send, such as an API key.</summary>
    public Dictionary<string, string> Headers { get; init; } = [];

    /// <summary>The request body's media type.</summary>
    public string ContentType { get; init; } = "application/json";

    /// <summary>The request body. Empty for a gateway that takes everything in the URL.</summary>
    public string? Body { get; init; }

    /// <summary>
    /// Text the response must contain, for a gateway that answers 200 whatever happened.
    /// </summary>
    public string? SuccessContains { get; init; }

    /// <summary>How long to wait for the gateway.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Whether enough is configured to send anything.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url);
}
