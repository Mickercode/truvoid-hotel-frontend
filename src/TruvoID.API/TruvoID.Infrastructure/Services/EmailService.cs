using System.Net.Http.Json;
using System.Text.Json;

namespace TruvoID.Infrastructure.Services;

public interface IEmailService
{
    Task SendAsync(string toEmail, string toName, string subject, string htmlBody);
}

public class ResendEmailService : IEmailService
{
    private readonly HttpClient _http;
    private readonly string _fromAddress;
    private const string DefaultFromAddress = "TruvoID <noreply@gettruvoid.com>";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ResendEmailService(IHttpClientFactory httpClientFactory)
    {
        _http = httpClientFactory.CreateClient("resend");
        // Allow the From address to be overridden via env so the sending domain
        // can be switched without redeploying (must be verified in Resend).
        _fromAddress = Environment.GetEnvironmentVariable("EMAIL_FROM_ADDRESS") ?? DefaultFromAddress;
    }

    public async Task SendAsync(string toEmail, string toName, string subject, string htmlBody)
    {
        var payload = new
        {
            from = _fromAddress,
            to = new[] { string.IsNullOrWhiteSpace(toName) ? toEmail : $"{toName} <{toEmail}>" },
            subject,
            html = htmlBody
        };

        var response = await _http.PostAsJsonAsync("https://api.resend.com/emails", payload, JsonOptions);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            var hint = response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized =>
                    " — check Resend__ApiKey / RESEND_API_KEY is a valid Resend API key.",
                System.Net.HttpStatusCode.Forbidden =>
                    $" — the sending domain for '{_fromAddress}' must be verified in Resend (set EMAIL_FROM_ADDRESS to a verified sender).",
                _ => "",
            };
            throw new InvalidOperationException($"Resend API error {(int)response.StatusCode}: {error}{hint}");
        }
    }
}
