using Npgsql;
using NpgsqlTypes;

namespace TruvoID.Infrastructure.Postgres;

public sealed record PostgresApiKey(
    Guid Id,
    Guid OrganizationId,
    Guid? OutletId,
    string KeyPrefix,
    string KeyHash,
    string? Description,
    string Status,
    DateTime CreatedAt,
    DateTime? LastUsedAt,
    DateTime? RevokedAt,
    long CallCount)
{
    /// <summary>"test" or "live", from the prefix TruvoID issued the key with.</summary>
    public string Environment => KeyPrefix.StartsWith("trv_test_", StringComparison.Ordinal) ? "test" : "live";
}

/// <param name="environment">"live" or "test". A sandbox deployment issues test keys
/// (trv_test_…) so a key's prefix always says which environment it belongs to.</param>
public sealed class PostgresApiKeyStore(NpgsqlDataSource dataSource, string environment = "live")
{
    public string Environment { get; } = environment is "live" or "test"
        ? environment : throw new ArgumentException("API key environment must be 'live' or 'test'.", nameof(environment));

    /// <summary>trv_live_ or trv_test_.</summary>
    public string KeyPrefixTag => $"trv_{Environment}_";

    public async Task<PostgresApiKey?> FindByHashAsync(string keyHash, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT id, organization_id, outlet_id, key_prefix, key_hash,
                   customer_label, status, created_at, last_used_at, revoked_at, call_count
            FROM control.api_key
            WHERE key_hash = @hash
            """);
        command.Parameters.AddWithValue("hash", keyHash);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task MarkUsedAsync(Guid id, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE control.api_key
            SET last_used_at = now(), call_count = call_count + 1
            WHERE id = @id AND status = 'active'
            """);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<PostgresApiKey> CreateAsync(
        Guid organizationId,
        Guid? outletId,
        string keyPrefix,
        string keyHash,
        string? description,
        Guid? createdByUserId,
        CancellationToken ct = default,
        string? environment = null)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO control.api_key
                (organization_id, outlet_id, environment, key_prefix, key_hash,
                 customer_label, scopes, created_by_user_id)
            VALUES (@organizationId, @outletId, @environment, @keyPrefix, @keyHash,
                    @description, @scopes, @createdBy)
            RETURNING id, organization_id, outlet_id, key_prefix, key_hash,
                      customer_label, status, created_at, last_used_at, revoked_at, call_count
            """);
        command.Parameters.AddWithValue("environment", environment ?? Environment);
        command.Parameters.AddWithValue("organizationId", organizationId);
        command.Parameters.AddWithValue("outletId", (object?)outletId ?? DBNull.Value);
        command.Parameters.AddWithValue("keyPrefix", keyPrefix);
        command.Parameters.AddWithValue("keyHash", keyHash);
        command.Parameters.AddWithValue("description", (object?)description ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter("scopes", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = new[] { "verify" } });
        command.Parameters.AddWithValue("createdBy", (object?)createdByUserId ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return Read(reader);
    }

    public async Task<IReadOnlyList<PostgresApiKey>> ListAsync(Guid organizationId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT id, organization_id, outlet_id, key_prefix, key_hash,
                   customer_label, status, created_at, last_used_at, revoked_at, call_count
            FROM control.api_key
            WHERE organization_id = @organizationId
            ORDER BY created_at DESC
            """);
        command.Parameters.AddWithValue("organizationId", organizationId);
        return await ReadManyAsync(command, ct);
    }

    public async Task<IReadOnlyList<PostgresApiKey>> ListAllAsync(CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT id, organization_id, outlet_id, key_prefix, key_hash,
                   customer_label, status, created_at, last_used_at, revoked_at, call_count
            FROM control.api_key
            ORDER BY created_at DESC
            """);
        return await ReadManyAsync(command, ct);
    }

    public async Task<bool> RevokeAsync(Guid id, Guid? organizationId, Guid? revokedByUserId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE control.api_key
            SET status = 'revoked', revoked_at = now(), revoked_by_user_id = @revokedBy
            WHERE id = @id AND (@organizationId IS NULL OR organization_id = @organizationId)
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("organizationId", (object?)organizationId ?? DBNull.Value);
        command.Parameters.AddWithValue("revokedBy", (object?)revokedByUserId ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    private static async Task<IReadOnlyList<PostgresApiKey>> ReadManyAsync(NpgsqlCommand command, CancellationToken ct)
    {
        var keys = new List<PostgresApiKey>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            keys.Add(Read(reader));
        return keys;
    }

    private static PostgresApiKey Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.IsDBNull(2) ? null : reader.GetGuid(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetString(6),
        reader.GetFieldValue<DateTime>(7),
        reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTime>(8),
        reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTime>(9),
        reader.GetInt64(10));
}
