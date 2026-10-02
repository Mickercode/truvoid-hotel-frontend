using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;

namespace TruvoID.Infrastructure.Postgres;

/// <summary>
/// Creates platform_admin accounts (or resets one's password) from the command line.
/// There is deliberately no HTTP endpoint for this: platform admins can reach every
/// Organization, so the only way to mint one is shell access to the deployment.
/// Runs as the migrator role, like migrate and provision-tenants.
/// </summary>
public sealed class PlatformAdminBootstrapper(string migratorConnectionString)
{
    public sealed record Result(Guid UserId, string Email, string Password, bool Created);

    public async Task<Result> CreateAsync(string email, string? fullName, CancellationToken ct = default)
    {
        var normalizedEmail = NormalizeEmail(email);
        var password = GeneratePassword();

        await using var conn = new NpgsqlConnection(migratorConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var existing = new NpgsqlCommand(
            "SELECT role FROM control.app_user WHERE lower(email) = @email", conn, tx))
        {
            existing.Parameters.AddWithValue("email", normalizedEmail);
            if (await existing.ExecuteScalarAsync(ct) is string role)
                throw new InvalidOperationException(role == "platform_admin"
                    ? $"{normalizedEmail} is already a platform admin. Use --reset-password to issue a new password."
                    : $"{normalizedEmail} already belongs to an Organization ({role}). Use a different email.");
        }

        var userId = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO control.app_user (id, email, full_name, credential_hash, role, status)
            VALUES (@id, @email, @fullName, @credentialHash, 'platform_admin', 'active')
            """, conn, tx))
        {
            insert.Parameters.AddWithValue("id", userId);
            insert.Parameters.AddWithValue("email", normalizedEmail);
            insert.Parameters.AddWithValue("fullName", string.IsNullOrWhiteSpace(fullName) ? DBNull.Value : fullName.Trim());
            insert.Parameters.AddWithValue("credentialHash", PasswordHasher.Hash(password));
            await insert.ExecuteNonQueryAsync(ct);
        }

        await AuditAsync(conn, tx, "platform_admin.created", userId, normalizedEmail, ct);
        await tx.CommitAsync(ct);
        return new Result(userId, normalizedEmail, password, Created: true);
    }

    public async Task<Result> ResetPasswordAsync(string email, CancellationToken ct = default)
    {
        var normalizedEmail = NormalizeEmail(email);
        var password = GeneratePassword();

        await using var conn = new NpgsqlConnection(migratorConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        Guid userId;
        await using (var update = new NpgsqlCommand("""
            UPDATE control.app_user
            SET credential_hash = @credentialHash, status = 'active', updated_at = now()
            WHERE lower(email) = @email AND role = 'platform_admin'
            RETURNING id
            """, conn, tx))
        {
            update.Parameters.AddWithValue("email", normalizedEmail);
            update.Parameters.AddWithValue("credentialHash", PasswordHasher.Hash(password));
            userId = await update.ExecuteScalarAsync(ct) as Guid?
                ?? throw new InvalidOperationException($"No platform admin with email {normalizedEmail}.");
        }

        await AuditAsync(conn, tx, "platform_admin.password_reset", userId, normalizedEmail, ct);
        await tx.CommitAsync(ct);
        return new Result(userId, normalizedEmail, password, Created: false);
    }

    private static async Task AuditAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string action, Guid userId, string email, CancellationToken ct)
    {
        await using var audit = new NpgsqlCommand("""
            INSERT INTO control.audit_log (occurred_at, actor_type, actor_id, action, entity, entity_id, metadata)
            VALUES (now(), 'system', NULL, @action, 'User', @entityId, @metadata::jsonb)
            """, conn, tx);
        audit.Parameters.AddWithValue("action", action);
        audit.Parameters.AddWithValue("entityId", userId.ToString());
        audit.Parameters.AddWithValue("metadata", JsonSerializer.Serialize(new { email, source = "cli" }));
        await audit.ExecuteNonQueryAsync(ct);
    }

    private static string NormalizeEmail(string email)
    {
        var trimmed = email?.Trim().ToLowerInvariant() ?? string.Empty;
        var at = trimmed.IndexOf('@');
        if (at <= 0 || at != trimmed.LastIndexOf('@') || at == trimmed.Length - 1 || trimmed.Contains(' '))
            throw new ArgumentException($"'{email}' is not a valid email address.");
        return trimmed;
    }

    // 20 chars from an unambiguous alphabet (~115 bits); shown once, changed after first login.
    private static string GeneratePassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return RandomNumberGenerator.GetString(alphabet, 20);
    }
}
