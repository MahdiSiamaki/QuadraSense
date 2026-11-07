using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Sqm.Application.Identity;

namespace Sqm.Infrastructure.Identity.Delivery;

/// <summary>Sends e-mail through a mail server.</summary>
/// <remarks>
/// MailKit rather than <c>System.Net.Mail.SmtpClient</c>, which Microsoft's documentation retires
/// in its favour: the built-in client has no implicit TLS on port 465 and no cancellation.
/// A connection per message, because these are rare and a held connection is one more thing for
/// a mail server to drop.
/// </remarks>
internal sealed class SmtpEmailSender
{
    private readonly SmtpDeliveryOptions _options;
    private readonly SecureSocketOptions _security;

    public SmtpEmailSender(SmtpDeliveryOptions options)
    {
        _options = options;
        _security = options.Security.ToUpperInvariant() switch
        {
            "STARTTLS" => SecureSocketOptions.StartTls,
            "TLS" => SecureSocketOptions.SslOnConnect,
            "NONE" => SecureSocketOptions.None,
            _ => throw new InvalidOperationException(
                "Auth:Delivery:Email:Security must be StartTls, Tls or None."),
        };
    }

    public async Task SendAsync(string to, OutgoingMessage message, CancellationToken ct)
    {
        try
        {
            using var mail = new MimeMessage();
            mail.From.Add(new MailboxAddress(_options.FromName, _options.From!));
            mail.To.Add(MailboxAddress.Parse(to));
            mail.Subject = message.Subject;
            mail.Body = new TextPart("plain") { Text = message.Text };

            using var client = new SmtpClient
            {
                Timeout = (int)_options.Timeout.TotalMilliseconds,
            };

            await client.ConnectAsync(_options.Host!, _options.Port, _security, ct)
                .ConfigureAwait(false);

            if (!string.IsNullOrEmpty(_options.Username))
            {
                await client.AuthenticateAsync(_options.Username, _options.Password ?? string.Empty, ct)
                    .ConfigureAwait(false);
            }

            await client.SendAsync(mail, ct).ConfigureAwait(false);
            await client.DisconnectAsync(true, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or CommandException
            or ProtocolException or MailKit.Security.AuthenticationException
            or System.Security.Authentication.AuthenticationException or ParseException
            or TimeoutException)
        {
            throw new ContactDeliveryException($"The mail server refused: {ex.Message}", ex);
        }
    }
}
