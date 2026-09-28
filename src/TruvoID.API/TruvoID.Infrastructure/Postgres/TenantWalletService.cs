using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace TruvoID.Infrastructure.Postgres;

public sealed record WalletSnapshot(Guid WalletId, long BalanceKobo);
public sealed record LedgerMutation(Guid LedgerEntryId, Guid WalletId, long BalanceAfterKobo);

/// <summary>
/// Financial mutations against one tenant schema. Callers own the surrounding
/// TenantSession transaction and must commit only after all related rows succeed.
/// </summary>
public sealed class TenantWalletService
{
    public async Task<WalletSnapshot> GetBalanceAsync(TenantSession session, CancellationToken ct = default)
    {
        var wallet = await FindWalletAsync(session, lockForUpdate: false, ct);
        return wallet;
    }

    public async Task<IReadOnlyList<WalletLedgerRow>> GetLedgerAsync(
        TenantSession session,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 200);
        await using var command = session.CreateCommand("""
            SELECT id, wallet_id, outlet_id, entry_type, amount_kobo,
                   balance_after_kobo, unit_price_kobo, reference_id, created_at
            FROM wallet_ledger_entry
            ORDER BY created_at DESC
            OFFSET @offset LIMIT @limit
            """);
        command.Parameters.AddWithValue("offset", (page - 1) * pageSize);
        command.Parameters.AddWithValue("limit", pageSize);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<WalletLedgerRow>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new WalletLedgerRow(
                reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetFieldValue<DateTime>(8)));
        }
        return rows;
    }

    public async Task<LedgerMutation> CreditAsync(
        TenantSession session,
        long amountKobo,
        Guid? outletId,
        string referenceId,
        long? unitPriceKobo = null,
        CancellationToken ct = default)
    {
        if (amountKobo <= 0)
            throw new ArgumentOutOfRangeException(nameof(amountKobo));

        return await CreditLikeAsync(session, amountKobo, outletId, referenceId, unitPriceKobo, "credit", ct);
    }

    public Task<LedgerMutation> RefundAsync(
        TenantSession session,
        long amountKobo,
        Guid? outletId,
        string referenceId,
        CancellationToken ct = default) =>
        CreditLikeAsync(session, amountKobo, outletId, referenceId, null, "refund", ct);

    public async Task<LedgerMutation> DebitAsync(
        TenantSession session,
        long amountKobo,
        Guid? outletId,
        string referenceId,
        CancellationToken ct = default)
    {
        if (amountKobo <= 0)
            throw new ArgumentOutOfRangeException(nameof(amountKobo));

        var wallet = await FindWalletAsync(session, lockForUpdate: true, ct);
        if (wallet.BalanceKobo < amountKobo)
            throw new InsufficientWalletBalanceException(wallet.BalanceKobo, amountKobo);

        var balance = wallet.BalanceKobo - amountKobo;
        await UpdateBalanceAsync(session, wallet.WalletId, balance, ct);
        var ledgerId = await InsertLedgerAsync(session, wallet.WalletId, outletId, "debit", amountKobo,
            balance, null, referenceId, ct);
        return new LedgerMutation(ledgerId, wallet.WalletId, balance);
    }

    /// <summary>Transfers prepaid credit from an Agency wallet to one Outlet wallet.</summary>
    public async Task<(LedgerMutation SellerDebit, LedgerMutation OutletCredit)> TransferToOutletAsync(
        TenantSession session,
        Guid outletId,
        long amountKobo,
        string referenceId,
        CancellationToken ct = default)
    {
        if (session.Scope.OutletId is not null)
            throw new InvalidOperationException("Only an Agency organization scope can fund an Outlet.");
        if (amountKobo <= 0)
            throw new ArgumentOutOfRangeException(nameof(amountKobo));

        await using var command = session.CreateCommand("""
            SELECT seller.id, seller.balance_kobo, buyer.id, buyer.balance_kobo
            FROM tenant t
            JOIN wallet seller ON seller.kind = 'organization'
            JOIN outlet o ON o.id = @outletId
            JOIN wallet buyer ON buyer.id = o.wallet_id AND buyer.kind = 'outlet'
            WHERE t.org_type = 'agency'
            FOR UPDATE
            """);
        command.Parameters.AddWithValue("outletId", outletId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("The Outlet does not belong to an Agency or has no Outlet wallet.");
        var sellerId = reader.GetGuid(0);
        var sellerBalance = reader.GetInt64(1);
        var buyerId = reader.GetGuid(2);
        var buyerBalance = reader.GetInt64(3);
        await reader.CloseAsync();

        if (sellerBalance < amountKobo)
            throw new InsufficientWalletBalanceException(sellerBalance, amountKobo);

        var sellerAfter = sellerBalance - amountKobo;
        var buyerAfter = checked(buyerBalance + amountKobo);
        await UpdateBalanceAsync(session, sellerId, sellerAfter, ct);
        await UpdateBalanceAsync(session, buyerId, buyerAfter, ct);
        var debitId = await InsertLedgerAsync(session, sellerId, outletId, "debit", amountKobo, sellerAfter, null, referenceId, ct);
        var creditId = await InsertLedgerAsync(session, buyerId, outletId, "credit", amountKobo, buyerAfter, null, referenceId, ct);
        return (new LedgerMutation(debitId, sellerId, sellerAfter), new LedgerMutation(creditId, buyerId, buyerAfter));
    }

    public async Task AddRevenueOutboxEventAsync(
        TenantSession session,
        object payload,
        CancellationToken ct = default)
    {
        await using var command = session.CreateCommand("""
            INSERT INTO outbox (event_type, payload) VALUES ('revenue', @payload)
            """);
        command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(payload);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<LedgerMutation> CreditLikeAsync(
        TenantSession session,
        long amountKobo,
        Guid? outletId,
        string referenceId,
        long? unitPriceKobo,
        string entryType,
        CancellationToken ct)
    {
        if (amountKobo <= 0)
            throw new ArgumentOutOfRangeException(nameof(amountKobo));
        var wallet = await FindWalletAsync(session, lockForUpdate: true, ct);
        var balance = checked(wallet.BalanceKobo + amountKobo);
        await UpdateBalanceAsync(session, wallet.WalletId, balance, ct);
        var ledgerId = await InsertLedgerAsync(session, wallet.WalletId, outletId, entryType, amountKobo,
            balance, unitPriceKobo, referenceId, ct);
        return new LedgerMutation(ledgerId, wallet.WalletId, balance);
    }

    private async Task<WalletSnapshot> FindWalletAsync(TenantSession session, bool lockForUpdate, CancellationToken ct)
    {
        var sql = session.Scope.OutletId is null
            ? "SELECT id, balance_kobo FROM wallet WHERE kind = 'organization'"
            : """
              SELECT w.id, w.balance_kobo
              FROM wallet w JOIN outlet o ON o.wallet_id = w.id
              WHERE o.id = @outletId
              """;
        if (lockForUpdate)
            sql += " FOR UPDATE";

        await using var command = session.CreateCommand(sql);
        if (session.Scope.OutletId is { } outletId)
            command.Parameters.AddWithValue("outletId", outletId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("The scoped wallet does not exist.");
        return new WalletSnapshot(reader.GetGuid(0), reader.GetInt64(1));
    }

    private static async Task UpdateBalanceAsync(TenantSession session, Guid walletId, long balance, CancellationToken ct)
    {
        await using var command = session.CreateCommand("""
            UPDATE wallet SET balance_kobo = @balance, version = version + 1, updated_at = now()
            WHERE id = @id
            """);
        command.Parameters.AddWithValue("id", walletId);
        command.Parameters.AddWithValue("balance", balance);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<Guid> InsertLedgerAsync(
        TenantSession session,
        Guid walletId,
        Guid? outletId,
        string type,
        long amount,
        long balance,
        long? unitPrice,
        string referenceId,
        CancellationToken ct)
    {
        await using var command = session.CreateCommand("""
            INSERT INTO wallet_ledger_entry
                (wallet_id, outlet_id, entry_type, amount_kobo, balance_after_kobo, unit_price_kobo, reference_id)
            VALUES (@walletId, @outletId, @type, @amount, @balance, @unitPrice, @referenceId)
            RETURNING id
            """);
        command.Parameters.AddWithValue("walletId", walletId);
        command.Parameters.AddWithValue("outletId", (object?)outletId ?? DBNull.Value);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("amount", amount);
        command.Parameters.AddWithValue("balance", balance);
        command.Parameters.AddWithValue("unitPrice", (object?)unitPrice ?? DBNull.Value);
        command.Parameters.AddWithValue("referenceId", referenceId);
        return (Guid)(await command.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("Ledger insert failed."));
    }
}

public sealed record WalletLedgerRow(
    Guid Id,
    Guid WalletId,
    Guid? OutletId,
    string EntryType,
    long AmountKobo,
    long BalanceAfterKobo,
    long? UnitPriceKobo,
    string? ReferenceId,
    DateTime CreatedAt);

public sealed class InsufficientWalletBalanceException(long balanceKobo, long requiredKobo)
    : InvalidOperationException($"Insufficient wallet balance. Available: {balanceKobo} kobo; required: {requiredKobo} kobo.")
{
    public long BalanceKobo { get; } = balanceKobo;
    public long RequiredKobo { get; } = requiredKobo;
}
