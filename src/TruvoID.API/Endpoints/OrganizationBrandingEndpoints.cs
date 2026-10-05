using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TruvoID.Infrastructure.Postgres;

namespace TruvoID.API.Endpoints;

public static class OrganizationBrandingEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationBrandingEndpoints(this IEndpointRouteBuilder app)
    {
        // Workspace branding is an organization-wide setting: administrators only.
        var group = app.MapGroup("/v1/tenant/branding").RequireAuthorization("TenantManager")
            .AddEndpointFilter(async (context, next) => context.HttpContext.IsOrganizationAdmin()
                ? await next(context)
                : Results.Json(new { error = "Only your organization's administrator can change branding." },
                    statusCode: StatusCodes.Status403Forbidden));
        group.MapGet("/", Get);
        group.MapPut("/", Save);
        group.MapPost("/logo", UploadLogo).DisableAntiforgery(); // bearer-token API: no CSRF surface
        return app;
    }

    private static async Task<IResult> Get(HttpContext ctx, OrganizationBrandingStore branding, CancellationToken ct)
    {
        return Results.Ok(ToResponse(await branding.GetAsync(ctx.GetOrganizationId(), ct)));
    }

    private static async Task<IResult> Save(HttpContext ctx, BrandingRequest request, OrganizationBrandingStore branding, CancellationToken ct)
    {
        try
        {
            await branding.SaveAsync(ctx.GetOrganizationId(), request.WorkspaceName, request.PrimaryColor, request.AccentColor, request.WelcomeMessage, ct);
            return Results.Ok(ToResponse(await branding.GetAsync(ctx.GetOrganizationId(), ct)));
        }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    private const long MaxLogoBytes = 2 * 1024 * 1024;
    // SVG is intentionally excluded: it can carry script, and the logo is echoed back
    // as a data: URL that a browser may render.
    private static readonly string[] AllowedLogoTypes = ["image/png", "image/jpeg", "image/webp"];

    private static async Task<IResult> UploadLogo(HttpContext ctx, IFormFile file, OrganizationBrandingStore branding, CancellationToken ct)
    {
        if (file is null) return Results.BadRequest(new { error = "Logo file is required." });
        if (file.Length is <= 0 or > MaxLogoBytes)
            return Results.BadRequest(new { error = "Logo must be between 1 byte and 2 MB." });
        if (!AllowedLogoTypes.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Logo must be PNG, JPEG, or WebP." });
        var contentType = AllowedLogoTypes.First(t => string.Equals(t, file.ContentType, StringComparison.OrdinalIgnoreCase));

        await using var stream = file.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, ct);
        try
        {
            await branding.SaveLogoAsync(ctx.GetOrganizationId(), contentType, memory.ToArray(), ct);
            return Results.Ok(ToResponse(await branding.GetAsync(ctx.GetOrganizationId(), ct)));
        }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    private static object ToResponse(OrganizationBranding branding) => new
    {
        organizationId = branding.OrganizationId,
        workspaceName = branding.WorkspaceName,
        primaryColor = branding.PrimaryColor,
        accentColor = branding.AccentColor,
        welcomeMessage = branding.WelcomeMessage,
        logoDataUrl = branding.LogoContent is null || string.IsNullOrWhiteSpace(branding.LogoContentType)
            ? null
            : $"data:{branding.LogoContentType};base64,{Convert.ToBase64String(branding.LogoContent)}"
    };

    public sealed record BrandingRequest(string? WorkspaceName, string PrimaryColor, string AccentColor, string? WelcomeMessage);
}
