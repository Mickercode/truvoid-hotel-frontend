using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace TruvoID.Infrastructure.Postgres;

/// <summary>
/// Turns 'pending' Organizations into live tenants: creates the Organization's own
/// least-privilege login role and schema, applies the tenant migrations, stores the
/// encrypted role password, and marks it 'active'. Each Organization is provisioned
/// in a single transaction (role and schema creation are transactional in Postgres),
/// so a failure leaves nothing half-built and the Organization simply stays pending.
/// Runs as the migrator role — never from the web-facing API.
/// </summary>
public sealed class TenantProvisioner(
    string migratorConnectionString,
    TenantCredentialProtector protector,
    ILogger logger,
    IReadOnlyList<MigrationScript>? tenantScripts = null)
{
    // Caps how many connections one Organization can hold, whatever the pool settings.
    private const int TenantConnectionLimit = 20;

    private readonly IReadOnlyList<MigrationScript> _tenantScripts =
        tenantScripts ?? PostgresMigrator.LoadEmbeddedTenantScripts();

    /// <summary>Provisions every pending Organization; returns how many were provisioned.</summary>
    public async Task<int> ProvisionPendingAsync(CancellationToken ct = default)
    {
        var provisioned = 0;
        while (await ProvisionNextAsync(ct))
            provisioned++;
        return provisioned;
    }

    private async Task<bool> ProvisionNextAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(migratorConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        TenantMigrationTarget? tenant = null;
        // SKIP LOCKED so several workers can provision concurrently without colliding.
        await using (var cmd = new NpgsqlCommand("""
            SELECT id, type, schema_name, db_role_name FROM control.organization
            WHERE status = 'pending'
            ORDER BY created_at
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """, conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
                tenant = new TenantMigrationTarget(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
        }

        if (tenant is null)
            return false;

        if (!PostgresMigrator.IsSafeIdentifier(tenant.SchemaName) || !PostgresMigrator.IsSafeIdentifier(tenant.RoleName))
            throw new InvalidOperationException($"Organization {tenant.OrganizationId} has an unsafe schema or role name.");

        var password = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var database = QuoteIdentifier(conn.Database!);

        // The password goes over the wire only as a SCRAM verifier, so the plaintext
        // never lands in server logs even with statement logging on.
        await PostgresMigrator.ExecuteAsync(conn, tx, $"""
            CREATE ROLE {tenant.RoleName} WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
                CONNECTION LIMIT {TenantConnectionLimit}
                PASSWORD '{ScramSha256Verifier(password)}';
            GRANT CONNECT ON DATABASE {database} TO {tenant.RoleName};
            CREATE SCHEMA {tenant.SchemaName};
            """, ct);

        await PostgresMigrator.ApplyTenantAsync(conn, tx, tenant, _tenantScripts, logger, ct);

        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO control.organization_db_credential (organization_id, password_ciphertext, key_id)
            VALUES (@id, @ciphertext, @keyId)
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("id", tenant.OrganizationId);
            cmd.Parameters.AddWithValue("ciphertext", protector.Protect(tenant.OrganizationId, password));
            cmd.Parameters.AddWithValue("keyId", protector.KeyId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = new NpgsqlCommand(
            "UPDATE control.organization SET status = 'active', updated_at = now() WHERE id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", tenant.OrganizationId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        logger.LogInformation("Provisioned {OrgType} {OrganizationId} (schema {Schema})",
            tenant.OrgType, tenant.OrganizationId, tenant.SchemaName);
        return true;
    }

    /// <summary>
    /// Builds the stored form Postgres uses for SCRAM-SHA-256 passwords (RFC 5802 /
    /// RFC 7677): SCRAM-SHA-256$iterations:salt$StoredKey:ServerKey.
    /// </summary>
    internal static string ScramSha256Verifier(string password, int iterations = 4096)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var saltedPassword = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
        var clientKey = HMACSHA256.HashData(saltedPassword, "Client Key"u8);
        var storedKey = SHA256.HashData(clientKey);
        var serverKey = HMACSHA256.HashData(saltedPassword, "Server Key"u8);

        return $"SCRAM-SHA-256${iterations}:{Convert.ToBase64String(salt)}" +
               $"${Convert.ToBase64String(storedKey)}:{Convert.ToBase64String(serverKey)}";
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
}
