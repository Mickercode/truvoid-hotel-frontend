using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace TruvoID.Infrastructure.Postgres;

/// <summary>
/// Single-use, short-lived password reset tokens. Issuing a new link invalidates any
/// earlier unused one, and redeeming changes the password and consumes the token in
/// one transaction, so a link can never be used twice.
/// </summary>
public sealed class PasswordResetStore(NpgsqlDataSource controlPlane)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    /// <summary>Returns the raw token to email, or null when there's no active user with that email.</summary>
    public async Task<(string Token, string Email, string? FullName)?> IssueAsync(string email, CancellationToken ct = default)
    {
        await using var conn = await controlPlane.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        Guid userId;
        string address;
        string? fullName;
        await using (var find = new NpgsqlCommand(
            "SELECT id, email, full_name FROM control.app_user WHERE lower(email) = lower(@email) AND status = 'active'", conn, tx))
        {
            find.Parameters.AddWithValue("email", email.Trim());
            await using var reader = await find.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;
            (userId, address, fullName) = (reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
        }

        await using (var retire = new NpgsqlCommand(
            "UPDATE control.password_reset SET used_at = now() WHERE user_id = @user AND used_at IS NULL", conn, tx))
        {
            retire.Parameters.AddWithValue("user", userId);
            await retire.ExecuteNonQueryAsync(ct);
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO control.password_reset (user_id, token_hash, expires_at) VALUES (@user, @hash, @expires)", conn, tx))
        {
            insert.Parameters.AddWithValue("user", userId);
            insert.Parameters.AddWithValue("hash", Hash(token));
            insert.Parameters.AddWithValue("expires", DateTime.UtcNow.Add(Lifetime));
            await insert.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return (token, address, fullName);
    }

    /// <summary>Sets the new password if the token is valid; returns the user id, or null.</summary>
    public async Task<Guid?> RedeemAsync(string token, string newPasswordHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        await using var conn = await controlPlane.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        Guid userId;
        await using (var consume = new NpgsqlCommand("""
            UPDATE control.password_reset SET used_at = now()
            WHERE token_hash = @hash AND used_at IS NULL AND expires_at > now()
            RETURNING user_id
            """, conn, tx))
        {
            consume.Parameters.AddWithValue("hash", Hash(token));
            if (await consume.ExecuteScalarAsync(ct) is not Guid id)
                return null;
            userId = id;
        }

        await using (var update = new NpgsqlCommand(
            "UPDATE control.app_user SET credential_hash = @hash, updated_at = now() WHERE id = @id AND status = 'active'", conn, tx))
        {
            update.Parameters.AddWithValue("hash", newPasswordHash);
            update.Parameters.AddWithValue("id", userId);
            if (await update.ExecuteNonQueryAsync(ct) != 1)
                return null; // disabled since the link was sent — the transaction rolls back
        }
        await tx.CommitAsync(ct);
        return userId;
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
