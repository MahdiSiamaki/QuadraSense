using System.Globalization;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Sqm.Application.Identity;

namespace Sqm.Infrastructure.Identity.Delivery;

/// <summary>Sends a text through an HTTP gateway, described entirely by configuration.</summary>
/// <remarks>
/// See <see cref="SmsGatewayOptions"/>. A failure says only what came back: never the text, which
/// holds the code, and never the URL, which may hold a key.
/// </remarks>
internal sealed class HttpSmsSender : IDisposable
{
    private readonly SmsGatewayOptions _options;
    private readonly HttpClient _client;

    public HttpSmsSender(SmsGatewayOptions options, HttpMessageHandler? handler = null)
    {
        _options = options;
        _client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _client.Timeout = options.Timeout;
    }

    public async Task SendAsync(string destination, string text, CancellationToken ct)
    {
        var values = Values(destination, text);

        using var request = new HttpRequestMessage(
            new HttpMethod(_options.Method.ToUpperInvariant()),
            Fill(_options.Url!, values, Uri.EscapeDataString));

        // An empty value is a header left unset in the deployment's environment, not one to send.
        foreach (var (name, value) in _options.Headers.Where(h => !string.IsNullOrEmpty(h.Value)))
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (!string.IsNullOrEmpty(_options.Body))
        {
            var body = Fill(_options.Body, values, Escaper(_options.ContentType));
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(_options.ContentType);
        }

        try
        {
            using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new ContactDeliveryException(
                    $"The SMS gateway answered {(int)response.StatusCode}.");
            }

            if (!string.IsNullOrEmpty(_options.SuccessContains))
            {
                var answer = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (!answer.Contains(_options.SuccessContains, StringComparison.Ordinal))
                {
                    throw new ContactDeliveryException(
                        "The SMS gateway's answer did not contain what was expected.");
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            && !ct.IsCancellationRequested)
        {
            // Not ex.Message: an HttpRequestException names the address, and the address may carry
            // the gateway's key.
            throw new ContactDeliveryException(
                $"The SMS gateway could not be reached ({ex.GetType().Name}).", ex);
        }
    }

    /// <inheritdoc cref="IDisposable.Dispose"/>
    public void Dispose() => _client.Dispose();

    /// <summary>Each value a template may name.</summary>
    internal static Dictionary<string, string> Values(string destination, string text)
    {
        var digits = destination.TrimStart('+');

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{to}"] = destination,
            ["{toDigits}"] = digits,
            // Iranian gateways commonly want the number as dialled at home.
            ["{toNational}"] = digits.StartsWith("98", StringComparison.Ordinal)
                ? string.Create(CultureInfo.InvariantCulture, $"0{digits[2..]}")
                : digits,
            ["{text}"] = text,
        };
    }

    /// <summary>Puts escaped values into a template.</summary>
    /// <remarks>In one pass, so a value containing a placeholder's name is left as it is.</remarks>
    internal static string Fill(
        string template, IReadOnlyDictionary<string, string> values, Func<string, string> escape)
    {
        var result = new StringBuilder(template.Length);
        var position = 0;

        while (position < template.Length)
        {
            var match = values.Keys.FirstOrDefault(
                key => string.CompareOrdinal(template, position, key, 0, key.Length) == 0);

            if (match is null)
            {
                result.Append(template[position]);
                position++;
            }
            else
            {
                result.Append(escape(values[match]));
                position += match.Length;
            }
        }

        return result.ToString();
    }

    /// <summary>How a value must be written inside a body of this type.</summary>
    internal static Func<string, string> Escaper(string contentType)
    {
        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return value =>
                JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString();
        }

        if (contentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.EscapeDataString;
        }

        if (contentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
        {
            return value => SecurityElement.Escape(value) ?? string.Empty;
        }

        return value => value;
    }
}
