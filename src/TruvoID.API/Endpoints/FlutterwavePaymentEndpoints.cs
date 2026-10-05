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
        // Funding moves real money, so it's organization-administrator only — not any
        // signed-in staff member or agency user.
        var topups = app.MapGroup("/v1/tenant/wallet/topups/flutterwave")
            .RequireAuthorization("TenantManager")
            .AddEndpointFilter(async (context, next) => context.HttpContext.IsOrganizationAdmin()
                ? await next(context)
                : Results.Json(new { error = "Only your organization's administrator can fund the wallet." },
                    statusCode: StatusCodes.Status403Forbidden));
        topups.MapPost("/initialize", Initialize);
        topups.MapPost("/verify", Verify);
        app.MapPost("/v1/payments/flutterwave/webhook", Webhook).AllowAnonymous().RequireRateLimiting("webhook");
        return app;
    }

    private static async Task<IResult> Initialize(
        HttpContext ctx,
        InitializeRequest request,
        TenantConnectionFactory tenants,
        FlutterwavePaymentService flutterwave,
        IConfiguration configuration,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        if (request.AmountNaira < 50000) return Results.BadRequest(new { error = "Minimum wallet funding is NGN 50,000." });
        if (!IsAllowedRedirect(request.RedirectUrl, configuration))
            return Results.BadRequest(new { error = "The payment redirect URL is not an allowed application URL." });
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
            loggers.CreateLogger(nameof(FlutterwavePaymentEndpoints))
                .LogError(ex, "Flutterwave checkout initialization failed for {Reference}", txRef);
            return Results.Problem("Payment checkout could not be initialized. Please try again or use bank transfer.",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> Verify(
        HttpContext ctx,
        VerifyRequest request,
        FlutterwavePaymentService flutterwave,
        TenantConnectionFactory tenants,
        TenantWalletService wallets,
        ILoggerFactory loggers,
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
            loggers.CreateLogger(nameof(FlutterwavePaymentEndpoints))
                .LogWarning(ex, "Flutterwave verification failed for reference {Reference}", request.TransactionReference);
            return Results.BadRequest(new { error = "We could not verify this payment. If you were debited, contact support." });
        }
    }

    private static async Task<IResult> Webhook(HttpRequest request, TenantConnectionFactory tenants, TenantWalletService wallets, IConfiguration configuration, CancellationToken ct)
    {
        var expected = configuration["Flutterwave:WebhookHash"] ?? Environment.GetEnvironmentVariable("FLUTTERWAVE_WEBHOOK_HASH");
        var received = request.Headers["verif-hash"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(received) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(received)))
            return Results.Unauthorized();

        // Bounded body + defensive parsing: this endpoint is anonymous, so a malformed
        // or oversized payload must never turn into an unhandled 500.
        if (request.ContentLength is > 64 * 1024)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Malformed webhook payload." });
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { error = "Malformed webhook payload." });
            if (!data.TryGetProperty("status", out var statusElement)
                || !string.Equals(statusElement.GetString(), "successful", StringComparison.OrdinalIgnoreCase))
                return Results.Ok();
            var txRef = data.TryGetProperty("tx_ref", out var txRefElement) ? txRefElement.GetString() ?? "" : "";
            var parts = txRef.Split('_');
            if (parts.Length < 3 || !Guid.TryParse(parts[1], out var organizationId))
                return Results.BadRequest(new { error = "Invalid transaction reference." });
            if (!data.TryGetProperty("id", out var idElement)
                || !data.TryGetProperty("amount", out var amountElement) || amountElement.ValueKind != JsonValueKind.Number
                || !data.TryGetProperty("currency", out var currencyElement))
                return Results.BadRequest(new { error = "Malformed webhook payload." });

            var payment = new FlutterwaveVerification(txRef, idElement.ToString(), "successful",
                amountElement.GetDecimal(), currencyElement.GetString() ?? "");
            return await CreditPayment(organizationId, payment, tenants, wallets, ct);
        }
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

    /// <summary>
    /// A caller-supplied redirect URL is handed to Flutterwave, which sends the user there after
    /// checkout. Without an allowlist this is an open redirect (phishing through a trusted payment
    /// page), so only same-origin app URLs (App:BaseUrl / Cors:Origins) are accepted.
    /// </summary>
    private static bool IsAllowedRedirect(string? url, IConfiguration configuration)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var redirect))
            return false;
        if (redirect.Scheme is not ("http" or "https"))
            return false;

        var allowedOrigins = new List<string>();
        if (configuration["App:BaseUrl"] is { Length: > 0 } appBaseUrl)
            allowedOrigins.Add(appBaseUrl);
        allowedOrigins.AddRange(configuration.GetSection("Cors:Origins").Get<string[]>() ?? []);

        var redirectOrigin = redirect.GetLeftPart(UriPartial.Authority);
        return allowedOrigins
            .Select(o => Uri.TryCreate(o, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : null)
            .Any(o => o is not null && string.Equals(o, redirectOrigin, StringComparison.OrdinalIgnoreCase));
    }

    public sealed record InitializeRequest(decimal AmountNaira, string RedirectUrl);
    public sealed record VerifyRequest(string TransactionReference, string ProviderTransactionId);
}
