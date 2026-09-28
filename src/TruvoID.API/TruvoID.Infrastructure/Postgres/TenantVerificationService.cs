using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace TruvoID.Infrastructure.Postgres;

public sealed record VerificationReservation(
    Guid CallId,
    Guid LedgerEntryId,
    long PriceKobo,
    long BalanceAfterKobo,
    string VerificationType);

/// <summary>
/// Reserves a verification in one tenant transaction: wallet debit, ledger entry,
/// and pending call record either commit together or all roll back.
/// </summary>
public sealed class TenantVerificationService(
    NpgsqlDataSource controlPlane,
    TenantWalletService wallets)
{
    public async Task<VerificationReservation> ReserveAsync(
        TenantSession session,
        string verificationType,
        string subjectRef,
        Guid? userId,
        Guid? apiKeyId,
        string? idempotencyKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(verificationType) || string.IsNullOrWhiteSpace(subjectRef))
            throw new ArgumentException("Verification type and subject are required.");
        if ((userId is null) == (apiKeyId is null))
            throw new ArgumentException("Exactly one verification caller is required.");

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var existing = await FindExistingAsync(session, idempotencyKey, userId, apiKeyId, ct);
            if (existing is not null)
                return existing;
        }

        var priceKobo = await ResolvePriceAsync(session.Scope.OrganizationId, verificationType, ct);
        var callId = Guid.NewGuid();
        var debit = await wallets.DebitAsync(session, priceKobo, session.Scope.OutletId, callId.ToString(), ct: ct);

        await using var command = session.CreateCommand("""
            INSERT INTO verification_call
                (id, outlet_id, user_id, api_key_id, verification_type, subject_ref,
                 status, ledger_entry_id, idempotency_key)
            VALUES (@id, @outletId, @userId, @apiKeyId, @type, @subjectRef,
                    'pending', @ledgerId, @idempotencyKey)
            """);
        command.Parameters.AddWithValue("id", callId);
        command.Parameters.AddWithValue("outletId", (object?)session.Scope.OutletId ?? DBNull.Value);
        command.Parameters.AddWithValue("userId", (object?)userId ?? DBNull.Value);
        command.Parameters.AddWithValue("apiKeyId", (object?)apiKeyId ?? DBNull.Value);
        command.Parameters.AddWithValue("type", verificationType.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("subjectRef", HashSubject(subjectRef));
        command.Parameters.AddWithValue("ledgerId", debit.LedgerEntryId);
        command.Parameters.AddWithValue("idempotencyKey", (object?)idempotencyKey ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);

        return new VerificationReservation(callId, debit.LedgerEntryId, priceKobo,
            debit.BalanceAfterKobo, verificationType.Trim().ToLowerInvariant());
    }

    public async Task CompleteAsync(
        TenantSession session,
        Guid callId,
        bool succeeded,
        string? resultJson,
        string? errorMessage,
        CancellationToken ct = default)
    {
        await using var command = session.CreateCommand("""
            SELECT ledger_entry_id, status
            FROM verification_call
            WHERE id = @id
            FOR UPDATE
            """);
        command.Parameters.AddWithValue("id", callId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("Verification call not found in this tenant scope.");
        var ledgerId = reader.GetGuid(0);
        var status = reader.GetString(1);
        await reader.CloseAsync();
        if (status != "pending")
            throw new InvalidOperationException("Verification call has already been completed.");

        if (!succeeded)
        {
            await using var amountCommand = session.CreateCommand(
                "SELECT amount_kobo FROM wallet_ledger_entry WHERE id = @id");
            amountCommand.Parameters.AddWithValue("id", ledgerId);
            var amount = Convert.ToInt64(await amountCommand.ExecuteScalarAsync(ct));
            await wallets.RefundAsync(session, amount, session.Scope.OutletId, callId.ToString(), ct);
        }

        await using var update = session.CreateCommand("""
            UPDATE verification_call
            SET status = @status, result = @result, completed_at = now()
            WHERE id = @id
            """);
        update.Parameters.AddWithValue("id", callId);
        update.Parameters.AddWithValue("status", succeeded ? "succeeded" : "failed");
        update.Parameters.Add("result", NpgsqlDbType.Jsonb).Value = (object?)resultJson ?? DBNull.Value;
        await update.ExecuteNonQueryAsync(ct);
    }

    private async Task<long> ResolvePriceAsync(Guid organizationId, string type, CancellationToken ct)
    {
        await using var command = controlPlane.CreateCommand("""
            SELECT coalesce(
                (SELECT price_kobo FROM control.organization_rate
                 WHERE organization_id = @organizationId AND verification_type = @type
                   AND effective_from <= now() ORDER BY effective_from DESC LIMIT 1),
                (SELECT price_kobo FROM control.platform_rate
                 WHERE verification_type = @type AND effective_from <= now()
                 ORDER BY effective_from DESC LIMIT 1))
            """);
        command.Parameters.AddWithValue("organizationId", organizationId);
        command.Parameters.AddWithValue("type", type.Trim().ToLowerInvariant());
        var result = await command.ExecuteScalarAsync(ct);
        if (result is null or DBNull)
            throw new InvalidOperationException($"No price is configured for verification type '{type}'.");
        return Convert.ToInt64(result);
    }

    private static async Task<VerificationReservation?> FindExistingAsync(
        TenantSession session,
        string idempotencyKey,
        Guid? userId,
        Guid? apiKeyId,
        CancellationToken ct)
    {
        await using var command = session.CreateCommand("""
            SELECT c.id, c.ledger_entry_id, l.amount_kobo, l.balance_after_kobo, c.verification_type
            FROM verification_call c
            JOIN wallet_ledger_entry l ON l.id = c.ledger_entry_id
            WHERE c.idempotency_key = @key
              AND ((@userId IS NOT NULL AND c.user_id = @userId)
                OR (@apiKeyId IS NOT NULL AND c.api_key_id = @apiKeyId))
            LIMIT 1
            """);
        command.Parameters.AddWithValue("key", idempotencyKey);
        command.Parameters.AddWithValue("userId", (object?)userId ?? DBNull.Value);
        command.Parameters.AddWithValue("apiKeyId", (object?)apiKeyId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new VerificationReservation(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4))
            : null;
    }

    private static string HashSubject(string subject) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subject.Trim()))).ToLowerInvariant();
}
