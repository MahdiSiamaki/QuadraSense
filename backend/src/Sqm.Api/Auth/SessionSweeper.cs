using Microsoft.Extensions.Options;
using Sqm.Application.Identity;

namespace Sqm.Api.Auth;

/// <summary>Deletes sessions that ended long enough ago to be of no further interest.</summary>
/// <remarks>
/// <para>
/// Expired sessions are already unusable - <c>ResolveAsync</c> checks the deadlines, so nothing
/// depends on this running. It exists so the table does not grow without bound, and it is
/// deliberately lazy about it: ended sessions are kept for thirty days so a user can see "signed
/// out from this device last Tuesday" on their profile, which is how someone notices a session
/// they did not start.
/// </para>
/// <para>
/// A hosted service rather than a cron entry or a database job, because it is one statement
/// against a small table and giving it its own deployment artefact would cost more to operate
/// than it saves. The audit log keeps the permanent record either way.
/// </para>
/// </remarks>
public sealed partial class SessionSweeper : BackgroundService
{
    [LoggerMessage(EventId = 1300, Level = LogLevel.Information,
        Message = "Removed {Count} ended session(s)")]
    private partial void LogSwept(int count);

    [LoggerMessage(EventId = 1301, Level = LogLevel.Warning,
        Message = "Session sweep failed; will retry on the next pass")]
    private partial void LogSweepFailed(Exception exception);

    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly ISessionStore _sessions;
    private readonly SessionSettings _options;
    private readonly ILogger<SessionSweeper> _logger;

    /// <summary>Creates the sweeper.</summary>
    public SessionSweeper(
        ISessionStore sessions, IOptions<AuthOptions> auth, ILogger<SessionSweeper> logger)
    {
        ArgumentNullException.ThrowIfNull(auth);

        _sessions = sessions;
        _options = auth.Value.Session;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        do
        {
            try
            {
                var removed = await _sessions
                    .DeleteExpiredAsync(_options.RetainEndedSessionsFor, stoppingToken)
                    .ConfigureAwait(false);

                if (removed > 0)
                {
                    LogSwept(removed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Housekeeping must never take the API down. A database blip here costs one pass.
                LogSweepFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
