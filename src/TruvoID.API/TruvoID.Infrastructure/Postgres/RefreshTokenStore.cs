using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace TruvoID.Infrastructure.Postgres;

/// <summary>Superseded: rotated moments ago by a concurrent request (another tab) — benign, nothing revoked.</summary>
public enum RefreshOutcome { Rotated, Unknown, Expired, Reused, Superseded }

public sealed record RefreshRotation(RefreshOutcome Outcome, Guid UserId, string? NewToken);

/// <summary>
/// Opaque, rotating refresh tokens (RFC 6819 §5.2.2.3 / OAuth 2.1 rotation). The raw
/// token is returned once and only its hash is stored. Reusing a rotated token revokes
/// every token in its family, logging out both the thief and the victim.
/// </summary>
public sealed class RefreshTokenStore(NpgsqlDataSource controlPlane, TimeSpan? lifetime = null)
{
    /// <summary>
    /// A token rotated this recently is treated as a concurrent-refresh race, not theft
    /// (same idea as Auth0's "reuse interval"). Outside it, reuse revokes the family.
    /// </summary>
    public static readonly TimeSpan ReuseGrace = TimeSpan.FromSeconds(30);

    public TimeSpan Lifetime { get; } = lifetime ?? TimeSpan.FromDays(30);

    /// <summary>Starts a new family (a fresh sign-in).</summary>
    public async Task<string> IssueAsync(Guid userId, CancellationToken ct = default)
    {
        await using var conn = await controlPlane.OpenConnectionAsync(ct);
        var (token, _) = await InsertAsync(conn, null, userId, Guid.NewGuid(), ct);
        return token;
    }

    public async Task<RefreshRotation> RotateAsync(string rawToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
            return new(RefreshOutcome.Unknown, Guid.Empty, null);

        await using var conn = await controlPlane.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        Guid id, userId, familyId;
        DateTime expiresAt;
        DateTime? revokedAt;
        bool rotated;
        await using (var find = new NpgsqlCommand("""
            SELECT id, user_id, family_id, expires_at, revoked_at, replaced_by IS NOT NULL
            FROM control.refresh_token WHERE token_hash = @hash FOR UPDATE
            """, conn, tx))
        {
            find.Parameters.AddWithValue("hash", Hash(rawToken));
            await using var reader = await find.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return new(RefreshOutcome.Unknown, Guid.Empty, null);
            (id, userId, familyId, expiresAt, revokedAt, rotated) =
                (reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetDateTime(3),
                 reader.IsDBNull(4) ? null : reader.GetDateTime(4), reader.GetBoolean(5));
        }

        if (revokedAt is { } at && rotated && DateTime.UtcNow - at < ReuseGrace)
            return new(RefreshOutcome.Superseded, userId, null);
        if (revokedAt is not null)
        {
            await RevokeFamilyAsync(conn, tx, familyId, ct);
            await tx.CommitAsync(ct);
            return new(RefreshOutcome.Reused, userId, null);
        }
        if (expiresAt <= DateTime.UtcNow)
            return new(RefreshOutcome.Expired, userId, null);

        var (token, newId) = await InsertAsync(conn, tx, userId, familyId, ct);
        await using (var retire = new NpgsqlCommand(
            "UPDATE control.refresh_token SET revoked_at = now(), replaced_by = @newId WHERE id = @id", conn, tx))
        {
            retire.Parameters.AddWithValue("id", id);
            retire.Parameters.AddWithValue("newId", newId);
            await retire.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new(RefreshOutcome.Rotated, userId, token);
    }

    /// <summary>Signs out one session (the family the token belongs to).</summary>
    public async Task RevokeFamilyOfAsync(string rawToken, CancellationToken ct = default)
    {
        await using var command = controlPlane.CreateCommand("""
            UPDATE control.refresh_token SET revoked_at = now()
            WHERE revoked_at IS NULL
              AND family_id = (SELECT family_id FROM control.refresh_token WHERE token_hash = @hash)
            """);
        command.Parameters.AddWithValue("hash", Hash(rawToken ?? ""));
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Signs out everywhere — after a password change or when an account is disabled.</summary>
    public async Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var command = controlPlane.CreateCommand(
            "UPDATE control.refresh_token SET revoked_at = now() WHERE user_id = @user AND revoked_at IS NULL");
        command.Parameters.AddWithValue("user", userId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<(string Token, Guid Id)> InsertAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid userId, Guid familyId, CancellationToken ct)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var id = Guid.NewGuid();
        await using var insert = new NpgsqlCommand("""
            INSERT INTO control.refresh_token (id, user_id, family_id, token_hash, expires_at)
            VALUES (@id, @user, @family, @hash, @expires)
            """, conn, tx);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("user", userId);
        insert.Parameters.AddWithValue("family", familyId);
        insert.Parameters.AddWithValue("hash", Hash(token));
        insert.Parameters.AddWithValue("expires", DateTime.UtcNow.Add(Lifetime));
        await insert.ExecuteNonQueryAsync(ct);
        return (token, id);
    }

    private static async Task RevokeFamilyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid familyId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "UPDATE control.refresh_token SET revoked_at = now() WHERE family_id = @family AND revoked_at IS NULL", conn, tx);
        command.Parameters.AddWithValue("family", familyId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
