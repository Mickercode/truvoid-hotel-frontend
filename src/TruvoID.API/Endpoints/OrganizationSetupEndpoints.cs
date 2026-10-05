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
        // The organization profile (and its uploaded documents) is administrator-only.
        group.AddEndpointFilter(async (context, next) => context.HttpContext.IsOrganizationAdmin()
            ? await next(context)
            : Results.Json(new { error = "Only your organization's administrator can edit the organization profile." },
                statusCode: StatusCodes.Status403Forbidden));
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

    private const long MaxDocumentBytes = 10 * 1024 * 1024;

    // Validation is by file extension, not the browser-supplied content type: browsers
    // routinely send application/octet-stream (or the wrong type) for PDFs and Office
    // files, which made legitimate uploads fail. The stored content type is chosen here,
    // never trusted from the client, and documents are served as attachments.
    private static readonly Dictionary<string, string> AllowedDocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".csv"] = "text/csv",
        [".txt"] = "text/plain",
    };

    private static async Task<IResult> UploadDocument(HttpContext ctx, IFormFile file, [Microsoft.AspNetCore.Mvc.FromForm] string documentType, OrganizationSetupStore setup, CancellationToken ct) // documentType arrives as a multipart field, not a query parameter
    {
        if (string.IsNullOrWhiteSpace(documentType) || file is null)
            return Results.BadRequest(new { error = "Document type and file are required." });
        // Validate size before buffering the body into memory.
        if (file.Length is <= 0 or > MaxDocumentBytes)
            return Results.BadRequest(new { error = "Documents must be between 1 byte and 10 MB." });
        if (!AllowedDocumentExtensions.TryGetValue(Path.GetExtension(file.FileName), out var contentType))
            return Results.BadRequest(new { error = "Upload a PDF, image, Word, Excel, CSV, or text document." });
        var fileName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "document";

        await using var stream = file.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, ct);
        try
        {
            var document = await setup.AddDocumentAsync(ctx.GetOrganizationId(), ctx.GetUserId(), documentType, fileName, contentType, memory.ToArray(), ct);
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

        // Tell the caller exactly what's still missing instead of a generic refusal.
        var missing = new List<string>();
        var missingSections = RequiredSections.Where(section => IsEmpty(snapshot.Sections[section])).ToArray();
        if (missingSections.Length > 0) missing.Add($"the {string.Join(", ", missingSections)} section(s)");
        if (snapshot.AccessLevel is not (>= 1 and <= 5)) missing.Add("an access level");
        if (!snapshot.AttestedAt.HasValue) missing.Add("the attestation");
        if (snapshot.Documents.Count == 0) missing.Add("at least one document");
        if (missing.Count > 0)
            return Results.Conflict(new { error = $"Before submitting, add {string.Join("; ", missing)}.", code = "setup_incomplete" });

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

    // A section counts as provided only if it has at least one meaningful value.
    // Previously {"field":""} (or only unchecked booleans) counted as complete, so a
    // blank profile could be marked 100% and submitted, and the reviewer then saw
    // "Not provided" everywhere.
    private static bool IsEmpty(string json)
    {
        if (json is "{}" or "null" or "[]") return true;
        try
        {
            using var document = JsonDocument.Parse(json);
            return !HasMeaningfulValue(document.RootElement);
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static bool HasMeaningfulValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Any(property => HasMeaningfulValue(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().Any(HasMeaningfulValue),
        JsonValueKind.String => !string.IsNullOrWhiteSpace(element.GetString()),
        JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
        _ => true, // numbers, true
    };

    public sealed record AccessLevelRequest(short Level);
    public sealed record AttestationRequest(bool Accepted);
}
