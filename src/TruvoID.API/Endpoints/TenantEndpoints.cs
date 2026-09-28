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

    public sealed record CreateOutletRequest(string Name);
    public sealed record OutletResponse(Guid Id, string Name, string Status, Guid WalletId, DateTime CreatedAt);
}
