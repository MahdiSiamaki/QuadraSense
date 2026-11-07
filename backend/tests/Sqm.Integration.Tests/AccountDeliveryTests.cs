using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Sqm.Application.Identity;
using Sqm.Infrastructure.Identity;
using Sqm.Infrastructure.Identity.Delivery;

namespace Sqm.Integration.Tests;

/// <summary>
/// What leaves the building, in the form it leaves in. Needs no database, so it runs everywhere.
/// </summary>
public sealed class AccountDeliveryTests
{
    private static readonly string Key = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ContactDelivery Delivery(
        DeliveryOptions options, bool isDevelopment = false, HttpMessageHandler? handler = null) =>
        new(options, isDevelopment, NullLogger<ContactDelivery>.Instance, handler);

    private static readonly OutgoingMessage Message = new("Subject line", "123456 is your code.");

    // ======================================================================= the secret box

    [Fact]
    public void A_sealed_secret_opens_for_its_owner()
    {
        var box = SecretBox.FromConfiguration(Key)!;
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");

        Assert.Equal(secret, box.Open(box.Seal(secret, 41), 41));
    }

    [Fact]
    public void A_row_copied_to_another_account_does_not_open()
    {
        var box = SecretBox.FromConfiguration(Key)!;

        Assert.Null(box.Open(box.Seal([1, 2, 3], 41), 42));
    }

    [Fact]
    public void A_changed_byte_does_not_open()
    {
        var box = SecretBox.FromConfiguration(Key)!;
        var sealedSecret = box.Seal([1, 2, 3], 41);

        sealedSecret[^1] ^= 0x01;

        Assert.Null(box.Open(sealedSecret, 41));
    }

    [Fact]
    public void Another_key_does_not_open()
    {
        var box = SecretBox.FromConfiguration(Key)!;
        var other = SecretBox.FromConfiguration(Convert.ToBase64String(new byte[32]))!;

        Assert.Null(other.Open(box.Seal([1, 2, 3], 41), 41));
    }

    [Fact]
    public void Two_seals_of_one_secret_differ()
    {
        var box = SecretBox.FromConfiguration(Key)!;

        Assert.NotEqual(box.Seal([1, 2, 3], 41), box.Seal([1, 2, 3], 41));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_key_means_no_box(string? key) => Assert.Null(SecretBox.FromConfiguration(key));

    [Theory]
    [InlineData("not base64 at all!")]
    [InlineData("c2hvcnQ=")]
    public void A_key_that_cannot_be_used_stops_the_start_rather_than_failing_later(string key) =>
        Assert.Throws<InvalidOperationException>(() => SecretBox.FromConfiguration(key));

    // ======================================================================= which channel

    [Fact]
    public void A_channel_with_nothing_configured_is_off()
    {
        using var delivery = Delivery(new DeliveryOptions());

        Assert.False(delivery.IsAvailable(ContactChannel.Email));
        Assert.False(delivery.IsAvailable(ContactChannel.Sms));
    }

    [Fact]
    public void Each_channel_turns_on_by_itself()
    {
        using var email = Delivery(new DeliveryOptions
        {
            Email = new SmtpDeliveryOptions { Host = "mail.test", From = "pi@example.test" },
        });
        using var sms = Delivery(new DeliveryOptions
        {
            Sms = new SmsGatewayOptions { Url = "https://gateway.test/send" },
        });

        Assert.True(email.IsAvailable(ContactChannel.Email));
        Assert.False(email.IsAvailable(ContactChannel.Sms));
        Assert.True(sms.IsAvailable(ContactChannel.Sms));
        Assert.False(sms.IsAvailable(ContactChannel.Email));
    }

    [Fact]
    public void A_mail_server_with_no_sender_address_is_not_configured()
    {
        using var delivery = Delivery(new DeliveryOptions
        {
            Email = new SmtpDeliveryOptions { Host = "mail.test" },
        });

        Assert.False(delivery.IsAvailable(ContactChannel.Email));
    }

    [Fact]
    public void Writing_codes_to_the_log_is_refused_outside_development()
    {
        var options = new DeliveryOptions { WriteToLog = true };

        using var production = Delivery(options, isDevelopment: false);
        using var development = Delivery(options, isDevelopment: true);

        Assert.False(production.IsAvailable(ContactChannel.Email));
        Assert.True(development.IsAvailable(ContactChannel.Email));
    }

    [Fact]
    public async Task Sending_on_a_channel_that_is_off_fails_plainly()
    {
        using var delivery = Delivery(new DeliveryOptions());

        await Assert.ThrowsAsync<ContactDeliveryException>(() =>
            delivery.SendAsync(ContactChannel.Sms, "+989120000000", Message, Ct));
    }

    // ======================================================================= the SMS gateway

    private static DeliveryOptions Sms(
        string url = "https://gateway.test/send", string? body = null,
        string contentType = "application/json", string? successContains = null,
        Dictionary<string, string>? headers = null) => new()
    {
        Sms = new SmsGatewayOptions
        {
            Url = url,
            Body = body,
            ContentType = contentType,
            SuccessContains = successContains,
            Headers = headers ?? [],
        },
    };

    [Fact]
    public async Task A_json_gateway_gets_the_number_and_the_text_escaped_for_json()
    {
        using var gateway = new FakeGateway();
        using var delivery = Delivery(
            Sms(body: """{"to":"{to}","digits":"{toDigits}","local":"{toNational}","text":"{text}"}"""),
            handler: gateway);

        await delivery.SendAsync(
            ContactChannel.Sms, "+989121234567", new OutgoingMessage("", "a \"quoted\" code"), Ct);

        Assert.Equal(
            """{"to":"+989121234567","digits":"989121234567","local":"09121234567","text":"a \"quoted\" code"}""",
            gateway.Body);
        Assert.Equal("application/json", gateway.ContentType);
    }

    [Fact]
    public async Task A_form_gateway_gets_the_values_percent_encoded()
    {
        using var gateway = new FakeGateway();
        using var delivery = Delivery(
            Sms(body: "to={to}&text={text}", contentType: "application/x-www-form-urlencoded"),
            handler: gateway);

        await delivery.SendAsync(
            ContactChannel.Sms, "+989121234567", new OutgoingMessage("", "a b&c"), Ct);

        Assert.Equal("to=%2B989121234567&text=a%20b%26c", gateway.Body);
    }

    [Fact]
    public async Task An_xml_gateway_gets_the_values_escaped_for_xml()
    {
        using var gateway = new FakeGateway();
        using var delivery = Delivery(
            Sms(body: "<m><t>{text}</t></m>", contentType: "text/xml"), handler: gateway);

        await delivery.SendAsync(
            ContactChannel.Sms, "+989121234567", new OutgoingMessage("", "a<b&c"), Ct);

        Assert.Equal("<m><t>a&lt;b&amp;c</t></m>", gateway.Body);
    }

    [Fact]
    public async Task A_gateway_that_takes_everything_in_the_address_gets_it_encoded()
    {
        using var gateway = new FakeGateway();
        using var delivery = Delivery(
            Sms(url: "https://gateway.test/send?to={toNational}&msg={text}"), handler: gateway);

        await delivery.SendAsync(
            ContactChannel.Sms, "+989121234567", new OutgoingMessage("", "a b"), Ct);

        Assert.Equal("https://gateway.test/send?to=09121234567&msg=a%20b", gateway.Url);
        Assert.Null(gateway.Body);
    }

    [Fact]
    public async Task A_text_holding_a_placeholder_name_is_not_expanded()
    {
        using var gateway = new FakeGateway();
        using var delivery = Delivery(Sms(body: "{text}", contentType: "text/plain"), handler: gateway);

        await delivery.SendAsync(
            ContactChannel.Sms, "+989121234567", new OutgoingMessage("", "{to} stays"), Ct);

        Assert.Equal("{to} stays", gateway.Body);
    }

    [Fact]
    public async Task Configured_headers_are_sent()
    {
        using var gateway = new FakeGateway();
        using var delivery = Delivery(
            Sms(headers: new() { ["X-Api-Key"] = "secret-value" }), handler: gateway);

        await delivery.SendAsync(ContactChannel.Sms, "+989121234567", Message, Ct);

        Assert.Equal("secret-value", gateway.Header("X-Api-Key"));
    }

    [Fact]
    public async Task A_header_left_empty_in_the_environment_is_not_sent()
    {
        using var gateway = new FakeGateway();
        using var delivery = Delivery(
            Sms(headers: new() { ["Authorization"] = string.Empty }), handler: gateway);

        await delivery.SendAsync(ContactChannel.Sms, "+989121234567", Message, Ct);

        Assert.Null(gateway.Header("Authorization"));
    }

    [Fact]
    public async Task A_refusal_is_reported_without_the_text_or_the_address()
    {
        using var gateway = new FakeGateway(HttpStatusCode.Forbidden);
        using var delivery = Delivery(
            Sms(url: "https://gateway.test/send?key=abc123"), handler: gateway);

        var failure = await Assert.ThrowsAsync<ContactDeliveryException>(() =>
            delivery.SendAsync(ContactChannel.Sms, "+989121234567", Message, Ct));

        Assert.Contains("403", failure.Message);
        Assert.DoesNotContain("123456", failure.Message);
        Assert.DoesNotContain("abc123", failure.Message);
    }

    [Fact]
    public async Task A_gateway_that_cannot_be_reached_is_reported_without_the_address()
    {
        using var gateway = new FakeGateway(fail: true);
        using var delivery = Delivery(
            Sms(url: "https://gateway.test/send?key=abc123"), handler: gateway);

        var failure = await Assert.ThrowsAsync<ContactDeliveryException>(() =>
            delivery.SendAsync(ContactChannel.Sms, "+989121234567", Message, Ct));

        Assert.DoesNotContain("abc123", failure.Message);
    }

    [Fact]
    public async Task A_gateway_that_answers_200_to_everything_can_be_held_to_its_body()
    {
        using var gateway = new FakeGateway(answer: """{"status":"rejected"}""");
        using var delivery = Delivery(
            Sms(successContains: "\"status\":\"sent\""), handler: gateway);

        await Assert.ThrowsAsync<ContactDeliveryException>(() =>
            delivery.SendAsync(ContactChannel.Sms, "+989121234567", Message, Ct));
    }

    // ======================================================================= the mail server

    [Fact]
    public async Task A_message_reaches_a_mail_server_as_written()
    {
        using var server = new FakeMailServer();
        using var delivery = Delivery(new DeliveryOptions
        {
            Email = new SmtpDeliveryOptions
            {
                Host = "127.0.0.1",
                Port = server.Port,
                Security = "None",
                From = "pi@example.test",
                FromName = "Quadrasense",
            },
        });

        await delivery.SendAsync(
            ContactChannel.Email, "person@example.test",
            new OutgoingMessage("Your code", "123456 is your code."), Ct);

        var conversation = await server.ConversationAsync();

        Assert.Contains("MAIL FROM:<pi@example.test>", conversation);
        Assert.Contains("RCPT TO:<person@example.test>", conversation);
        Assert.Contains("Subject: Your code", conversation);
        Assert.Contains("123456 is your code.", conversation);
    }

    [Fact]
    public async Task A_mail_server_that_is_not_there_fails_as_a_delivery_failure()
    {
        // A port that was free a moment ago.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        using var delivery = Delivery(new DeliveryOptions
        {
            Email = new SmtpDeliveryOptions
            {
                Host = "127.0.0.1",
                Port = port,
                Security = "None",
                From = "pi@example.test",
            },
        });

        await Assert.ThrowsAsync<ContactDeliveryException>(() =>
            delivery.SendAsync(ContactChannel.Email, "person@example.test", Message, Ct));
    }

    [Fact]
    public void A_security_setting_that_means_nothing_stops_the_start() =>
        Assert.Throws<InvalidOperationException>(() => Delivery(new DeliveryOptions
        {
            Email = new SmtpDeliveryOptions
            {
                Host = "mail.test",
                From = "pi@example.test",
                Security = "Maybe",
            },
        }));

    // =======================================================================

    private sealed class FakeGateway : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _answer;
        private readonly bool _fail;
        private HttpRequestMessage? _request;

        public FakeGateway(
            HttpStatusCode status = HttpStatusCode.OK, string answer = "ok", bool fail = false)
        {
            _status = status;
            _answer = answer;
            _fail = fail;
        }

        public string? Url => _request?.RequestUri?.AbsoluteUri;

        public string? Body { get; private set; }

        public string? ContentType { get; private set; }

        public string? Header(string name) =>
            _request?.Headers.TryGetValues(name, out var values) == true ? values.Single() : null;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_fail)
            {
                throw new HttpRequestException(
                    $"No route to {request.RequestUri}", new SocketException());
            }

            _request = request;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            ContentType = request.Content?.Headers.ContentType?.MediaType;

            return new HttpResponseMessage(_status) { Content = new StringContent(_answer) };
        }
    }

    /// <summary>Just enough SMTP to accept one message and remember the conversation.</summary>
    private sealed class FakeMailServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task<string> _conversation;

        public FakeMailServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _conversation = Task.Run(RunAsync);
        }

        public int Port { get; }

        public Task<string> ConversationAsync() => _conversation;

        public void Dispose() => _listener.Stop();

        private async Task<string> RunAsync()
        {
            var heard = new StringBuilder();

            using var client = await _listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, Encoding.ASCII)
            {
                AutoFlush = true,
                NewLine = "\r\n",
            };

            await writer.WriteLineAsync("220 test ESMTP");
            var inData = false;

            while (await reader.ReadLineAsync() is { } line)
            {
                heard.AppendLine(line);

                if (inData)
                {
                    if (line == ".")
                    {
                        inData = false;
                        await writer.WriteLineAsync("250 accepted");
                    }

                    continue;
                }

                if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("221 bye");
                    break;
                }

                if (line.StartsWith("DATA", StringComparison.OrdinalIgnoreCase))
                {
                    inData = true;
                    await writer.WriteLineAsync("354 go on");
                    continue;
                }

                await writer.WriteLineAsync("250 ok");
            }

            return heard.ToString();
        }
    }
}
