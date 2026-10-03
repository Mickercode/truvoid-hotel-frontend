using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using TruvoID.API.Auth;
using TruvoID.Infrastructure.Identity;
using TruvoID.Infrastructure.Postgres;

namespace TruvoID.API.Endpoints;

public static class TenantVerificationEndpoints
{
    public static IEndpointRouteBuilder MapTenantVerificationEndpoints(this IEndpointRouteBuilder app)
    {
        // Callable from the dashboard (JWT) or server-to-server (X-API-Key).
        app.MapPost("/v1/verify/{type}", Verify)
            .RequireAuthorization(policy => policy
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, ApiKeyAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser())
            .RequireRateLimiting("verify");

        // Test numbers for test mode — available everywhere, since every workspace has test mode.
        app.MapGet("/v1/verify/test-numbers", () => Results.Ok(SandboxIdentityProvider.TestNumbers.ToDictionary(
                kv => kv.Key, kv => new { match = kv.Value.Match, noMatch = kv.Value.NoMatch, providerError = kv.Value.Error })))
            .AllowAnonymous();
        return app;
    }

    private static async Task<IResult> Verify(
        HttpContext ctx,
        string type,
        VerifyRequest request,
        VerificationRunner runner,
        OrganizationSetupStore setup,
        CancellationToken ct)
    {
        try
        {
            // Mode: an API key's own environment wins (trv_test_ / trv_live_); the dashboard sends
            // X-TruvoID-Mode. Anything unspecified is test — live is never the accidental default.
            var mode = ctx.User.FindFirst("key_environment")?.Value
                ?? ctx.Request.Headers["X-TruvoID-Mode"].FirstOrDefault()?.Trim().ToLowerInvariant()
                ?? "test";
            var testMode = mode != "live";
            if (!testMode && !await setup.IsLiveEnabledAsync(ctx.GetOrganizationId(), ct))
                return Results.Json(new
                {
                    error = "Live verification unlocks once TruvoID approves your organization profile. Until then, use test mode — it's free.",
                    code = "live_not_enabled",
                }, statusCode: StatusCodes.Status403Forbidden);

            var idempotencyKey = ctx.Request.Headers["Idempotency-Key"].FirstOrDefault() ?? request.IdempotencyKey;
            var userId = ctx.GetUserId();
            var outcome = await runner.RunAsync(
                ctx.GetTenantScope(), type, request.Number,
                userId == Guid.Empty ? null : userId, ctx.GetApiKeyId(), idempotencyKey, ct, testMode);

            var body = new
            {
                id = outcome.CallId,
                type = outcome.Type,
                environment = outcome.Environment,
                status = outcome.Status,
                message = outcome.Message,
                identity = outcome.Identity,
                charge = new { amountKobo = outcome.AmountKobo, refunded = outcome.Refunded },
                balanceAfterKobo = outcome.BalanceAfterKobo,
                createdAt = outcome.CreatedAt,
                replayed = outcome.Replayed,
            };
            return outcome.Status switch
            {
                "provider_error" => Results.Json(body, statusCode: StatusCodes.Status502BadGateway),
                "pending" => Results.Json(body, statusCode: StatusCodes.Status202Accepted),
                _ => Results.Ok(body),
            };
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message, code = "invalid_request" });
        }
        catch (InsufficientWalletBalanceException ex)
        {
            return Results.Json(new { error = "Your wallet balance is too low for this verification. Fund your wallet and try again.",
                code = "insufficient_balance", balanceKobo = ex.BalanceKobo }, statusCode: StatusCodes.Status402PaymentRequired);
        }
        catch (IdentityProviderUnavailableException ex)
        {
            return Results.Json(new { error = ex.Message, code = "provider_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Results.Conflict(new { error = "A request with this Idempotency-Key is already in progress.", code = "idempotency_conflict" });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("No price is configured"))
        {
            return Results.Json(new { error = "Pricing for this verification type hasn't been set up yet. Contact TruvoID support.",
                code = "pricing_not_configured" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not an active, provisioned tenant"))
        {
            return Results.Conflict(new { error = "Your workspace is still being set up or has been suspended.", code = "workspace_not_ready" });
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
    }

    /// <param name="Number">The NIN, BVN or phone number to verify.</param>
    /// <param name="IdempotencyKey">Optional; the Idempotency-Key header takes precedence.</param>
    public sealed record VerifyRequest(string? Number, string? IdempotencyKey);
}
