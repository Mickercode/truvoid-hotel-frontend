using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace TruvoID.API.Endpoints;

/// <summary>
/// Platform revenue from the central ledger. Credit sales are income; refunds are
/// negative; outlet resales are visibility only (excluded from revenue).
/// </summary>
public static class AdminFinancialsEndpoints
{
    public static IEndpointRouteBuilder MapAdminFinancialsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/admin/financials", Get).RequireAuthorization("TruvoAdmin");
        return app;
    }

    private static async Task<IResult> Get(NpgsqlDataSource db, int days = 30, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 365);

        long creditSalesKobo, refundsKobo, entries;
        await using (var totals = db.CreateCommand("""
            SELECT coalesce(sum(amount_kobo) FILTER (WHERE entry_type = 'credit_sale'), 0),
                   coalesce(sum(amount_kobo) FILTER (WHERE entry_type = 'refund'), 0),
                   count(*)
            FROM control.slogani_revenue_ledger
            WHERE occurred_at >= now() - make_interval(days => @days)
            """))
        {
            totals.Parameters.AddWithValue("days", days);
            await using var reader = await totals.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            creditSalesKobo = reader.GetInt64(0);
            refundsKobo = reader.GetInt64(1);
            entries = reader.GetInt64(2);
        }

        var byType = new List<object>();
        await using (var command = db.CreateCommand("""
            SELECT coalesce(verification_type, '—'), sum(amount_kobo), count(*)
            FROM control.slogani_revenue_ledger
            WHERE entry_type = 'credit_sale' AND occurred_at >= now() - make_interval(days => @days)
            GROUP BY verification_type
            ORDER BY sum(amount_kobo) DESC
            """))
        {
            command.Parameters.AddWithValue("days", days);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                byType.Add(new { verificationType = reader.GetString(0), amountKobo = reader.GetInt64(1), count = reader.GetInt64(2) });
        }

        var topOrganizations = new List<object>();
        await using (var command = db.CreateCommand("""
            SELECT o.id, o.name, sum(l.amount_kobo)
            FROM control.slogani_revenue_ledger l
            JOIN control.organization o ON o.id = l.organization_id
            WHERE l.entry_type = 'credit_sale' AND l.occurred_at >= now() - make_interval(days => @days)
            GROUP BY o.id, o.name
            ORDER BY sum(l.amount_kobo) DESC
            LIMIT 10
            """))
        {
            command.Parameters.AddWithValue("days", days);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                topOrganizations.Add(new { organizationId = reader.GetGuid(0), name = reader.GetString(1), amountKobo = reader.GetInt64(2) });
        }

        var recent = new List<object>();
        await using (var command = db.CreateCommand("""
            SELECT l.occurred_at, o.name, l.entry_type, l.amount_kobo, l.verification_type, l.credit_batch_ref
            FROM control.slogani_revenue_ledger l
            JOIN control.organization o ON o.id = l.organization_id
            WHERE l.occurred_at >= now() - make_interval(days => @days)
            ORDER BY l.occurred_at DESC
            LIMIT 50
            """))
        {
            command.Parameters.AddWithValue("days", days);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                recent.Add(new
                {
                    occurredAt = reader.GetFieldValue<DateTime>(0),
                    organizationName = reader.GetString(1),
                    entryType = reader.GetString(2),
                    amountKobo = reader.GetInt64(3),
                    verificationType = reader.IsDBNull(4) ? null : reader.GetString(4),
                    reference = reader.IsDBNull(5) ? null : reader.GetString(5),
                });
        }

        return Results.Ok(new
        {
            rangeDays = days,
            totals = new
            {
                creditSalesKobo,
                refundsKobo,
                netKobo = creditSalesKobo + refundsKobo,
                entries,
            },
            byType,
            topOrganizations,
            recent,
        });
    }
}
