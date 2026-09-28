using System.Text.Json;
using Npgsql;

namespace TruvoID.Infrastructure.Postgres;

/// <summary>
/// Delivers tenant revenue outbox events into the central Slogani ledger. The
/// source event id makes retries idempotent; tenant mutation and event creation
/// remain atomic even if the relay is temporarily unavailable.
/// </summary>
public sealed class RevenueOutboxRelay(string migratorConnectionString)
{
    public async Task<int> RelayAsync(CancellationToken ct = default)
    {
        await using var control = new NpgsqlConnection(migratorConnectionString);
        await control.OpenAsync(ct);
        var tenants = new List<(Guid Id, string Schema)>();
        await using (var command = new NpgsqlCommand(
            "SELECT id, schema_name FROM control.organization WHERE status = 'active'", control))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                tenants.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        var delivered = 0;
        foreach (var tenant in tenants)
            delivered += await RelayTenantAsync(control, tenant.Id, tenant.Schema, ct);
        return delivered;
    }

    private static async Task<int> RelayTenantAsync(
        NpgsqlConnection connection,
        Guid organizationId,
        string schema,
        CancellationToken ct)
    {
        if (!PostgresMigrator.IsSafeIdentifier(schema))
            throw new InvalidOperationException($"Unsafe tenant schema '{schema}'.");

        await using var tx = await connection.BeginTransactionAsync(ct);
        var events = new List<RevenueEvent>();
        await using (var command = new NpgsqlCommand($"""
            SELECT id, payload, occurred_at
            FROM {schema}.outbox
            WHERE event_type = 'revenue' AND delivered_at IS NULL
            ORDER BY occurred_at
            FOR UPDATE SKIP LOCKED
            LIMIT 500
            """, connection, tx))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var payload = JsonDocument.Parse(reader.GetFieldValue<string>(1)).RootElement.Clone();
                events.Add(new RevenueEvent(reader.GetGuid(0), payload));
            }
        }

        foreach (var item in events)
        {
            var payload = item.Payload;
            var amount = payload.GetProperty("amountKobo").GetInt64();
            var entryType = payload.GetProperty("entryType").GetString()
                ?? throw new InvalidOperationException("Revenue event has no entry type.");
            Guid? outletId = payload.TryGetProperty("outletId", out var outlet)
                && outlet.ValueKind != JsonValueKind.Null
                ? outlet.GetGuid()
                : null;
            var reference = payload.TryGetProperty("reference", out var referenceValue)
                ? referenceValue.GetString()
                : null;

            await using (var insert = new NpgsqlCommand("""
                INSERT INTO control.slogani_revenue_ledger
                    (occurred_at, organization_id, outlet_id, entry_type, amount_kobo, credit_batch_ref, source_event_id)
                VALUES (now(), @organizationId, @outletId, @entryType, @amount, @reference, @sourceEventId)
                ON CONFLICT (source_event_id) DO NOTHING
                """, connection, tx))
            {
                insert.Parameters.AddWithValue("organizationId", organizationId);
                insert.Parameters.AddWithValue("outletId", (object?)outletId ?? DBNull.Value);
                insert.Parameters.AddWithValue("entryType", entryType);
                insert.Parameters.AddWithValue("amount", amount);
                insert.Parameters.AddWithValue("reference", (object?)reference ?? DBNull.Value);
                insert.Parameters.AddWithValue("sourceEventId", item.Id);
                await insert.ExecuteNonQueryAsync(ct);
            }

            await using var mark = new NpgsqlCommand($"UPDATE {schema}.outbox SET delivered_at = now() WHERE id = @id", connection, tx);
            mark.Parameters.AddWithValue("id", item.Id);
            await mark.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return events.Count;
    }

    private sealed record RevenueEvent(Guid Id, JsonElement Payload);
}
