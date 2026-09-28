using Microsoft.AspNetCore.Http;
using TruvoID.Infrastructure.Postgres;

namespace TruvoID.API.Endpoints;

public static class HttpContextExtensions
{
    public static Guid GetInstitutionId(this HttpContext ctx)
    {
        var claim = ctx.User.FindFirst("institution_id")
            ?? ctx.User.FindFirst("institutionId");

        if (claim is null || !Guid.TryParse(claim.Value, out var id))
            return Guid.Empty;

        return id;
    }

    public static Guid GetOrganizationId(this HttpContext ctx)
    {
        var claim = ctx.User.FindFirst("organization_id")
            ?? ctx.User.FindFirst("institution_id")
            ?? ctx.User.FindFirst("institutionId");
        return claim is not null && Guid.TryParse(claim.Value, out var id) ? id : Guid.Empty;
    }

    public static Guid? GetOutletId(this HttpContext ctx)
    {
        var claim = ctx.User.FindFirst("outlet_id");
        return claim is not null && Guid.TryParse(claim.Value, out var id) ? id : null;
    }

    public static TenantScope GetTenantScope(this HttpContext ctx)
    {
        var organizationId = ctx.GetOrganizationId();
        if (organizationId == Guid.Empty)
            throw new UnauthorizedAccessException("Organization scope is missing.");
        return ctx.GetOutletId() is { } outletId
            ? TenantScope.Outlet(organizationId, outletId)
            : TenantScope.Organization(organizationId);
    }

    public static Guid GetUserId(this HttpContext ctx)
    {
        var claim = ctx.User.FindFirst("sub")
            ?? ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);

        if (claim is null || !Guid.TryParse(claim.Value, out var id))
            return Guid.Empty;

        return id;
    }

    /// <summary>Set only when the request authenticated via an X-API-Key header rather than a JWT.</summary>
    public static Guid? GetApiKeyId(this HttpContext ctx)
    {
        var claim = ctx.User.FindFirst("api_key_id");
        return claim is not null && Guid.TryParse(claim.Value, out var id) ? id : null;
    }
}
