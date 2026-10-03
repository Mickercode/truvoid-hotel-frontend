using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TruvoID.Infrastructure.Postgres;

namespace TruvoID.API.Endpoints;

public static class OrganizationSetupEndpoints
{
    private static readonly string[] RequiredSections = ["general", "contacts", "business", "ownership", "directors", "services", "compliance", "legal"];

    public static IEndpointRouteBuilder MapOrganizationSetupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/tenant/setup").RequireAuthorization("TenantManager");
        // Any edit while the profile is under review or approved → 409 with a clear reason.
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (SetupLockedException ex) { return Results.Conflict(new { error = ex.Message, code = "setup_locked" }); }
        });
        group.MapGet("/", Get);
        group.MapPut("/{section}", SaveSection);
        group.MapPut("/access-level", SaveAccessLevel);
        group.MapPut("/attestation", SaveAttestation);
        // Bearer-token API (no cookies), so CSRF anti-forgery doesn't apply; without this every upload 500s.
        group.MapPost("/documents", UploadDocument).DisableAntiforgery();
        group.MapPost("/submit", Submit);
        return app;
    }

    private static async Task<IResult> Get(HttpContext ctx, OrganizationSetupStore setup, CancellationToken ct)
    {
        var snapshot = await setup.GetAsync(ctx.GetOrganizationId(), ct);
        return Results.Ok(ToResponse(snapshot));
    }

    private static async Task<IResult> SaveSection(HttpContext ctx, string section, JsonElement payload, OrganizationSetupStore setup, CancellationToken ct)
    {
        try
        {
            await setup.SaveSectionAsync(ctx.GetOrganizationId(), section, payload, ct);
            return Results.Ok(new { message = "Section saved." });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> SaveAccessLevel(HttpContext ctx, AccessLevelRequest request, OrganizationSetupStore setup, CancellationToken ct)
    {
        try
        {
            await setup.SaveAccessLevelAsync(ctx.GetOrganizationId(), request.Level, ct);
            return Results.Ok(new { message = "Access level saved." });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> SaveAttestation(HttpContext ctx, AttestationRequest request, OrganizationSetupStore setup, CancellationToken ct)
    {
        await setup.SetAttestationAsync(ctx.GetOrganizationId(), request.Accepted, ct);
        return Results.Ok(new { message = "Attestation saved." });
    }

    private static async Task<IResult> UploadDocument(HttpContext ctx, IFormFile file, [Microsoft.AspNetCore.Mvc.FromForm] string documentType, OrganizationSetupStore setup, CancellationToken ct) // documentType arrives as a multipart field, not a query parameter
    {
        if (string.IsNullOrWhiteSpace(documentType) || file is null)
            return Results.BadRequest(new { error = "Document type and file are required." });
        await using var stream = file.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, ct);
        try
        {
            var document = await setup.AddDocumentAsync(ctx.GetOrganizationId(), ctx.GetUserId(), documentType, file.FileName, file.ContentType, memory.ToArray(), ct);
            return Results.Ok(document);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> Submit(HttpContext ctx, OrganizationSetupStore setup, CancellationToken ct)
    {
        var snapshot = await setup.GetAsync(ctx.GetOrganizationId(), ct);
        var complete = RequiredSections.All(section => !IsEmpty(snapshot.Sections[section]))
            && snapshot.AccessLevel is >= 1 and <= 5
            && snapshot.AttestedAt.HasValue
            && snapshot.Documents.Count > 0;
        if (!complete)
            return Results.Conflict(new { error = "Complete the required organization sections, one access level, one document, and the attestation before submitting." });
        if (!await setup.SubmitAsync(ctx.GetOrganizationId(), ct))
            return Results.Conflict(new { error = "Your profile is already under review or approved.", code = "setup_locked" });
        return Results.Ok(new { message = "Organization profile submitted. TruvoID will review it and unlock live verification once approved." });
    }

    internal static object ToResponse(OrganizationSetupSnapshot snapshot) => new
    {
        organizationId = snapshot.OrganizationId,
        sections = snapshot.Sections.ToDictionary(pair => pair.Key, pair => JsonDocument.Parse(pair.Value).RootElement.Clone()),
        accessLevel = snapshot.AccessLevel,
        attested = snapshot.AttestedAt.HasValue,
        status = snapshot.Status,
        reviewNote = snapshot.ReviewNote,
        submittedAt = snapshot.SubmittedAt,
        reviewedAt = snapshot.ReviewedAt,
        editable = snapshot.Status is not ("submitted" or "approved"),
        documents = snapshot.Documents,
        progress = CalculateProgress(snapshot)
    };

    private static int CalculateProgress(OrganizationSetupSnapshot snapshot)
    {
        var complete = RequiredSections.Count(section => !IsEmpty(snapshot.Sections[section]));
        complete += snapshot.AccessLevel is >= 1 and <= 5 ? 1 : 0;
        complete += snapshot.AttestedAt.HasValue ? 1 : 0;
        complete += snapshot.Documents.Count > 0 ? 1 : 0;
        return (int)Math.Round(complete * 100d / (RequiredSections.Length + 3));
    }

    private static bool IsEmpty(string json) => json is "{}" or "null" or "[]";

    public sealed record AccessLevelRequest(short Level);
    public sealed record AttestationRequest(bool Accepted);
}
