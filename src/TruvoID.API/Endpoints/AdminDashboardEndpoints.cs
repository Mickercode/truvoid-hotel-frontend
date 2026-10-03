using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using TruvoID.Core.Interfaces;
using TruvoID.Domain.Enums;
using TruvoID.Infrastructure.Postgres;

namespace TruvoID.API.Endpoints;

public static class AdminDashboardEndpoints
{
    public static IEndpointRouteBuilder MapAdminDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/admin").RequireAuthorization("TruvoAdmin");
        group.MapGet("/api-keys", GetAllApiKeys);
        group.MapPost("/api-keys/tenants", CreateTenantApiKey);
        group.MapPost("/api-keys/{id:guid}/revoke", RevokeApiKey);
        return app;
    }

    private static async Task<IResult> GetAllApiKeys(PostgresApiKeyStore keys, CancellationToken ct)
    {
        var result = (await keys.ListAllAsync(ct)).Select(key => new AdminApiKeyResponse
        {
            Id = key.Id,
            OrganizationId = key.OrganizationId,
            OutletId = key.OutletId,
            KeyPrefix = key.KeyPrefix,
            Description = key.Description,
            Scope = key.OutletId.HasValue ? "outlet" : "organization",
            Status = key.Status,
            CallCount = key.CallCount,
            CreatedAt = key.CreatedAt,
            LastUsedAt = key.LastUsedAt
        });
        return Results.Ok(result);
    }

    private static async Task<IResult> CreateTenantApiKey(
        HttpContext ctx,
        CreateTenantApiKeyRequest request,
        PostgresApiKeyStore keys,
        NpgsqlDataSource controlPlane,
        TenantConnectionFactory tenants,
        OrganizationSetupStore setup,
        IAuditService audit,
        CancellationToken ct)
    {
        await using var organization = controlPlane.CreateCommand("""
            SELECT name, status FROM control.organization WHERE id = @id
            """);
        organization.Parameters.AddWithValue("id", request.OrganizationId);
        await using var reader = await organization.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return Results.NotFound(new { error = "Tenant organization not found." });
        var organizationName = reader.GetString(0);
        if (!string.Equals(reader.GetString(1), "active", StringComparison.OrdinalIgnoreCase))
            return Results.Conflict(new { error = "Tenant organization is not active." });
        await reader.DisposeAsync();

        if (request.OutletId is { } outletId)
        {
            await using var session = await tenants.BeginAsync(TenantScope.Organization(request.OrganizationId), ct);
            await using var outlet = session.CreateCommand("SELECT id FROM outlet WHERE id = @id");
            outlet.Parameters.AddWithValue("id", outletId);
            if (await outlet.ExecuteScalarAsync(ct) is null)
                return Results.NotFound(new { error = "Outlet not found in this tenant." });
        }

        if (await ApiKeyEndpoints.CheckEnvironmentAsync(request.Environment, request.OrganizationId, setup, ct) is { } refusal)
            return refusal;
        var created = await ApiKeyEndpoints.CreateAsync(
            keys, request.OrganizationId, request.OutletId, request.Description, ctx.GetUserId(), ct, request.Environment);
        await audit.LogAsync(AuditAction.ApiKeyGenerated, "ApiKey", created.Stored.Id,
            ctx.GetUserId(), "User", $"Platform-generated key for {organizationName}", ct);

        return Results.Ok(new ApiKeyResponse
        {
            Id = created.Stored.Id.ToString(),
            KeyPrefix = created.Stored.KeyPrefix,
            Description = created.Stored.Description,
            Status = 0,
            CreatedAt = created.Stored.CreatedAt,
            RawKey = created.RawKey,
            Scope = created.Stored.OutletId.HasValue ? "outlet" : "organization",
            OutletId = created.Stored.OutletId
        });
    }

    private static async Task<IResult> RevokeApiKey(
        Guid id,
        HttpContext ctx,
        PostgresApiKeyStore keys,
        IAuditService audit,
        CancellationToken ct)
    {
        if (!await keys.RevokeAsync(id, null, ctx.GetUserId(), ct))
            return Results.NotFound(new { error = "API key not found." });
        await audit.LogAsync(AuditAction.ApiKeyRevoked, "ApiKey", id, ctx.GetUserId(), "User", "Revoked by platform admin", ct);
        return Results.Ok(new { message = "API key revoked." });
    }

    public sealed record CreateTenantApiKeyRequest(Guid OrganizationId, Guid? OutletId, string? Description, string? Environment = null);

    private sealed class AdminApiKeyResponse
    {
        public Guid Id { get; init; }
        public Guid OrganizationId { get; init; }
        public Guid? OutletId { get; init; }
        public string KeyPrefix { get; init; } = "";
        public string? Description { get; init; }
        public string Scope { get; init; } = "organization";
        public string Status { get; init; } = "active";
        public long CallCount { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? LastUsedAt { get; init; }
    }
}
