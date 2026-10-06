using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace TruvoID.API.Endpoints;

/// <summary>Read-only view of the append-only audit log for platform staff.</summary>
public static class AdminAuditEndpoints
{
    public static IEndpointRouteBuilder MapAdminAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/admin/audit", List).RequireAuthorization("TruvoAdmin");
        return app;
    }

    private static async Task<IResult> List(
        NpgsqlDataSource db,
        int page = 1,
        int pageSize = 50,
        string? action = null,
        string? entity = null,
        Guid? organizationId = null,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(action)) filters.Add("a.action = @action");
        if (!string.IsNullOrWhiteSpace(entity)) filters.Add("a.entity = @entity");
        if (organizationId is { } orgFilter) filters.Add("a.organization_id = @org");
        var where = filters.Count == 0 ? "" : $"WHERE {string.Join(" AND ", filters)}";

        await using var command = db.CreateCommand($"""
            SELECT a.id, a.occurred_at, a.actor_type, a.actor_id, u.email, a.action, a.entity, a.entity_id,
                   a.organization_id, o.name, a.metadata->>'details'
            FROM control.audit_log a
            LEFT JOIN control.app_user u ON u.id = a.actor_id
            LEFT JOIN control.organization o ON o.id = a.organization_id
            {where}
            ORDER BY a.occurred_at DESC, a.id DESC
            LIMIT @pageSize OFFSET @offset
            """);
        if (!string.IsNullOrWhiteSpace(action)) command.Parameters.AddWithValue("action", action.Trim());
        if (!string.IsNullOrWhiteSpace(entity)) command.Parameters.AddWithValue("entity", entity.Trim());
        if (organizationId is { } orgId) command.Parameters.AddWithValue("org", orgId);
        command.Parameters.AddWithValue("pageSize", pageSize);
        command.Parameters.AddWithValue("offset", (page - 1) * pageSize);

        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<AuditLogItem>();
        while (await reader.ReadAsync(ct))
        {
            items.Add(new AuditLogItem(
                reader.GetInt64(0).ToString(),
                reader.GetFieldValue<DateTime>(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetGuid(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }

        return Results.Ok(new { page, pageSize, items });
    }

    private sealed record AuditLogItem(
        string Id,
        DateTime OccurredAt,
        string ActorType,
        Guid? ActorId,
        string? ActorEmail,
        string Action,
        string Entity,
        string? EntityId,
        Guid? OrganizationId,
        string? OrganizationName,
        string? Details);
}
