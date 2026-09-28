using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using TruvoID.Core.Interfaces;
using TruvoID.Domain.Enums;
using TruvoID.Infrastructure.Postgres;

namespace TruvoID.API.Endpoints;

public static class AgencyInvitationEndpoints
{
    public static IEndpointRouteBuilder MapAgencyInvitationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/admin/agencies/invite", InviteAgency)
            .RequireAuthorization("TruvoAdmin");
        app.MapPost("/v1/auth/agency-invitations/accept", AcceptInvitation)
            .AllowAnonymous();
        return app;
    }

    private static async Task<IResult> InviteAgency(
        HttpContext ctx,
        InviteAgencyRequest request,
        ControlPlaneIdentityStore identities,
        IAuditService audit,
        CancellationToken ct)
    {
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
    }

    private static async Task<IResult> AcceptInvitation(
        AcceptAgencyInvitationRequest request,
        ControlPlaneIdentityStore identities,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
            return Results.BadRequest(new { error = "Invitation token is required." });

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
