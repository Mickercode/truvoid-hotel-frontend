using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace TruvoID.Infrastructure.Postgres;

/// <summary>A versioned SQL script; Version is the file name without extension (e.g. "0001_control_plane").</summary>
public sealed record MigrationScript(string Version, string Sql)
{
    // Line endings normalized so a CRLF checkout on Windows and an LF checkout on
    // Linux don't look like an edited migration. Computed on read, not stored, so a
    // `with` copy can't carry a stale checksum.
    public string Checksum => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Sql.Replace("\r\n", "\n")))).ToLowerInvariant();
}

/// <summary>
/// Applies the control-plane and per-Organization SQL migrations. Runs as the
/// DDL-owning migrator role, never as a runtime role (build doc §3.2) — invoked
/// via the "migrate" command, not at app startup, so the running API never holds
/// DDL credentials.
/// </summary>
public static partial class PostgresMigrator
{
    private const string ControlPlaneResourcePrefix = "ControlPlane.";
    private const string TenantResourcePrefix = "Tenant.";

    // Arbitrary but fixed: serializes concurrent migrator runs (e.g. overlapping deploys).
    private const long AdvisoryLockKey = 7_378_541_220_931_001;

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex IdentifierPattern();

    /// <summary>True for a plain lowercase Postgres identifier that is safe to splice into DDL unquoted.</summary>
    public static bool IsSafeIdentifier(string value) => IdentifierPattern().IsMatch(value);

    public static IReadOnlyList<MigrationScript> LoadEmbeddedControlPlaneScripts() => LoadEmbedded(ControlPlaneResourcePrefix);

    public static IReadOnlyList<MigrationScript> LoadEmbeddedTenantScripts() => LoadEmbedded(TenantResourcePrefix);

    /// <summary>Brings the control-plane schema up to date; returns how many scripts were applied.</summary>
    public static async Task<int> MigrateAsync(
        string connectionString,
        string appRole,
        IReadOnlyList<MigrationScript> scripts,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (!IsSafeIdentifier(appRole))
            throw new ArgumentException($"Invalid Postgres role name '{appRole}'.", nameof(appRole));

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        await ExecuteAsync(conn, null, $"SELECT pg_advisory_lock({AdvisoryLockKey})", ct);
        try
        {
            await ExecuteAsync(conn, null, "CREATE SCHEMA IF NOT EXISTS control", ct);
            return await ApplyAsync(conn, null, "control", scripts,
                new Dictionary<string, string> { ["app_role"] = appRole }, logger, ct);
        }
        finally
        {
            await ExecuteAsync(conn, null, $"SELECT pg_advisory_unlock({AdvisoryLockKey})", CancellationToken.None);
        }
    }

    /// <summary>
    /// Brings every active Organization's schema up to the latest tenant scripts.
    /// Pending Organizations are skipped — the provisioner applies their full set.
    /// </summary>
    public static async Task<int> MigrateTenantsAsync(
        string connectionString,
        IReadOnlyList<MigrationScript> scripts,
        ILogger logger,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        await ExecuteAsync(conn, null, $"SELECT pg_advisory_lock({AdvisoryLockKey})", ct);
        try
        {
            var tenants = new List<TenantMigrationTarget>();
            await using (var cmd = new NpgsqlCommand(
                "SELECT id, type, schema_name, db_role_name FROM control.organization WHERE status <> 'pending' ORDER BY created_at", conn))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    tenants.Add(new TenantMigrationTarget(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }

            var applied = 0;
            foreach (var tenant in tenants)
                applied += await ApplyTenantAsync(conn, null, tenant, scripts, logger, ct);
            return applied;
        }
        finally
        {
            await ExecuteAsync(conn, null, $"SELECT pg_advisory_unlock({AdvisoryLockKey})", CancellationToken.None);
        }
    }

    internal static Task<int> ApplyTenantAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        TenantMigrationTarget tenant,
        IReadOnlyList<MigrationScript> scripts,
        ILogger logger,
        CancellationToken ct)
    {
        if (!IsSafeIdentifier(tenant.SchemaName) || !IsSafeIdentifier(tenant.RoleName))
            throw new InvalidOperationException($"Organization {tenant.OrganizationId} has an unsafe schema or role name.");
        if (tenant.OrgType is not ("institution" or "agency"))
            throw new InvalidOperationException($"Organization {tenant.OrganizationId} has unknown type '{tenant.OrgType}'.");

        return ApplyAsync(conn, tx, tenant.SchemaName, scripts, new Dictionary<string, string>
        {
            ["schema"] = tenant.SchemaName,
            ["tenant_role"] = tenant.RoleName,
            ["organization_id"] = tenant.OrganizationId.ToString(),
            ["org_type"] = tenant.OrgType,
        }, logger, ct);
    }

    /// <summary>
    /// Applies unapplied scripts in version order, tracked in {schema}.schema_migrations.
    /// With <paramref name="tx"/> everything runs inside the caller's transaction;
    /// without it, each script gets its own.
    /// </summary>
    private static async Task<int> ApplyAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        string schema,
        IReadOnlyList<MigrationScript> scripts,
        IReadOnlyDictionary<string, string> substitutions,
        ILogger logger,
        CancellationToken ct)
    {
        var historyTable = $"{schema}.schema_migrations";
        await ExecuteAsync(conn, tx, $"""
            CREATE TABLE IF NOT EXISTS {historyTable} (
                version    text PRIMARY KEY,
                checksum   text NOT NULL,
                applied_at timestamptz NOT NULL DEFAULT now()
            )
            """, ct);

        var applied = new Dictionary<string, string>();
        await using (var cmd = new NpgsqlCommand($"SELECT version, checksum FROM {historyTable}", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                applied[reader.GetString(0)] = reader.GetString(1);
        }

        var appliedCount = 0;
        foreach (var script in scripts.OrderBy(s => s.Version, StringComparer.Ordinal))
        {
            if (applied.TryGetValue(script.Version, out var checksum))
            {
                if (checksum != script.Checksum)
                    throw new InvalidOperationException(
                        $"Migration {schema}/{script.Version} was modified after it was applied. " +
                        "Add a new migration instead of editing an applied one.");
                continue;
            }

            var sql = Substitute(script, substitutions);
            var scriptTx = tx ?? await conn.BeginTransactionAsync(ct);
            try
            {
                await ExecuteAsync(conn, scriptTx, sql, ct);
                await using (var record = new NpgsqlCommand(
                    $"INSERT INTO {historyTable} (version, checksum) VALUES (@version, @checksum)", conn, scriptTx))
                {
                    record.Parameters.AddWithValue("version", script.Version);
                    record.Parameters.AddWithValue("checksum", script.Checksum);
                    await record.ExecuteNonQueryAsync(ct);
                }
                if (tx is null)
                    await scriptTx.CommitAsync(ct);
            }
            finally
            {
                if (tx is null)
                    await scriptTx.DisposeAsync();
            }

            logger.LogInformation("Applied migration {Schema}/{Version}", schema, script.Version);
            appliedCount++;
        }

        return appliedCount;
    }

    private static string Substitute(MigrationScript script, IReadOnlyDictionary<string, string> substitutions)
    {
        var sql = substitutions.Aggregate(script.Sql, (current, pair) => current.Replace($"{{{{{pair.Key}}}}}", pair.Value));
        if (sql.Contains("{{", StringComparison.Ordinal))
            throw new InvalidOperationException($"Migration {script.Version} has a placeholder with no value.");
        return sql;
    }

    private static IReadOnlyList<MigrationScript> LoadEmbedded(string prefix)
    {
        var assembly = typeof(PostgresMigrator).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n =>
            {
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream);
                return new MigrationScript(n[prefix.Length..^".sql".Length], reader.ReadToEnd());
            })
            .ToList();
    }

    internal static async Task ExecuteAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

internal sealed record TenantMigrationTarget(Guid OrganizationId, string OrgType, string SchemaName, string RoleName);
