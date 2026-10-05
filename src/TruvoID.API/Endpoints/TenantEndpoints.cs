using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TruvoID.Infrastructure.Postgres;

namespace TruvoID.API.Endpoints;

public static class TenantEndpoints
{
    public static IEndpointRouteBuilder MapTenantEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/tenant/outlets")
            .RequireAuthorization("TenantManager");

        group.MapGet("/", ListOutlets);
        group.MapPost("/", CreateOutlet);
        group.MapGet("/{outletId:guid}", GetOutlet);
        // Outlet status is an organization-administrator power (see the handler).
        group.MapPost("/{outletId:guid}/suspend", (HttpContext ctx, Guid outletId, TenantConnectionFactory tenants, CancellationToken ct) =>
            SetOutletStatus(ctx, outletId, "suspended", tenants, ct));
        group.MapPost("/{outletId:guid}/reactivate", (HttpContext ctx, Guid outletId, TenantConnectionFactory tenants, CancellationToken ct) =>
            SetOutletStatus(ctx, outletId, "active", tenants, ct));
        return app;
    }

    private static async Task<IResult> ListOutlets(
        HttpContext ctx,
        TenantConnectionFactory tenants,
        CancellationToken ct)
    {
        var organizationId = ctx.GetOrganizationId();
        if (organizationId == Guid.Empty)
            return Results.Unauthorized();

        await using var session = await tenants.BeginAsync(
            TenantScope.Organization(organizationId), ct);
        await using var command = session.CreateCommand("""
            SELECT id, name, status, wallet_id, created_at
            FROM outlet
            ORDER BY created_at
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var outlets = new List<OutletResponse>();
        while (await reader.ReadAsync(ct))
        {
            outlets.Add(new OutletResponse(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetGuid(3),
                reader.GetFieldValue<DateTime>(4)));
        }

        return Results.Ok(outlets);
    }

    private static async Task<IResult> CreateOutlet(
        HttpContext ctx,
        CreateOutletRequest request,
        TenantConnectionFactory tenants,
        CancellationToken ct)
    {
        var organizationId = ctx.GetOrganizationId();
        if (organizationId == Guid.Empty)
            return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.BadRequest(new { error = "Outlet name is required." });

        await using var session = await tenants.BeginAsync(
            TenantScope.Organization(organizationId), ct);
        var outletId = await TenantOutlets.CreateAsync(session, request.Name, ctx.GetUserId(), ct);
        await session.CommitAsync(ct);

        return Results.Created($"/v1/tenant/outlets/{outletId}", new { id = outletId, name = request.Name.Trim() });
    }

    /// <summary>One outlet, with its own wallet balance. Organization scope, so an agency admin sees any of its outlets.</summary>
    private static async Task<IResult> GetOutlet(
        HttpContext ctx,
        Guid outletId,
        TenantConnectionFactory tenants,
        CancellationToken ct)
    {
        var organizationId = ctx.GetOrganizationId();
        if (organizationId == Guid.Empty)
            return Results.Unauthorized();

        await using var session = await tenants.BeginAsync(
            TenantScope.Organization(organizationId), ct);
        await using var command = session.CreateCommand("""
            SELECT o.id, o.name, o.status, o.wallet_id, o.created_at, w.balance_kobo
            FROM outlet o JOIN wallet w ON w.id = o.wallet_id
            WHERE o.id = @id
            """);
        command.Parameters.AddWithValue("id", outletId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return Results.NotFound(new { error = "Outlet not found." });

        return Results.Ok(new OutletDetailResponse(
            reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetGuid(3),
            reader.GetFieldValue<DateTime>(4), reader.GetInt64(5)));
    }

    private static async Task<IResult> SetOutletStatus(
        HttpContext ctx,
        Guid outletId,
        string status,
        TenantConnectionFactory tenants,
        CancellationToken ct)
    {
        if (!ctx.IsOrganizationAdmin())
            return Results.Json(new { error = "Only your organization's administrator can change an outlet's status." },
                statusCode: StatusCodes.Status403Forbidden);
        var organizationId = ctx.GetOrganizationId();
        if (organizationId == Guid.Empty)
            return Results.Unauthorized();

        await using var session = await tenants.BeginAsync(
            TenantScope.Organization(organizationId), ct);
        await using var command = session.CreateCommand(
            "UPDATE outlet SET status = @status, updated_at = now() WHERE id = @id");
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("id", outletId);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            return Results.NotFound(new { error = "Outlet not found." });
        await session.CommitAsync(ct);
        return Results.Ok(new { message = $"Outlet {status}." });
    }

    public sealed record CreateOutletRequest(string Name);
    public sealed record OutletResponse(Guid Id, string Name, string Status, Guid WalletId, DateTime CreatedAt);
    public sealed record OutletDetailResponse(Guid Id, string Name, string Status, Guid WalletId, DateTime CreatedAt, long BalanceKobo);
}
