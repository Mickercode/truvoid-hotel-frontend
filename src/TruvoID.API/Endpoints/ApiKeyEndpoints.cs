using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TruvoID.Core.Interfaces;
using TruvoID.Domain.Enums;
using TruvoID.Infrastructure.Postgres;

namespace TruvoID.API.Endpoints;

public static class ApiKeyEndpoints
{
    private const string KeyPrefixTag = "trv_live_";

    public static IEndpointRouteBuilder MapApiKeyEndpoints(this IEndpointRouteBuilder app)
    {
        var legacy = app.MapGroup("/v1/api-keys").RequireAuthorization();
        legacy.MapGet("/", ListKeys);
        legacy.MapPost("/", CreateKey);
        legacy.MapDelete("/{id:guid}", RevokeKey);

        var tenant = app.MapGroup("/v1/tenant/api-keys").RequireAuthorization("TenantManager");
        tenant.MapGet("/", ListTenantKeys);
        tenant.MapPost("/outlets/{outletId:guid}", CreateOutletKey);
        tenant.MapDelete("/{id:guid}", RevokeTenantKey);
        return app;
    }

    private static async Task<IResult> ListKeys(HttpContext ctx, PostgresApiKeyStore keys, CancellationToken ct)
    {
        var organizationId = ctx.GetOrganizationId();
        if (organizationId == Guid.Empty) return Results.Unauthorized();
        return Results.Ok((await keys.ListAsync(organizationId, ct)).Select(k => MapResponse(k)).ToList());
    }

    private static async Task<IResult> CreateKey(
        HttpContext ctx,
        CreateApiKeyRequest request,
        PostgresApiKeyStore keys,
        IAuditService audit,
        CancellationToken ct)
    {
        var organizationId = ctx.GetOrganizationId();
        if (organizationId == Guid.Empty) return Results.Unauthorized();
        var key = await CreateAsync(keys, organizationId, null, request.Description, ctx.GetUserId(), ct);
        await audit.LogAsync(AuditAction.ApiKeyGenerated, "ApiKey", key.Stored.Id, ctx.GetUserId(), "User", key.Stored.Description);
        return Results.Ok(MapResponse(key.Stored, key.RawKey));
    }

    private static async Task<IResult> RevokeKey(
        HttpContext ctx,
        Guid id,
        PostgresApiKeyStore keys,
        IAuditService audit,
        CancellationToken ct)
    {
        var organizationId = ctx.GetOrganizationId();
        if (organizationId == Guid.Empty) return Results.Unauthorized();
        if (!await keys.RevokeAsync(id, organizationId, ctx.GetUserId(), ct))
            return Results.NotFound(new { error = "API key not found." });
        await audit.LogAsync(AuditAction.ApiKeyRevoked, "ApiKey", id, ctx.GetUserId(), "User");
        return Results.NoContent();
    }

    private static async Task<IResult> ListTenantKeys(HttpContext ctx, PostgresApiKeyStore keys, CancellationToken ct)
    {
        var organizationId = ctx.GetOrganizationId();
        if (organizationId == Guid.Empty) return Results.Unauthorized();
        return Results.Ok((await keys.ListAsync(organizationId, ct)).Select(k => MapResponse(k)).ToList());
    }

    private static async Task<IResult> CreateOutletKey(
        HttpContext ctx,
        Guid outletId,
        CreateApiKeyRequest request,
        PostgresApiKeyStore keys,
        TenantConnectionFactory tenants,
        IAuditService audit,
        CancellationToken ct)
    {
        if (!IsAgencyAdmin(ctx)) return Results.Forbid();
        var organizationId = ctx.GetOrganizationId();
        if (organizationId == Guid.Empty) return Results.Unauthorized();

        await using var session = await tenants.BeginAsync(TenantScope.Organization(organizationId), ct);
        await using var type = session.CreateCommand("SELECT org_type FROM tenant");
        if (!string.Equals((string?)await type.ExecuteScalarAsync(ct), "agency", StringComparison.OrdinalIgnoreCase))
            return Results.Forbid();
        await using var outlet = session.CreateCommand("SELECT id FROM outlet WHERE id = @id");
        outlet.Parameters.AddWithValue("id", outletId);
        if (await outlet.ExecuteScalarAsync(ct) is null)
            return Results.NotFound(new { error = "Outlet not found in this agency." });

        var key = await CreateAsync(keys, organizationId, outletId, request.Description, ctx.GetUserId(), ct);
        await audit.LogAsync(AuditAction.ApiKeyGenerated, "ApiKey", key.Stored.Id, ctx.GetUserId(), "User", key.Stored.Description);
        return Results.Ok(MapResponse(key.Stored, key.RawKey));
    }

    private static async Task<IResult> RevokeTenantKey(
        HttpContext ctx,
        Guid id,
        PostgresApiKeyStore keys,
        IAuditService audit,
        CancellationToken ct)
    {
        if (!IsAgencyAdmin(ctx)) return Results.Forbid();
        var organizationId = ctx.GetOrganizationId();
        if (organizationId == Guid.Empty) return Results.Unauthorized();
        if (!await keys.RevokeAsync(id, organizationId, ctx.GetUserId(), ct))
            return Results.NotFound(new { error = "API key not found." });
        await audit.LogAsync(AuditAction.ApiKeyRevoked, "ApiKey", id, ctx.GetUserId(), "User");
        return Results.NoContent();
    }

    internal static async Task<(PostgresApiKey Stored, string RawKey)> CreateAsync(
        PostgresApiKeyStore keys,
        Guid organizationId,
        Guid? outletId,
        string? description,
        Guid userId,
        CancellationToken ct)
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var rawKey = $"{KeyPrefixTag}{secret}";
        var prefix = rawKey[..(KeyPrefixTag.Length + 8)];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();
        var stored = await keys.CreateAsync(organizationId, outletId, prefix, hash,
            string.IsNullOrWhiteSpace(description) ? null : description.Trim(), userId == Guid.Empty ? null : userId, ct);
        return (stored, rawKey);
    }

    private static bool IsAgencyAdmin(HttpContext ctx) =>
        ctx.User.IsInRole("agency_admin") || ctx.User.FindFirst("tenant_role")?.Value == "agency_admin";

    private static ApiKeyResponse MapResponse(PostgresApiKey key, string? rawKey = null) => new()
    {
        Id = key.Id.ToString(),
        KeyPrefix = key.KeyPrefix,
        Description = key.Description,
        Status = key.Status == "active" ? 0 : 1,
        CreatedAt = key.CreatedAt,
        RawKey = rawKey,
        Scope = key.OutletId.HasValue ? "outlet" : "organization",
        OutletId = key.OutletId
    };

    public sealed record CreateApiKeyRequest(string? Description);
}

public sealed class ApiKeyResponse
{
    public string Id { get; init; } = "";
    public string KeyPrefix { get; init; } = "";
    public string? Description { get; init; }
    public int Status { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? RawKey { get; init; }
    public string Scope { get; init; } = "organization";
    public Guid? OutletId { get; init; }
}
