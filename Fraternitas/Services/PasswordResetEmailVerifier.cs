using System.Net;
using System.Text;
using System.Text.Json;

namespace Fraternitas.Services;

/// <summary>Exercise the Resend request without using a real key or sending live mail.</summary>
public static class PasswordResetEmailVerifier
{
    public static async Task VerifyAsync()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RESEND_API_KEY"] = "test-only-not-a-real-key",
            ["FRATERNITAS_EMAIL_FROM"] = "Fraternitas <notifications@example.invalid>"
        }).Build();

        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.resend.com/") };
        var mail = new FraternitasEmailService(client, config);
        const string recipient = "member@example.invalid";
        const string link = "https://fraternitas.moir.pw/account/reset-password?uid=test-user&code=test-link";
        var result = await PasswordRecoveryEndpoints.SendResetEmailAsync(mail, recipient, link);
        if (!result.Accepted || result.MessageId != "mock-message-id" || handler.Calls != 1)
            throw new InvalidOperationException("Resend email acceptance was not handled.");

        using var json = JsonDocument.Parse(handler.Body ?? "");
        var root = json.RootElement;
        if (root.GetProperty("to")[0].GetString() != recipient
            || root.GetProperty("from").GetString() != "Fraternitas <notifications@example.invalid>"
            || !root.GetProperty("subject").GetString()!.Contains("Reset your password")
            || !root.GetProperty("text").GetString()!.Contains(link)
            || !root.GetProperty("text").GetString()!.Contains("two hours")
            || handler.Method != HttpMethod.Post
            || handler.Url != "https://api.resend.com/emails"
            || handler.AuthorizationScheme != "Bearer")
            throw new InvalidOperationException("Reset email did not include the expected recipient and link.");

        handler.Reject = true;
        var failure = await PasswordRecoveryEndpoints.SendResetEmailAsync(mail, recipient, link);
        if (failure.Accepted || handler.Calls != 2)
            throw new InvalidOperationException("Resend rejection was not handled.");

        Console.WriteLine("Password reset email payload and provider rejection verified with a mock Resend handler.");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Url { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public int Calls { get; private set; }
        public bool Reject { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Method = request.Method;
            Url = request.RequestUri?.AbsoluteUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            return new HttpResponseMessage(Reject ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"mock-message-id\"}", Encoding.UTF8, "application/json")
            };
        }
    }
}
