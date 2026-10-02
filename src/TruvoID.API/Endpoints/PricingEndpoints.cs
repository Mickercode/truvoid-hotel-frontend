using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using TruvoID.Infrastructure.Identity;

namespace TruvoID.API.Endpoints;

/// <summary>
/// Prices are append-only and versioned: a change inserts a new row effective now,
/// so the history of what was charged when is never lost. Resolution (see
/// TenantVerificationService.ResolvePriceAsync): Organization override, else platform rate.
/// </summary>
public static class PricingEndpoints
{
    public static IEndpointRouteBuilder MapPricingEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/v1/admin/pricing").RequireAuthorization("TruvoAdmin");
        admin.MapGet("/", ListPlatformRates);
        admin.MapPut("/{type}", SetPlatformRate);
        admin.MapGet("/organizations/{organizationId:guid}", (Guid organizationId, NpgsqlDataSource db, CancellationToken ct) =>
            EffectiveRatesAsync(db, organizationId, ct));
        admin.MapPut("/organizations/{organizationId:guid}/{type}", SetOrganizationRate);

        // What the signed-in Organization pays — shown before a verification is run.
        app.MapGet("/v1/tenant/pricing", (HttpContext ctx, NpgsqlDataSource db, CancellationToken ct) =>
            EffectiveRatesAsync(db, ctx.GetOrganizationId(), ct)).RequireAuthorization();
        return app;
    }

    private static async Task<IResult> ListPlatformRates(NpgsqlDataSource db, CancellationToken ct)
    {
        await using var command = db.CreateCommand("""
            SELECT t.code, t.name, r.price_kobo, r.cost_kobo, r.effective_from
            FROM control.verification_type t
            LEFT JOIN LATERAL (
                SELECT price_kobo, cost_kobo, effective_from FROM control.platform_rate
                WHERE verification_type = t.code AND effective_from <= now()
                ORDER BY effective_from DESC LIMIT 1) r ON true
            ORDER BY t.code
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rates = new List<object>();
        while (await reader.ReadAsync(ct))
            rates.Add(new
            {
                type = reader.GetString(0),
                name = reader.GetString(1),
                priceKobo = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2),
                costKobo = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3),
                effectiveFrom = reader.IsDBNull(4) ? (DateTime?)null : reader.GetFieldValue<DateTime>(4),
            });
        return Results.Ok(rates);
    }

    private static async Task<IResult> SetPlatformRate(
        HttpContext ctx, string type, SetPlatformRateRequest request, NpgsqlDataSource db, CancellationToken ct)
    {
        if (Validate(type, request.PriceKobo) is { } error) return Results.BadRequest(new { error });
        if (request.CostKobo is < 0 or > MaxKobo) return Results.BadRequest(new { error = "Upstream cost must be between ₦0 and ₦100,000." });

        await using var command = db.CreateCommand("""
            INSERT INTO control.platform_rate (verification_type, price_kobo, cost_kobo, effective_from, created_by_user_id)
            VALUES (@type, @price, @cost, now(), @user)
            """);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("price", request.PriceKobo);
        command.Parameters.AddWithValue("cost", request.CostKobo);
        command.Parameters.AddWithValue("user", ctx.GetUserId());
        await command.ExecuteNonQueryAsync(ct);
        return Results.Ok(new
        {
            type, priceKobo = request.PriceKobo, costKobo = request.CostKobo,
            warning = request.PriceKobo < request.CostKobo ? "Price is below upstream cost — every call will lose money." : null,
        });
    }

    private static async Task<IResult> SetOrganizationRate(
        HttpContext ctx, Guid organizationId, string type, SetOrganizationRateRequest request, NpgsqlDataSource db, CancellationToken ct)
    {
        if (Validate(type, request.PriceKobo) is { } error) return Results.BadRequest(new { error });
        try
        {
            await using var command = db.CreateCommand("""
                INSERT INTO control.organization_rate (organization_id, verification_type, price_kobo, effective_from, created_by_user_id)
                VALUES (@org, @type, @price, now(), @user)
                """);
            command.Parameters.AddWithValue("org", organizationId);
            command.Parameters.AddWithValue("type", type);
            command.Parameters.AddWithValue("price", request.PriceKobo);
            command.Parameters.AddWithValue("user", ctx.GetUserId());
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return Results.NotFound(new { error = "Organization not found." });
        }
        return await EffectiveRatesAsync(db, organizationId, ct);
    }

    private static async Task<IResult> EffectiveRatesAsync(NpgsqlDataSource db, Guid organizationId, CancellationToken ct)
    {
        await using var command = db.CreateCommand("""
            SELECT t.code, t.name,
                   (SELECT price_kobo FROM control.organization_rate
                    WHERE organization_id = @org AND verification_type = t.code AND effective_from <= now()
                    ORDER BY effective_from DESC LIMIT 1),
                   (SELECT price_kobo FROM control.platform_rate
                    WHERE verification_type = t.code AND effective_from <= now()
                    ORDER BY effective_from DESC LIMIT 1)
            FROM control.verification_type t
            ORDER BY t.code
            """);
        command.Parameters.AddWithValue("org", organizationId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rates = new List<object>();
        while (await reader.ReadAsync(ct))
        {
            long? custom = reader.IsDBNull(2) ? null : reader.GetInt64(2);
            long? platform = reader.IsDBNull(3) ? null : reader.GetInt64(3);
            rates.Add(new
            {
                type = reader.GetString(0),
                name = reader.GetString(1),
                priceKobo = custom ?? platform,
                source = custom is not null ? "organization" : platform is not null ? "platform" : "unset",
            });
        }
        return Results.Ok(rates);
    }

    private const long MaxKobo = 10_000_000; // ₦100,000 — a typo guard, not a business limit

    private static string? Validate(string type, long priceKobo)
    {
        if (!IdentitySubject.SupportedTypes.Contains(type))
            return $"Unknown verification type '{type}'. Use one of: {string.Join(", ", IdentitySubject.SupportedTypes)}.";
        if (priceKobo is < 0 or > MaxKobo)
            return "Price must be between ₦0 and ₦100,000.";
        return null;
    }

    public sealed record SetPlatformRateRequest(long PriceKobo, long CostKobo);
    public sealed record SetOrganizationRateRequest(long PriceKobo);
}
