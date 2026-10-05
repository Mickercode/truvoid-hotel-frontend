using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TruvoID.Core.Interfaces;
using TruvoID.Domain.Enums;
using TruvoID.Infrastructure.Postgres;
using TruvoID.Infrastructure.Services;

namespace TruvoID.API.Endpoints;

/// <summary>
/// TruvoID Ops create Institutions and Agencies by invitation (build doc §2.4). The organization
/// itself is created only when the invitation is accepted — see OrganizationInvitationStore.
/// </summary>
public static class OrganizationInvitationEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationInvitationEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/v1/admin").RequireAuthorization("TruvoAdmin");
        admin.MapGet("/email-availability", async (string? email, OrganizationInvitationStore invitations, CancellationToken ct) =>
            AuthValidation.IsValidEmail(email)
                ? Results.Ok(await invitations.CheckEmailAsync(email!, ct))
                : Results.Ok(new EmailAvailability(false, "Enter a valid email address.")));
        admin.MapGet("/invitations", async (OrganizationInvitationStore invitations, CancellationToken ct) =>
            Results.Ok(await invitations.ListAsync(ct)));
        admin.MapPost("/invitations", Create);
        admin.MapPost("/invitations/{id:guid}/resend", Resend);
        admin.MapPost("/invitations/{id:guid}/cancel", async (HttpContext ctx, Guid id, OrganizationInvitationStore invitations, IAuditService audit, CancellationToken ct) =>
        {
            if (!await invitations.CancelAsync(id, ct))
                return Results.Conflict(new { error = "Only open invitations can be cancelled." });
            await audit.LogAsync(AuditAction.Deleted, "OrganizationInvitation", id, ctx.GetUserId(), "User", "cancelled", ct);
            return Results.Ok(new { message = "Invitation cancelled. The link no longer works." });
        });

        // Public: the invite link's page loads the details, then accepts with a new password.
        app.MapGet("/v1/auth/invitations/{token}", async (string token, OrganizationInvitationStore invitations, CancellationToken ct) =>
                await invitations.PreviewAsync(token, ct) is { } i
                    ? Results.Ok(new { organizationName = i.OrganizationName, organizationType = i.OrganizationType,
                        adminFullName = i.AdminFullName, adminEmail = i.AdminEmail, status = i.Status, expiresAt = i.ExpiresAt })
                    : Results.NotFound(new { error = "This invitation link isn't valid. Check you opened the full link from your email." }))
            .AllowAnonymous().RequireRateLimiting("auth");
        app.MapPost("/v1/auth/invitations/accept", Accept).AllowAnonymous().RequireRateLimiting("auth");
        return app;
    }

    public sealed record CreateInvitationRequest(string? OrganizationType, string? OrganizationName, string? AdminFullName, string? AdminEmail);
    public sealed record AcceptInvitationRequest(string? Token, string? Password);

    internal static async Task<IResult> Create(
        HttpContext ctx, CreateInvitationRequest request, OrganizationInvitationStore invitations,
        IEmailService email, IAuditService audit, IConfiguration configuration, ILoggerFactory loggers, CancellationToken ct)
    {
        var type = request.OrganizationType?.Trim().ToLowerInvariant();
        if (type is not ("institution" or "agency"))
            return Results.BadRequest(new { error = "Choose Institution or Agency." });
        if ((request.OrganizationName?.Trim().Length ?? 0) is < 2 or > 120)
            return Results.BadRequest(new { error = "Organization name must be between 2 and 120 characters." });
        if ((request.AdminFullName?.Trim().Length ?? 0) is < 2 or > 120)
            return Results.BadRequest(new { error = "Enter the administrator's full name." });
        if (!AuthValidation.IsValidEmail(request.AdminEmail))
            return Results.BadRequest(new { error = "Enter a valid email address for the administrator." });

        try
        {
            var issued = await invitations.CreateAsync(request.OrganizationName!, type, request.AdminFullName!, request.AdminEmail!, ctx.GetUserId(), ct);
            await audit.LogAsync(AuditAction.Created, "OrganizationInvitation", issued.Invitation.Id, ctx.GetUserId(), "User",
                $"{type}: {issued.Invitation.OrganizationName}", ct);
            return await DeliverAsync(issued, email, configuration, loggers, "Invitation created");
        }
        catch (InvitationException ex)
        {
            return Results.Conflict(new { error = ex.Message, code = ex.Code });
        }
    }

    private static async Task<IResult> Resend(
        HttpContext ctx, Guid id, OrganizationInvitationStore invitations,
        IEmailService email, IAuditService audit, IConfiguration configuration, ILoggerFactory loggers, CancellationToken ct)
    {
        try
        {
            var issued = await invitations.ResendAsync(id, ct);
            await audit.LogAsync(AuditAction.Updated, "OrganizationInvitation", id, ctx.GetUserId(), "User", "resent", ct);
            return await DeliverAsync(issued, email, configuration, loggers, "New link issued (the previous link no longer works)");
        }
        catch (InvitationException ex)
        {
            return Results.Conflict(new { error = ex.Message, code = ex.Code });
        }
    }

    /// <summary>Emails the link and says honestly whether that worked; the link is always returned so Ops can share it.</summary>
    private static async Task<IResult> DeliverAsync(IssuedInvitation issued, IEmailService email, IConfiguration configuration, ILoggerFactory loggers, string headline)
    {
        var i = issued.Invitation;
        var link = AppLinks.Build(configuration, $"accept-invite?token={issued.Token}");
        var emailSent = true;
        try
        {
            await email.SendAsync(i.AdminEmail, i.AdminFullName, $"You're invited to set up {i.OrganizationName} on TruvoID",
                EmailTemplates.StaffInvitation(i.OrganizationName, "TruvoID",
                    i.OrganizationType == "agency" ? "Agency administrator" : "Institution administrator", link));
        }
        catch (Exception ex)
        {
            emailSent = false;
            loggers.CreateLogger(nameof(OrganizationInvitationEndpoints)).LogError(ex, "Invitation email to {Email} could not be sent", i.AdminEmail);
        }
        return Results.Ok(new
        {
            invitation = i,
            link,
            emailSent,
            message = emailSent
                ? $"{headline} and emailed to {i.AdminEmail}."
                : $"{headline}, but the email could not be sent. Copy the link below and send it to {i.AdminEmail} yourself.",
        });
    }

    private static async Task<IResult> Accept(
        AcceptInvitationRequest request, OrganizationInvitationStore invitations, IAuditService audit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
            return Results.BadRequest(new { error = "The invitation link is incomplete." });
        if (AuthValidation.ValidatePassword(request.Password) is { } passwordError)
            return Results.BadRequest(new { error = passwordError });
        try
        {
            var (organizationId, userId, adminEmail) = await invitations.AcceptAsync(request.Token, PasswordHasher.Hash(request.Password!), ct);
            await audit.LogAsync(AuditAction.Created, "Organization", organizationId, userId, "User", "invitation accepted", ct);
            return Results.Ok(new { message = "Your organization has been created. Sign in to continue.", email = adminEmail });
        }
        catch (InvitationException ex)
        {
            return Results.Json(new { error = ex.Message, code = ex.Code },
                statusCode: ex.Code == "email_taken" ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest);
        }
    }
}
