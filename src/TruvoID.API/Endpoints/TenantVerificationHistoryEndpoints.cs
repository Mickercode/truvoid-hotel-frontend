using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TruvoID.Infrastructure.Postgres;

namespace TruvoID.API.Endpoints;

public static class TenantVerificationHistoryEndpoints
{
    public static IEndpointRouteBuilder MapTenantVerificationHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/tenant/verification-calls", List)
            .RequireAuthorization("TenantManager");
        return app;
    }

    private static async Task<IResult> List(
        HttpContext ctx,
        TenantConnectionFactory tenants,
        int page = 1,
        int pageSize = 25,
        string? status = null,
        string? type = null,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        await using var session = await tenants.BeginAsync(TenantScope.Organization(ctx.GetOrganizationId()), ct);
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(status)) filters.Add("status = @status");
        if (!string.IsNullOrWhiteSpace(type)) filters.Add("verification_type = @type");
        var where = filters.Count == 0 ? "" : $"WHERE {string.Join(" AND ", filters)}";
        await using var command = session.CreateCommand($"""
            SELECT id, verification_type, status, subject_ref, ledger_entry_id, created_at, completed_at,
                   result->>'verdict', result->'identity'->>'fullName', result->>'environment'
            FROM verification_call
            {where}
            ORDER BY created_at DESC
            LIMIT @pageSize OFFSET @offset
            """);
        if (!string.IsNullOrWhiteSpace(status)) command.Parameters.AddWithValue("status", status.Trim().ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(type)) command.Parameters.AddWithValue("type", type.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("pageSize", pageSize);
        command.Parameters.AddWithValue("offset", (page - 1) * pageSize);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var calls = new List<VerificationHistoryItem>();
        while (await reader.ReadAsync(ct))
            calls.Add(new VerificationHistoryItem(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)[..Math.Min(12, reader.GetString(3).Length)], reader.GetGuid(4), reader.GetFieldValue<DateTime>(5), reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTime>(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9)));
        return Results.Ok(new { page, pageSize, items = calls });
    }

    private sealed record VerificationHistoryItem(Guid Id, string VerificationType, string Status, string SubjectPreview, Guid LedgerEntryId, DateTime CreatedAt, DateTime? CompletedAt,
        string? Verdict, string? FullName, string? Environment);
}
