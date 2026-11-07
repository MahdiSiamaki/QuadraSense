using Microsoft.Extensions.Logging;
using Sqm.Application.Identity;

namespace Sqm.Infrastructure.Identity.Delivery;

/// <summary>Sends each message down the channel it asks for, if that channel is configured.</summary>
public sealed partial class ContactDelivery : IContactDelivery, IDisposable
{
    [LoggerMessage(EventId = 4230, Level = LogLevel.Warning,
        Message = "DEVELOPMENT ONLY, not sent. {Channel} to {Destination}: {Text}")]
    private partial void LogMessage(ContactChannel channel, string destination, string text);

    [LoggerMessage(EventId = 4231, Level = LogLevel.Warning,
        Message = "Auth:Delivery:WriteToLog is set outside Development and is ignored.")]
    private partial void LogRefused();

    private readonly SmtpEmailSender? _email;
    private readonly HttpSmsSender? _sms;
    private readonly bool _writeToLog;
    private readonly ILogger<ContactDelivery> _logger;

    /// <summary>Creates the delivery.</summary>
    /// <param name="options">What is configured.</param>
    /// <param name="isDevelopment">Whether this process may write a code to its log.</param>
    /// <param name="logger">The log.</param>
    /// <param name="smsHandler">Stands in for the network in a test.</param>
    public ContactDelivery(
        DeliveryOptions options,
        bool isDevelopment,
        ILogger<ContactDelivery> logger,
        HttpMessageHandler? smsHandler = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _logger = logger;
        _email = options.Email.IsConfigured ? new SmtpEmailSender(options.Email) : null;
        _sms = options.Sms.IsConfigured ? new HttpSmsSender(options.Sms, smsHandler) : null;
        _writeToLog = options.WriteToLog && isDevelopment;

        if (options.WriteToLog && !isDevelopment)
        {
            LogRefused();
        }
    }

    /// <inheritdoc />
    public bool IsAvailable(ContactChannel channel) => _writeToLog || channel switch
    {
        ContactChannel.Email => _email is not null,
        ContactChannel.Sms => _sms is not null,
        _ => false,
    };

    /// <inheritdoc />
    public async Task SendAsync(
        ContactChannel channel, string destination, OutgoingMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (_writeToLog)
        {
            LogMessage(channel, destination, message.Text);
            return;
        }

        switch (channel)
        {
            case ContactChannel.Email when _email is not null:
                await _email.SendAsync(destination, message, ct).ConfigureAwait(false);
                break;

            case ContactChannel.Sms when _sms is not null:
                await _sms.SendAsync(destination, message.Text, ct).ConfigureAwait(false);
                break;

            default:
                throw new ContactDeliveryException($"{channel} is not configured.");
        }
    }

    /// <inheritdoc />
    public void Dispose() => _sms?.Dispose();
}
