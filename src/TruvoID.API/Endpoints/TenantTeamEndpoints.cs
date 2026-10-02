using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using TruvoID.Core.Interfaces;
using TruvoID.Domain.Enums;
using TruvoID.Infrastructure.Postgres;
using TruvoID.Infrastructure.Services;

namespace TruvoID.API.Endpoints;

public static class TenantTeamEndpoints
{
    public static IEndpointRouteBuilder MapTenantTeamEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/tenant/team").RequireAuthorization("TenantManager");
        group.MapGet("/", List);
        group.MapPost("/invite", Invite);
        group.MapPost("/{userId:guid}/disable", Disable);
        group.MapPost("/{userId:guid}/reactivate", Reactivate);
        group.MapPut("/{userId:guid}/role", ChangeRole);
        app.MapPost("/v1/auth/team-invitations/accept", Accept).AllowAnonymous();
        return app;
    }

    private static bool IsAdmin(HttpContext ctx) =>
        ctx.User.IsInRole("Admin") || ctx.User.FindFirst("tenant_role")?.Value is "institution_admin" or "agency_admin";

    private static async Task<IResult> List(HttpContext ctx, ControlPlaneIdentityStore identities, CancellationToken ct)
    {
        return Results.Ok(await identities.ListTeamAsync(ctx.GetOrganizationId(), ct));
    }

    private static async Task<IResult> Invite(HttpContext ctx, InviteTeamMemberRequest request, ControlPlaneIdentityStore identities, TenantConnectionFactory tenants, IAuditService audit, IEmailService email, ILoggerFactory loggerFactory, IConfiguration configuration, CancellationToken ct)
    {
        if (!IsAdmin(ctx)) return Results.Forbid();
        var allowed = ctx.User.FindFirst("tenant_role")?.Value == "agency_admin"
            ? new[] { "agency_user", "outlet_staff", "outlet_owner" }
            : new[] { "institution_staff" };
        if (!allowed.Contains(request.Role, StringComparer.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "The selected role is not valid for this organization." });
        if (request.Role.StartsWith("outlet_", StringComparison.OrdinalIgnoreCase) && request.OutletId is null)
            return Results.BadRequest(new { error = "An outlet is required for outlet team roles." });
        if (request.OutletId is { } outletId)
        {
            await using var session = await tenants.BeginAsync(TenantScope.Organization(ctx.GetOrganizationId()), ct);
            await using var outlet = session.CreateCommand("SELECT id FROM outlet WHERE id = @id");
            outlet.Parameters.AddWithValue("id", outletId);
            if (await outlet.ExecuteScalarAsync(ct) is null)
                return Results.NotFound(new { error = "Outlet not found in this organization." });
        }
        var rawToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();
        var invitation = await identities.InviteUserAsync(ctx.GetOrganizationId(), request.Email, request.FullName, request.Role.ToLowerInvariant(), request.OutletId, ctx.GetUserId(), hash, DateTime.UtcNow.AddDays(7), ct);
        await audit.LogAsync(AuditAction.Created, "TeamInvitation", invitation.InvitationId, ctx.GetUserId(), "User", request.Email, ct);
        var inviteUrl = AppLinks.Build(configuration, $"accept-team-invite?token={rawToken}");
        try
        {
            var inviterName = ctx.User.Identity?.Name ?? "Your workspace administrator";
            await email.SendAsync(request.Email, request.FullName, $"You're invited to join {request.Role.Replace('_', ' ')} on TruvoID", EmailTemplates.StaffInvitation(ctx.User.FindFirst("institution_name")?.Value ?? "your TruvoID workspace", inviterName, request.Role.Replace('_', ' '), inviteUrl));
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger(nameof(TenantTeamEndpoints)).LogError(ex, "Team invitation email could not be sent to {Email}", request.Email);
        }
        return Results.Ok(new { invitationId = invitation.InvitationId, invitationToken = rawToken, expiresAt = DateTime.UtcNow.AddDays(7) });
    }

    private static async Task<IResult> Disable(HttpContext ctx, Guid userId, ControlPlaneIdentityStore identities, CancellationToken ct) => await ChangeStatus(ctx, userId, "disabled", identities, ct);
    private static async Task<IResult> Reactivate(HttpContext ctx, Guid userId, ControlPlaneIdentityStore identities, CancellationToken ct) => await ChangeStatus(ctx, userId, "active", identities, ct);

    private static async Task<IResult> ChangeStatus(HttpContext ctx, Guid userId, string status, ControlPlaneIdentityStore identities, CancellationToken ct)
    {
        if (!IsAdmin(ctx)) return Results.Forbid();
        return await identities.SetUserStatusAsync(ctx.GetOrganizationId(), userId, status, ct)
            ? Results.Ok(new { message = $"Team member {status}." })
            : Results.NotFound(new { error = "Team member not found." });
    }

    private static async Task<IResult> ChangeRole(HttpContext ctx, Guid userId, ChangeTeamRoleRequest request, ControlPlaneIdentityStore identities, CancellationToken ct)
    {
        if (!IsAdmin(ctx)) return Results.Forbid();
        var family = ctx.User.FindFirst("tenant_role")?.Value == "agency_admin" ? "agency" : "institution";
        var expectedRole = $"{family}_{(family == "agency" ? "user" : "staff")}";
        if (!string.Equals(request.Role, expectedRole, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "The selected role is not valid for this organization." });
        return await identities.SetUserRoleAsync(ctx.GetOrganizationId(), userId, expectedRole, ct)
            ? Results.Ok(new { message = "Team role updated." })
            : Results.NotFound(new { error = "Team member not found." });
    }

    private static async Task<IResult> Accept(AcceptTeamInvitationRequest request, ControlPlaneIdentityStore identities, CancellationToken ct)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Token))).ToLowerInvariant();
        try
        {
            var user = await identities.AcceptUserInvitationAsync(hash, request.Password, ct);
            return user is null ? Results.BadRequest(new { error = "This invitation is invalid, expired, or already accepted." }) : Results.Ok(new { message = "Invitation accepted. You can now sign in." });
        }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    public sealed record InviteTeamMemberRequest(string FullName, string Email, string Role, Guid? OutletId);
    public sealed record ChangeTeamRoleRequest(string Role);
    public sealed record AcceptTeamInvitationRequest(string Token, string Password);
}
