using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TruvoID.Infrastructure.Postgres;
using TruvoID.Infrastructure.Services;

namespace TruvoID.API.Endpoints;

public static class FlutterwavePaymentEndpoints
{
    public static IEndpointRouteBuilder MapFlutterwavePaymentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/tenant/wallet/topups/flutterwave/initialize", Initialize).RequireAuthorization();
        app.MapPost("/v1/tenant/wallet/topups/flutterwave/verify", Verify).RequireAuthorization();
        app.MapPost("/v1/payments/flutterwave/webhook", Webhook).AllowAnonymous();
        return app;
    }

    private static async Task<IResult> Initialize(
        HttpContext ctx,
        InitializeRequest request,
        TenantConnectionFactory tenants,
        FlutterwavePaymentService flutterwave,
        CancellationToken ct)
    {
        if (request.AmountNaira < 50000) return Results.BadRequest(new { error = "Minimum wallet funding is NGN 50,000." });
        var organizationId = ctx.GetOrganizationId();
        var txRef = $"trv_{organizationId:N}_{Guid.NewGuid():N}";
        await using (var session = await tenants.BeginAsync(TenantScope.Organization(organizationId), ct))
        {
            await using var command = session.CreateCommand("INSERT INTO wallet_payment (tx_ref, amount_kobo, currency, status) VALUES (@txRef, @amount, 'NGN', 'pending')");
            command.Parameters.AddWithValue("txRef", txRef);
            command.Parameters.AddWithValue("amount", checked((long)(request.AmountNaira * 100m)));
            await command.ExecuteNonQueryAsync(ct);
            await session.CommitAsync(ct);
        }

        try
        {
            var email = ctx.User.FindFirst("email")?.Value ?? throw new InvalidOperationException("An email address is required for payment.");
            var name = ctx.User.Identity?.Name ?? email;
            var checkout = await flutterwave.InitializeAsync(txRef, request.AmountNaira, email, name, request.RedirectUrl, ct);
            return Results.Ok(new { checkoutUrl = checkout.PaymentLink, transactionReference = checkout.TransactionReference });
        }
        catch (Exception ex)
        {
            return Results.Problem("Payment checkout could not be initialized.", statusCode: StatusCodes.Status502BadGateway, extensions: new Dictionary<string, object?> { ["error"] = ex.Message });
        }
    }

    private static async Task<IResult> Verify(
        HttpContext ctx,
        VerifyRequest request,
        FlutterwavePaymentService flutterwave,
        TenantConnectionFactory tenants,
        TenantWalletService wallets,
        CancellationToken ct)
    {
        try
        {
            var payment = await flutterwave.VerifyAsync(request.ProviderTransactionId, ct);
            if (!string.Equals(payment.Status, "successful", StringComparison.OrdinalIgnoreCase) || !string.Equals(payment.Currency, "NGN", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Flutterwave has not marked this payment as successful." });
            if (!string.Equals(payment.TransactionReference, request.TransactionReference, StringComparison.Ordinal))
                return Results.BadRequest(new { error = "Payment reference mismatch." });
            return await CreditPayment(ctx.GetOrganizationId(), payment, tenants, wallets, ct);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> Webhook(HttpRequest request, TenantConnectionFactory tenants, TenantWalletService wallets, IConfiguration configuration, CancellationToken ct)
    {
        var expected = configuration["Flutterwave:WebhookHash"] ?? Environment.GetEnvironmentVariable("FLUTTERWAVE_WEBHOOK_HASH");
        var received = request.Headers["verif-hash"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(received) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(received)))
            return Results.Unauthorized();

        using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
        var data = document.RootElement.GetProperty("data");
        if (!string.Equals(data.GetProperty("status").GetString(), "successful", StringComparison.OrdinalIgnoreCase)) return Results.Ok();
        var txRef = data.GetProperty("tx_ref").GetString() ?? "";
        var parts = txRef.Split('_');
        if (parts.Length < 3 || !Guid.TryParse(parts[1], out var organizationId)) return Results.BadRequest(new { error = "Invalid transaction reference." });
        var payment = new FlutterwaveVerification(txRef, data.GetProperty("id").ToString(), "successful", data.GetProperty("amount").GetDecimal(), data.GetProperty("currency").GetString() ?? "");
        return await CreditPayment(organizationId, payment, tenants, wallets, ct);
    }

    private static async Task<IResult> CreditPayment(Guid organizationId, FlutterwaveVerification payment, TenantConnectionFactory tenants, TenantWalletService wallets, CancellationToken ct)
    {
        if (!string.Equals(payment.Currency, "NGN", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new { error = "Unsupported payment currency." });
        await using var session = await tenants.BeginAsync(TenantScope.Organization(organizationId), ct);
        await using var find = session.CreateCommand("SELECT id, amount_kobo, status FROM wallet_payment WHERE tx_ref = @txRef FOR UPDATE");
        find.Parameters.AddWithValue("txRef", payment.TransactionReference);
        await using var reader = await find.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return Results.NotFound(new { error = "Payment record not found." });
        var paymentId = reader.GetGuid(0);
        var amountKobo = reader.GetInt64(1);
        var status = reader.GetString(2);
        await reader.CloseAsync();
        if (status == "succeeded") { await session.CommitAsync(ct); return Results.Ok(new { status = "already_credited" }); }
        if (checked((long)(payment.Amount * 100m)) != amountKobo) return Results.BadRequest(new { error = "Payment amount mismatch." });
        var mutation = await wallets.CreditAsync(session, amountKobo, null, $"flutterwave:{payment.TransactionReference}", ct: ct);
        await wallets.AddRevenueOutboxEventAsync(session, new { organizationId, amountKobo, reference = payment.TransactionReference, entryType = "flutterwave_topup" }, ct);
        await using var update = session.CreateCommand("UPDATE wallet_payment SET status = 'succeeded', provider_transaction_id = @providerId, paid_at = now(), updated_at = now() WHERE id = @id");
        update.Parameters.AddWithValue("providerId", payment.ProviderTransactionId);
        update.Parameters.AddWithValue("id", paymentId);
        await update.ExecuteNonQueryAsync(ct);
        await session.CommitAsync(ct);
        return Results.Ok(new { status = "credited", mutation.BalanceAfterKobo, mutation.LedgerEntryId });
    }

    public sealed record InitializeRequest(decimal AmountNaira, string RedirectUrl);
    public sealed record VerifyRequest(string TransactionReference, string ProviderTransactionId);
}
