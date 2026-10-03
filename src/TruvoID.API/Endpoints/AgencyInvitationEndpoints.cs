using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Npgsql;
using TruvoID.Core.Interfaces;
using TruvoID.Domain.Enums;
using TruvoID.Infrastructure.Postgres;
using TruvoID.Infrastructure.Services;

namespace TruvoID.API.Endpoints;

public static class AgencyInvitationEndpoints
{
    public static IEndpointRouteBuilder MapAgencyInvitationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/admin/agencies/invite", InviteAgency)
            .RequireAuthorization("TruvoAdmin");
        app.MapPost("/v1/auth/agency-invitations/accept", AcceptInvitation)
            .AllowAnonymous().RequireRateLimiting("auth");
        return app;
    }

    private static async Task<IResult> InviteAgency(
        HttpContext ctx,
        InviteAgencyRequest request,
        ControlPlaneIdentityStore identities,
        IAuditService audit,
        IEmailService email,
        ILoggerFactory loggerFactory,
        IConfiguration configuration,
        CancellationToken ct)
    {
        // Validate before anything is created, so a typo never leaves a junk agency behind.
        if ((request.AgencyName?.Trim().Length ?? 0) is < 2 or > 120)
            return Results.BadRequest(new { error = "Agency name must be between 2 and 120 characters." });
        if ((request.AdminFullName?.Trim().Length ?? 0) is < 2 or > 120)
            return Results.BadRequest(new { error = "Enter the agency administrator's full name." });
        if (!AuthValidation.IsValidEmail(request.AdminEmail))
            return Results.BadRequest(new { error = "Enter a valid email address for the agency administrator." });
        try
        {
            var rawToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();
            var invitation = await identities.InviteAgencyAsync(
                request.AgencyName,
                request.AdminFullName,
                request.AdminEmail,
                ctx.GetUserId(),
                tokenHash,
                DateTime.UtcNow.AddDays(7),
                ct);
            await audit.LogAsync(AuditAction.Created, "AgencyInvitation", invitation.InvitationId,
                ctx.GetUserId(), "User", request.AgencyName, ct);
            var inviteUrl = AppLinks.Build(configuration, $"accept-agency-invite?token={rawToken}");
            try
            {
                await email.SendAsync(request.AdminEmail, request.AdminFullName, $"You're invited to administer {request.AgencyName} on TruvoID", EmailTemplates.StaffInvitation(request.AgencyName, "TruvoID Platform", "Agency administrator", inviteUrl));
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger(nameof(AgencyInvitationEndpoints)).LogError(ex, "Agency invitation email could not be sent to {Email}", request.AdminEmail);
            }

            return Results.Ok(new
            {
                invitationId = invitation.InvitationId,
                organizationId = invitation.OrganizationId,
                expiresAt = DateTime.UtcNow.AddDays(7),
                invitationToken = rawToken
            });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Results.Conflict(new { error = "That email already has a TruvoID account. Each email can belong to only one account — use a different email.", code = "email_taken" });
        }
    }

    private static async Task<IResult> AcceptInvitation(
        AcceptAgencyInvitationRequest request,
        ControlPlaneIdentityStore identities,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
            return Results.BadRequest(new { error = "Invitation token is required." });
        if (AuthValidation.ValidatePassword(request.Password) is { } passwordError)
            return Results.BadRequest(new { error = passwordError });

        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Token))).ToLowerInvariant();
        try
        {
            var user = await identities.AcceptAgencyInvitationAsync(tokenHash, request.Password, ct);
            return user is null
                ? Results.BadRequest(new { error = "This invitation is invalid, expired, or already accepted." })
                : Results.Ok(new { message = "Agency invitation accepted. You can now sign in." });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    public sealed record InviteAgencyRequest(string AgencyName, string AdminFullName, string AdminEmail);
    public sealed record AcceptAgencyInvitationRequest(string Token, string Password);
}
