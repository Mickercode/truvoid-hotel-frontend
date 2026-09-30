using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace TruvoID.API.Endpoints;

public static class AdminOrganizationEndpoints
{
    public static IEndpointRouteBuilder MapAdminOrganizationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/admin/organizations").RequireAuthorization("TruvoAdmin");
        group.MapGet("/", List);
        group.MapPost("/{id:guid}/suspend", (Guid id, NpgsqlDataSource db, CancellationToken ct) => SetStatus(id, "suspended", db, ct));
        group.MapPost("/{id:guid}/reactivate", (Guid id, NpgsqlDataSource db, CancellationToken ct) => SetStatus(id, "active", db, ct));
        return app;
    }

    private static async Task<IResult> List(NpgsqlDataSource db, CancellationToken ct)
    {
        await using var command = db.CreateCommand("""
            SELECT o.id, o.name, o.type, o.status, o.created_at,
                   coalesce(s.status, 'incomplete'),
                   (SELECT count(*) FROM control.app_user u WHERE u.organization_id = o.id)
            FROM control.organization o
            LEFT JOIN control.organization_setup s ON s.organization_id = o.id
            ORDER BY o.created_at DESC
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var organizations = new List<OrganizationAdminItem>();
        while (await reader.ReadAsync(ct))
            organizations.Add(new OrganizationAdminItem(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTime>(4), reader.GetString(5), reader.GetInt64(6)));
        return Results.Ok(organizations);
    }

    private static async Task<IResult> SetStatus(Guid id, string status, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var command = db.CreateCommand("UPDATE control.organization SET status = @status, updated_at = now() WHERE id = @id");
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteNonQueryAsync(ct) == 1
            ? Results.Ok(new { message = $"Organization {status}." })
            : Results.NotFound(new { error = "Organization not found." });
    }

    private sealed record OrganizationAdminItem(Guid Id, string Name, string Type, string Status, DateTime CreatedAt, string SetupStatus, long UserCount);
}
