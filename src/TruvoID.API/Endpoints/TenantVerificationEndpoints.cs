using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TruvoID.API.Auth;
using TruvoID.Infrastructure.Postgres;

namespace TruvoID.API.Endpoints;

public static class TenantVerificationEndpoints
{
    public static IEndpointRouteBuilder MapTenantVerificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/tenant/verification-calls")
            .RequireAuthorization(policy => policy
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, ApiKeyAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser());
        group.MapPost("/reserve", Reserve);
        return app;
    }

    private static async Task<IResult> Reserve(
        HttpContext ctx,
        ReserveVerificationRequest request,
        TenantConnectionFactory tenants,
        TenantVerificationService verifications,
        CancellationToken ct)
    {
        try
        {
            await using var session = await tenants.BeginAsync(ctx.GetTenantScope(), ct);
            var reservation = await verifications.ReserveAsync(
                session,
                request.VerificationType,
                request.SubjectRef,
                ctx.GetUserId() is var userId && userId != Guid.Empty ? userId : null,
                ctx.GetApiKeyId(),
                request.IdempotencyKey,
                ct);
            await session.CommitAsync(ct);
            return Results.Ok(reservation);
        }
        catch (InsufficientWalletBalanceException ex)
        {
            return Results.Conflict(new { error = ex.Message, code = "insufficient_balance" });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
    }

    public sealed record ReserveVerificationRequest(
        string VerificationType,
        string SubjectRef,
        string? IdempotencyKey);
}
