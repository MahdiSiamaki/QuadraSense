using Sqm.Application.Identity;
using Sqm.Infrastructure.Identity;

namespace Sqm.Api.Auth;

/// <summary>Does the work of <see cref="AccountOutbox"/>, after the responses have gone.</summary>
/// <remarks>
/// One at a time. These are rare, each ends at a mail server or a gateway, and a person waiting for
/// a code waits seconds at worst. The time this takes is the point: it happens where nobody
/// calling <c>POST recovery/start</c> can measure it.
/// </remarks>
public sealed partial class AccountOutboxDispatcher : BackgroundService
{
    [LoggerMessage(EventId = 1310, Level = LogLevel.Error,
        Message = "Account work failed and was dropped; the person can ask again")]
    private partial void LogFailed(Exception exception);

    private readonly AccountOutbox _outbox;
    private readonly IAccountRecovery _recovery;
    private readonly ILogger<AccountOutboxDispatcher> _logger;

    /// <summary>Creates the dispatcher.</summary>
    public AccountOutboxDispatcher(
        AccountOutbox outbox, IAccountRecovery recovery, ILogger<AccountOutboxDispatcher> logger)
    {
        _outbox = outbox;
        _recovery = recovery;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var work in _outbox.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await (work switch
                {
                    RecoveryCodeWork code => _recovery.SendCodeAsync(
                        code.Identifier, code.Channel, code.Context, stoppingToken),
                    AccountNoticeWork notice => _recovery.SendNoticeAsync(
                        notice.UserId, notice.Notice, stoppingToken),
                    _ => Task.CompletedTask,
                }).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(ex);
            }
        }
    }
}
