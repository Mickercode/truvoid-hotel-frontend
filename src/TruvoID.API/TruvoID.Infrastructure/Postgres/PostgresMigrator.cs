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
/// Applies the control-plane SQL migrations. Runs as the DDL-owning migrator role,
/// never as the runtime app role (build doc §3.2) — invoked via the "migrate"
/// command, not at app startup, so the running API never holds DDL credentials.
/// </summary>
public static class PostgresMigrator
{
    private const string ControlPlaneResourcePrefix = "ControlPlane.";

    // Arbitrary but fixed: serializes concurrent migrator runs (e.g. overlapping deploys).
    private const long AdvisoryLockKey = 7_378_541_220_931_001;

    private static readonly Regex RoleNamePattern = new("^[a-z_][a-z0-9_]{0,62}$");

    public static IReadOnlyList<MigrationScript> LoadEmbeddedControlPlaneScripts()
    {
        var assembly = typeof(PostgresMigrator).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ControlPlaneResourcePrefix, StringComparison.Ordinal)
                        && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n =>
            {
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream);
                return new MigrationScript(n[ControlPlaneResourcePrefix.Length..^".sql".Length], reader.ReadToEnd());
            })
            .ToList();
    }

    /// <summary>Applies any unapplied scripts in version order; returns how many were applied.</summary>
    public static async Task<int> MigrateAsync(
        string connectionString,
        string appRole,
        IReadOnlyList<MigrationScript> scripts,
        ILogger logger,
        CancellationToken ct = default)
    {
        // Substituted unquoted into GRANT statements, so it must be a plain identifier.
        if (!RoleNamePattern.IsMatch(appRole))
            throw new ArgumentException($"Invalid Postgres role name '{appRole}'.", nameof(appRole));

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        await ExecuteAsync(conn, null, $"SELECT pg_advisory_lock({AdvisoryLockKey})", ct);
        try
        {
            await ExecuteAsync(conn, null, """
                CREATE SCHEMA IF NOT EXISTS control;
                CREATE TABLE IF NOT EXISTS control.schema_migrations (
                    version    text PRIMARY KEY,
                    checksum   text NOT NULL,
                    applied_at timestamptz NOT NULL DEFAULT now()
                );
                """, ct);

            var applied = new Dictionary<string, string>();
            await using (var cmd = new NpgsqlCommand("SELECT version, checksum FROM control.schema_migrations", conn))
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
                            $"Migration {script.Version} was modified after it was applied. " +
                            "Add a new migration instead of editing an applied one.");
                    continue;
                }

                await using var tx = await conn.BeginTransactionAsync(ct);
                await ExecuteAsync(conn, tx, script.Sql.Replace("{{app_role}}", appRole), ct);
                await using (var record = new NpgsqlCommand(
                    "INSERT INTO control.schema_migrations (version, checksum) VALUES (@version, @checksum)", conn, tx))
                {
                    record.Parameters.AddWithValue("version", script.Version);
                    record.Parameters.AddWithValue("checksum", script.Checksum);
                    await record.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);

                logger.LogInformation("Applied control-plane migration {Version}", script.Version);
                appliedCount++;
            }

            return appliedCount;
        }
        finally
        {
            await ExecuteAsync(conn, null, $"SELECT pg_advisory_unlock({AdvisoryLockKey})", CancellationToken.None);
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
