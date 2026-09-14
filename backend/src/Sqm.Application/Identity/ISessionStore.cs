namespace Sqm.Application.Identity;

/// <summary>Creates, resolves and revokes sessions.</summary>
/// <remarks>
/// <para>
/// Sessions live in PostgreSQL and the cookie carries nothing but an opaque random value. The
/// cost is one indexed lookup per request; the benefit is that every authorisation decision reads
/// the current state of the account, so deactivating a user or changing their roles takes effect
/// immediately rather than whenever a token happens to expire.
/// </para>
/// <para>
/// There is deliberately no in-memory cache of resolved sessions. A cache would restore exactly
/// the staleness window that choosing server-side sessions was meant to remove, and would do it
/// invisibly - the revocation would appear to work in testing and fail under load, on whichever
/// instance happened to hold the stale entry.
/// </para>
/// </remarks>
public interface ISessionStore
{
    /// <summary>Issues a new session and returns the cookie value exactly once.</summary>
    Task<SessionCreation> CreateAsync(
        long userId, string? ip, string? userAgent, CancellationToken ct);

    /// <summary>
    /// Resolves a cookie value to its session and the user's current effective permissions.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> if the token is unknown, expired (idle or absolute), revoked, or
    /// belongs to a deactivated account. The caller cannot tell which, and does not need to.
    /// </returns>
    Task<ResolvedSession?> ResolveAsync(string token, CancellationToken ct);

    /// <summary>Rolls the idle window forward.</summary>
    /// <remarks>
    /// Called after a successful request, and only when the session has not been touched
    /// recently - see <c>SessionOptions.TouchInterval</c>. Writing on every request would turn
    /// every GET into a write transaction for no behavioural gain.
    /// </remarks>
    Task TouchAsync(Guid sessionId, CancellationToken ct);

    /// <summary>Revokes one session. Idempotent.</summary>
    Task<bool> RevokeAsync(Guid sessionId, string revokedBy, string reason, CancellationToken ct);

    /// <summary>
    /// Revokes every live session for a user, optionally sparing the one making the request.
    /// </summary>
    /// <remarks>
    /// Called on deactivation, on password change, and from the "sign out everywhere" action.
    /// A password change that leaves the attacker's existing session alive has not recovered the
    /// account, which is the whole reason a user changes a password they think is compromised.
    /// </remarks>
    Task<int> RevokeAllForUserAsync(
        long userId, Guid? exceptSessionId, string revokedBy, string reason, CancellationToken ct);

    /// <summary>Sessions for a user, newest first, live and recently ended.</summary>
    Task<IReadOnlyList<SessionInfo>> ListForUserAsync(
        long userId, Guid? currentSessionId, int limit, CancellationToken ct);

    /// <summary>Deletes sessions that ended long enough ago to be of no further interest.</summary>
    /// <returns>How many rows were removed.</returns>
    Task<int> DeleteExpiredAsync(TimeSpan retainEndedFor, CancellationToken ct);
}
