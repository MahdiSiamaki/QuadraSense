namespace Sqm.Application.Identity;

/// <summary>
/// Checks a username and password against whatever holds the truth about them.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam for Active Directory. The product owner chose local accounts now, with
/// LDAP/AD as a likely later requirement (ADR-006), and both are the same shape of problem: the
/// user types a name and a secret, something authoritative says yes or no, and the application
/// decides what that person may do. <c>LocalPasswordAuthenticator</c> implements it today;
/// <c>LdapPasswordAuthenticator</c> would implement it by binding to the directory, and nothing
/// above this interface would change.
/// </para>
/// <para>
/// OIDC deliberately does NOT fit here, and pretending it does would be the expensive kind of
/// abstraction. A redirect flow never sees a password: it adds a callback endpoint that maps an
/// external subject claim onto <c>auth.user_account.external_id</c> and then creates a session
/// through the same <see cref="ISessionStore"/>. That is an addition alongside this interface,
/// not an implementation of it, and the schema already carries the columns it needs.
/// </para>
/// <para>
/// Authorisation is never delegated. Even with a directory, group membership maps to roles held
/// in this system's own tables, because the permission catalogue is this system's concept and no
/// directory knows what <c>tac.activate</c> means.
/// </para>
/// </remarks>
public interface IPasswordAuthenticator
{
    /// <summary>The <c>auth.user_account.provider</c> value this authenticator owns.</summary>
    string Provider { get; }

    /// <summary>
    /// Attempts a sign-in, recording the attempt and applying lockout policy.
    /// </summary>
    /// <remarks>
    /// The implementation owns the failure counters and the audit entry, because both have to
    /// happen whatever the caller does next, and a caller that forgets one leaves either a
    /// lockout that never triggers or an attack that leaves no trace.
    /// </remarks>
    Task<LoginOutcome> AuthenticateAsync(
        string username,
        string password,
        AuthenticationContext context,
        CancellationToken ct);
}

/// <summary>Where a sign-in attempt came from. Recorded with the attempt, successful or not.</summary>
public sealed record AuthenticationContext(string? Ip, string? UserAgent, string? CorrelationId);
