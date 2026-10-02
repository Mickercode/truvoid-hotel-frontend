using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TruvoID.Infrastructure.Postgres;
using TruvoID.Infrastructure.Services;

namespace TruvoID.API.Endpoints;

public static class PasswordResetEndpoints
{
    // Identical for known and unknown emails, so this endpoint can't be used to discover accounts.
    private const string SentMessage = "If an account exists for that email, we've sent a link to reset the password. It expires in 1 hour.";

    public static IEndpointRouteBuilder MapPasswordResetEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/auth/forgot-password", ForgotPassword).AllowAnonymous().RequireRateLimiting("auth");
        app.MapPost("/v1/auth/reset-password", ResetPassword).AllowAnonymous().RequireRateLimiting("auth");
        return app;
    }

    private static async Task<IResult> ForgotPassword(
        ForgotPasswordRequest request,
        PasswordResetStore resets,
        IServiceScopeFactory scopes,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!AuthValidation.IsValidEmail(request.Email))
            return Results.BadRequest(new { error = "Enter a valid email address." });

        if (await resets.IssueAsync(request.Email!, ct) is { } issued)
        {
            var url = AppLinks.Build(configuration, $"reset-password?token={issued.Token}");
            // Sent in the background: awaiting the email provider here would make known
            // emails measurably slower to answer than unknown ones.
            _ = Task.Run(async () =>
            {
                await using var scope = scopes.CreateAsyncScope();
                try
                {
                    await scope.ServiceProvider.GetRequiredService<IEmailService>().SendAsync(
                        issued.Email, issued.FullName ?? issued.Email, "Reset your TruvoID password",
                        EmailTemplates.PasswordReset(issued.FullName ?? "there", url));
                }
                catch (Exception ex)
                {
                    loggerFactory.CreateLogger(nameof(PasswordResetEndpoints))
                        .LogError(ex, "Password reset email could not be sent");
                }
            });
        }
        return Results.Ok(new { message = SentMessage });
    }

    private static async Task<IResult> ResetPassword(
        ResetPasswordRequest request,
        PasswordResetStore resets,
        RefreshTokenStore refreshTokens,
        CancellationToken ct)
    {
        if (AuthValidation.ValidatePassword(request.Password) is { } passwordError)
            return Results.BadRequest(new { error = passwordError });

        var userId = await resets.RedeemAsync(request.Token ?? "", PasswordHasher.Hash(request.Password!), ct);
        if (userId is null)
            return Results.BadRequest(new { error = "This reset link is invalid, has expired, or was already used. Request a new one.", code = "invalid_reset_token" });

        // Whoever knew the old password is signed out everywhere.
        await refreshTokens.RevokeAllForUserAsync(userId.Value, ct);
        return Results.Ok(new { message = "Your password has been reset. Sign in with your new password." });
    }

    public sealed record ForgotPasswordRequest(string? Email);
    public sealed record ResetPasswordRequest(string? Token, string? Password);
}
