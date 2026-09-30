using System.Net.Http.Json;
using System.Text.Json;

namespace TruvoID.Infrastructure.Services;

public sealed record FlutterwaveCheckout(string TransactionReference, string PaymentLink);
public sealed record FlutterwaveVerification(string TransactionReference, string ProviderTransactionId, string Status, decimal Amount, string Currency);

public sealed class FlutterwavePaymentService(IHttpClientFactory clients)
{
    public async Task<FlutterwaveCheckout> InitializeAsync(
        string txRef,
        decimal amount,
        string email,
        string name,
        string redirectUrl,
        CancellationToken ct)
    {
        var response = await clients.CreateClient("flutterwave").PostAsJsonAsync("v3/payments", new
        {
            tx_ref = txRef,
            amount,
            currency = "NGN",
            redirect_url = redirectUrl,
            customer = new { email, name },
            customizations = new { title = "TruvoID wallet funding", description = "Fund your TruvoID verification wallet" }
        }, ct);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        if (!response.IsSuccessStatusCode || !body.TryGetProperty("status", out var status) || status.GetString() != "success")
            throw new InvalidOperationException(body.TryGetProperty("message", out var message) ? message.GetString() : "Flutterwave checkout could not be initialized.");

        var data = body.GetProperty("data");
        return new FlutterwaveCheckout(txRef, data.GetProperty("link").GetString() ?? throw new InvalidOperationException("Flutterwave did not return a checkout link."));
    }

    public async Task<FlutterwaveVerification> VerifyAsync(string providerTransactionId, CancellationToken ct)
    {
        var response = await clients.CreateClient("flutterwave").GetAsync($"v3/transactions/{Uri.EscapeDataString(providerTransactionId)}/verify", ct);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        if (!response.IsSuccessStatusCode || !body.TryGetProperty("status", out var status) || status.GetString() != "success")
            throw new InvalidOperationException(body.TryGetProperty("message", out var message) ? message.GetString() : "Flutterwave transaction verification failed.");

        var data = body.GetProperty("data");
        return new FlutterwaveVerification(
            data.GetProperty("tx_ref").GetString() ?? throw new InvalidOperationException("Flutterwave response has no transaction reference."),
            data.GetProperty("id").ToString(),
            data.GetProperty("status").GetString() ?? "",
            data.GetProperty("amount").GetDecimal(),
            data.GetProperty("currency").GetString() ?? "");
    }
}
