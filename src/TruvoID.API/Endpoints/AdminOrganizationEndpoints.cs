using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using TruvoID.Infrastructure.Postgres;
using TruvoID.Infrastructure.Services;

namespace TruvoID.API.Endpoints;

public static class AdminOrganizationEndpoints
{
    public static IEndpointRouteBuilder MapAdminOrganizationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/admin/organizations").RequireAuthorization("TruvoAdmin");
        group.MapGet("/", List);
        group.MapPost("/{id:guid}/suspend", (Guid id, NpgsqlDataSource db, CancellationToken ct) => SetStatus(id, "suspended", db, ct));
        group.MapPost("/{id:guid}/reactivate", (Guid id, NpgsqlDataSource db, CancellationToken ct) => SetStatus(id, "active", db, ct));

        // Profile review: approval is what moves an Institution from test mode to live.
        group.MapGet("/{id:guid}/setup", async (Guid id, OrganizationSetupStore setup, CancellationToken ct) =>
            Results.Ok(OrganizationSetupEndpoints.ToResponse(await setup.GetAsync(id, ct))));
        group.MapGet("/{id:guid}/documents/{documentId:guid}", async (Guid id, Guid documentId, OrganizationSetupStore setup, CancellationToken ct) =>
            await setup.GetDocumentAsync(id, documentId, ct) is { } doc
                ? Results.File(doc.Content, doc.ContentType, doc.FileName)
                : Results.NotFound(new { error = "Document not found." }));
        group.MapPost("/{id:guid}/setup/approve", (HttpContext ctx, Guid id, ReviewRequest? request, OrganizationSetupStore setup,
                NpgsqlDataSource db, IEmailService email, ILoggerFactory loggers, CancellationToken ct) =>
            Review(ctx, id, approve: true, request?.Note, setup, db, email, loggers, ct));
        group.MapPost("/{id:guid}/setup/request-changes", (HttpContext ctx, Guid id, ReviewRequest? request, OrganizationSetupStore setup,
                NpgsqlDataSource db, IEmailService email, ILoggerFactory loggers, CancellationToken ct) =>
            Review(ctx, id, approve: false, request?.Note, setup, db, email, loggers, ct));
        return app;
    }

    private static async Task<IResult> Review(
        HttpContext ctx, Guid organizationId, bool approve, string? note, OrganizationSetupStore setup,
        NpgsqlDataSource db, IEmailService email, ILoggerFactory loggers, CancellationToken ct)
    {
        try
        {
            if (!await setup.ReviewAsync(organizationId, approve, note, ctx.GetUserId(), ct))
                return Results.Conflict(new { error = "Only a submitted profile can be reviewed." });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        if (approve)
        {
            // Tell the organization's admins they can go live. Email failures never undo the approval.
            await using var admins = db.CreateCommand("""
                SELECT u.email, coalesce(u.full_name, u.email), o.name
                FROM control.app_user u JOIN control.organization o ON o.id = u.organization_id
                WHERE u.organization_id = @id AND u.role IN ('institution_admin', 'agency_admin') AND u.status = 'active'
                """);
            admins.Parameters.AddWithValue("id", organizationId);
            await using var reader = await admins.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                try
                {
                    await email.SendAsync(reader.GetString(0), reader.GetString(1), "You're approved to go live on TruvoID",
                        EmailTemplates.Approved(reader.GetString(2), reader.GetString(1)));
                }
                catch (Exception ex)
                {
                    loggers.CreateLogger(nameof(AdminOrganizationEndpoints)).LogError(ex, "Approval email failed for organization {OrganizationId}", organizationId);
                }
            }
        }
        return Results.Ok(new { message = approve ? "Profile approved. Live verification is now enabled." : "Sent back to the organization with your note." });
    }

    public sealed record ReviewRequest(string? Note);

    private static async Task<IResult> List(
        NpgsqlDataSource db,
        TenantConnectionFactory tenants,
        TenantWalletService wallets,
        CancellationToken ct)
    {
        var organizations = new List<OrganizationAdminItem>();
        await using (var command = db.CreateCommand("""
            SELECT o.id, o.name, o.type, o.status, o.created_at,
                   coalesce(s.status, 'incomplete'),
                   (SELECT count(*) FROM control.app_user u WHERE u.organization_id = o.id)
            FROM control.organization o
            LEFT JOIN control.organization_setup s ON s.organization_id = o.id
            ORDER BY o.created_at DESC
            """))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                organizations.Add(new OrganizationAdminItem(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTime>(4), reader.GetString(5), reader.GetInt64(6)));
        }

        // The wallet lives in each organization's own schema, so only provisioned
        // ("active") organizations have a balance to read; the rest stay null.
        var result = new List<object>(organizations.Count);
        foreach (var organization in organizations)
        {
            long? balanceKobo = null;
            if (organization.Status == "active")
            {
                try
                {
                    await using var session = await tenants.BeginAsync(TenantScope.Organization(organization.Id), ct);
                    balanceKobo = (await wallets.GetBalanceAsync(session, ct)).BalanceKobo;
                }
                catch
                {
                    // Best-effort: a single unreadable tenant must not fail the whole list.
                }
            }
            result.Add(new
            {
                organization.Id,
                organization.Name,
                organization.Type,
                organization.Status,
                organization.CreatedAt,
                organization.SetupStatus,
                organization.UserCount,
                balanceKobo,
            });
        }
        return Results.Ok(result);
    }

    private static async Task<IResult> SetStatus(Guid id, string status, NpgsqlDataSource db, CancellationToken ct)
    {
        // Only active <-> suspended. Reactivating a 'pending' Organization would mark it
        // active before the worker has created its schema, breaking it permanently.
        var from = status == "suspended" ? "active" : "suspended";
        await using var command = db.CreateCommand(
            "UPDATE control.organization SET status = @status, updated_at = now() WHERE id = @id AND status = @from RETURNING id");
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("id", id);
        if (await command.ExecuteScalarAsync(ct) is not null)
            return Results.Ok(new { message = $"Organization {status}." });

        await using var exists = db.CreateCommand("SELECT status FROM control.organization WHERE id = @id");
        exists.Parameters.AddWithValue("id", id);
        return await exists.ExecuteScalarAsync(ct) is string current
            ? Results.Conflict(new { error = $"Only {from} organizations can be {status}; this one is {current}." })
            : Results.NotFound(new { error = "Organization not found." });
    }

    private sealed record OrganizationAdminItem(Guid Id, string Name, string Type, string Status, DateTime CreatedAt, string SetupStatus, long UserCount);
}
