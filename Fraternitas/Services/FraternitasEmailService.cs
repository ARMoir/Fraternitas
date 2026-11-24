using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Fraternitas.Services;

public sealed record EmailSendResult(bool Accepted, string Message, string? MessageId = null);

/// <summary>
/// Transactional email via Resend. The API key is read only from the server's
/// environment; never expose it in rendered HTML, logs, GitHub or a database.
/// </summary>
public sealed class FraternitasEmailService(HttpClient client, IConfiguration config)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(config["RESEND_API_KEY"]);
    public string Sender => config["FRATERNITAS_EMAIL_FROM"]?.Trim()
        is { Length: > 0 } from ? from : "Fraternitas <onboarding@resend.dev>";

    public async Task<EmailSendResult> SendAsync(
        string recipient, string subject, string plainText, CancellationToken cancellation = default)
    {
        var key = config["RESEND_API_KEY"];
        if (string.IsNullOrWhiteSpace(key))
            return new(false, "Resend isn't configured on this server (RESEND_API_KEY is missing).");
        if (!System.Net.Mail.MailAddress.TryCreate(recipient, out var address)
            || !string.Equals(address.Address, recipient.Trim(), StringComparison.OrdinalIgnoreCase))
            return new(false, "Recipient email address is invalid.");

        using var request = new HttpRequestMessage(HttpMethod.Post, "emails");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = JsonContent.Create(new
        {
            from = Sender,
            to = new[] { address.Address },
            subject,
            text = plainText
        });
        try
        {
            using var response = await client.SendAsync(request, cancellation);
            if (!response.IsSuccessStatusCode)
                return new(false, $"Resend rejected the message (HTTP {(int)response.StatusCode}). Check your verified sending domain and recipient.");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation);
            var id = json.RootElement.TryGetProperty("id", out var idValue)
                ? idValue.GetString() : null;
            return new(true, "Resend accepted the email for delivery.", id);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or JsonException or InvalidOperationException)
        {
            return new(false, "Could not reach Resend. Check network connectivity and service configuration.");
        }
    }
}
