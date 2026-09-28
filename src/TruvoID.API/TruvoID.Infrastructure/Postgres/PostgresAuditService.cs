using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using TruvoID.Core.Interfaces;
using TruvoID.Domain.Enums;

namespace TruvoID.Infrastructure.Postgres;

public sealed class PostgresAuditService(
    NpgsqlDataSource dataSource,
    ILogger<PostgresAuditService> logger) : IAuditService
{
    public async Task LogAsync(
        AuditAction action,
        string entity,
        Guid entityId,
        Guid? actorId = null,
        string? actorType = null,
        string? details = null,
        CancellationToken ct = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand("""
                INSERT INTO control.audit_log
                    (occurred_at, actor_type, actor_id, action, entity, entity_id, metadata)
                VALUES
                    (now(), @actorType, @actorId, @action, @entity, @entityId, @metadata)
                """);
            command.Parameters.AddWithValue("actorType", (actorType ?? "system").ToLowerInvariant() switch
            {
                "user" => "user",
                "apikey" => "api_key",
                "api_key" => "api_key",
                _ => "system"
            });
            command.Parameters.AddWithValue("actorId", (object?)actorId ?? DBNull.Value);
            command.Parameters.AddWithValue("action", action.ToString());
            command.Parameters.AddWithValue("entity", entity);
            command.Parameters.AddWithValue("entityId", entityId.ToString());
            command.Parameters.Add(new NpgsqlParameter("metadata", NpgsqlDbType.Jsonb)
            {
                Value = JsonSerializer.Serialize(new { details })
            });
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist PostgreSQL audit entry for {Action} {Entity} {EntityId}", action, entity, entityId);
        }
    }
}
