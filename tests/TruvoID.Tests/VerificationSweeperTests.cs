using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TruvoID.Infrastructure.Identity;
using TruvoID.Infrastructure.Postgres;

namespace TruvoID.Tests;

public class VerificationSweeperTests(TenantDatabase t) : IClassFixture<TenantDatabase>, IAsyncLifetime
{
    private const long PriceKobo = 10_000;
    private TenantScope Scope => TenantScope.Organization(t.InstitutionId);
    private TenantVerificationService Verifications => new(t.ControlPlane, new TenantWalletService());
    private VerificationSweeper Sweeper => new(t.ControlPlane, t.Factory, Verifications, NullLogger.Instance);

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(t.Database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var rate = new NpgsqlCommand("""
            INSERT INTO control.platform_rate (verification_type, price_kobo, cost_kobo, effective_from)
            SELECT code, @price, 0, now() - interval '1 minute' FROM control.verification_type
            WHERE NOT EXISTS (SELECT 1 FROM control.platform_rate)
            """, conn);
        rate.Parameters.AddWithValue("price", PriceKobo);
        await rate.ExecuteNonQueryAsync();

        await using var session = await t.Factory.BeginAsync(Scope);
        await new TenantWalletService().CreditAsync(session, PriceKobo * 10, null, $"fund-{Guid.NewGuid():N}");
        await session.CommitAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>A call that was charged but never completed — what a crash mid-request leaves behind.</summary>
    private async Task<Guid> ReservePendingAsync()
    {
        await using var session = await t.Factory.BeginAsync(Scope);
        var reservation = await Verifications.ReserveAsync(session, "nin", "00000000001", Guid.NewGuid(), null, null);
        await session.CommitAsync();
        return reservation.CallId;
    }

    // created_at isn't writable by the tenant role, so age the row as the migrator (which RLS still binds).
    private async Task AgeAsync(Guid callId, TimeSpan by)
    {
        await using var conn = new NpgsqlConnection(t.Database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand("SELECT set_config('app.scope', 'org', true)", conn, tx))
            await scope.ExecuteNonQueryAsync();
        await using (var age = new NpgsqlCommand(
            $"UPDATE org_{t.InstitutionId:N}.verification_call SET created_at = created_at - make_interval(secs => @s) WHERE id = @id", conn, tx))
        {
            age.Parameters.AddWithValue("s", by.TotalSeconds);
            age.Parameters.AddWithValue("id", callId);
            Assert.Equal(1, await age.ExecuteNonQueryAsync());
        }
        await tx.CommitAsync();
    }

    private async Task<long> BalanceAsync()
    {
        await using var session = await t.Factory.BeginAsync(Scope);
        return (await new TenantWalletService().GetBalanceAsync(session)).BalanceKobo;
    }

    private async Task<string> StatusAsync(Guid callId)
    {
        await using var session = await t.Factory.BeginAsync(Scope);
        await using var cmd = session.CreateCommand("SELECT status FROM verification_call WHERE id = @id");
        cmd.Parameters.AddWithValue("id", callId);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task StaleCall_IsRefundedOnce_FreshCallIsLeftAlone()
    {
        var stale = await ReservePendingAsync();
        var fresh = await ReservePendingAsync();
        await AgeAsync(stale, VerificationSweeper.StaleAfter + TimeSpan.FromMinutes(1));
        var before = await BalanceAsync();

        var refunded = await Sweeper.SweepAsync();

        Assert.True(refunded >= 1);
        Assert.Equal("failed", await StatusAsync(stale));
        Assert.Equal("pending", await StatusAsync(fresh));      // may still be in flight
        Assert.Equal(before + PriceKobo, await BalanceAsync());

        await Sweeper.SweepAsync();                              // second pass: nothing new to refund
        Assert.Equal(before + PriceKobo, await BalanceAsync());
    }
}
