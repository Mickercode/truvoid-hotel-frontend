using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TruvoID.Infrastructure.Identity;
using TruvoID.Infrastructure.Postgres;

namespace TruvoID.Tests;

/// <summary>
/// The money path against a real Postgres: charge, provider answer, refund on provider
/// error, idempotent replay. Uses the sandbox provider so outcomes are deterministic.
/// </summary>
public class VerificationRunnerTests(TenantDatabase t) : IClassFixture<TenantDatabase>, IAsyncLifetime
{
    private const long PriceKobo = 10_000; // ₦100
    private readonly Guid _user = Guid.NewGuid();
    private TenantScope Scope => TenantScope.Organization(t.InstitutionId);

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(t.Database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var rate = new NpgsqlCommand("""
            INSERT INTO control.platform_rate (verification_type, price_kobo, cost_kobo, effective_from)
            SELECT code, @price, 5000, now() - interval '1 minute' FROM control.verification_type
            WHERE NOT EXISTS (SELECT 1 FROM control.platform_rate)
            """, conn);
        rate.Parameters.AddWithValue("price", PriceKobo);
        await rate.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private VerificationRunner Runner(IIdentityProvider? provider = null) => new(
        t.Factory,
        new TenantVerificationService(t.ControlPlane, new TenantWalletService()),
        provider ?? new SandboxIdentityProvider(TimeSpan.Zero),
        NullLogger<VerificationRunner>.Instance);

    private async Task<long> BalanceAsync()
    {
        await using var session = await t.Factory.BeginAsync(Scope);
        return (await new TenantWalletService().GetBalanceAsync(session)).BalanceKobo;
    }

    private async Task FundAsync(long kobo)
    {
        await using var session = await t.Factory.BeginAsync(Scope);
        await new TenantWalletService().CreditAsync(session, kobo, null, $"test-{Guid.NewGuid():N}");
        await session.CommitAsync();
    }

    private async Task<(string Status, string? Result)> CallAsync(Guid id)
    {
        await using var session = await t.Factory.BeginAsync(Scope);
        await using var cmd = session.CreateCommand("SELECT status, result::text FROM verification_call WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    [Fact]
    public async Task Match_ChargesOnceAndStoresMinimizedResult()
    {
        await FundAsync(PriceKobo * 3);
        var before = await BalanceAsync();

        var outcome = await Runner().RunAsync(Scope, "nin", "00000000001", _user, null, null, CancellationToken.None);

        Assert.Equal("match", outcome.Status);
        Assert.Equal("sandbox", outcome.Environment);
        Assert.NotNull(outcome.Identity!.Photo);              // returned live…
        Assert.Equal(before - PriceKobo, await BalanceAsync());
        var (status, result) = await CallAsync(outcome.CallId);
        Assert.Equal("succeeded", status);
        Assert.DoesNotContain("data:image", result);           // …but never persisted
        Assert.DoesNotContain("SANDBOX CLOSE", result);        // nor the address
        Assert.Contains("ADAEZE", result);
    }

    [Fact]
    public async Task NoMatch_IsStillCharged()
    {
        await FundAsync(PriceKobo);
        var before = await BalanceAsync();
        var outcome = await Runner().RunAsync(Scope, "nin", "00000000002", _user, null, null, CancellationToken.None);
        Assert.Equal("no_match", outcome.Status);
        Assert.False(outcome.Refunded);
        Assert.Equal(before - PriceKobo, await BalanceAsync());
    }

    [Fact]
    public async Task ProviderError_IsRefundedInFull()
    {
        await FundAsync(PriceKobo);
        var before = await BalanceAsync();

        var outcome = await Runner().RunAsync(Scope, "nin", "00000000003", _user, null, null, CancellationToken.None);

        Assert.Equal("provider_error", outcome.Status);
        Assert.True(outcome.Refunded);
        Assert.Equal(before, outcome.BalanceAfterKobo);
        Assert.Equal(before, await BalanceAsync());
        Assert.Equal("failed", (await CallAsync(outcome.CallId)).Status);
    }

    [Fact]
    public async Task ProviderThatThrows_IsRefunded()
    {
        await FundAsync(PriceKobo);
        var before = await BalanceAsync();
        var outcome = await Runner(new ThrowingProvider()).RunAsync(Scope, "bvn", "22222222221", _user, null, null, CancellationToken.None);
        Assert.Equal("provider_error", outcome.Status);
        Assert.Equal(before, await BalanceAsync());
    }

    [Fact]
    public async Task SameIdempotencyKey_ReturnsOriginalAnswerWithoutChargingAgain()
    {
        await FundAsync(PriceKobo * 2);
        var key = $"idem-{Guid.NewGuid():N}";
        var first = await Runner().RunAsync(Scope, "nin", "00000000001", _user, null, key, CancellationToken.None);
        var afterFirst = await BalanceAsync();

        var second = await Runner().RunAsync(Scope, "nin", "00000000001", _user, null, key, CancellationToken.None);

        Assert.True(second.Replayed);
        Assert.Equal(first.CallId, second.CallId);
        Assert.Equal("match", second.Status);
        Assert.Equal("ADAEZE CHIOMA OKAFOR", second.Identity!.FullName);
        Assert.Equal(afterFirst, await BalanceAsync());
    }

    [Fact]
    public async Task InvalidNumber_ChargesNothing()
    {
        var before = await BalanceAsync();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Runner().RunAsync(Scope, "nin", "123", _user, null, null, CancellationToken.None));
        Assert.Equal(before, await BalanceAsync());
    }

    [Fact]
    public async Task UnconfiguredProvider_ChargesNothing()
    {
        var before = await BalanceAsync();
        await Assert.ThrowsAsync<IdentityProviderUnavailableException>(() =>
            Runner(new UnconfiguredProvider()).RunAsync(Scope, "nin", "00000000001", _user, null, null, CancellationToken.None));
        Assert.Equal(before, await BalanceAsync());
    }

    [Fact]
    public async Task InsufficientBalance_IsRejectedBeforeTheProviderIsCalled()
    {
        var spy = new ThrowingProvider();
        // Drain the wallet to below one call's price.
        var balance = await BalanceAsync();
        if (balance >= PriceKobo)
        {
            await using var session = await t.Factory.BeginAsync(Scope);
            await new TenantWalletService().DebitAsync(session, balance, null, $"drain-{Guid.NewGuid():N}");
            await session.CommitAsync();
        }

        await Assert.ThrowsAsync<InsufficientWalletBalanceException>(() =>
            Runner(spy).RunAsync(Scope, "nin", "00000000001", _user, null, null, CancellationToken.None));
        Assert.Equal(0, spy.Calls);
    }

    private sealed class ThrowingProvider : IIdentityProvider
    {
        public int Calls { get; private set; }
        public string Environment => "live";
        public bool IsConfigured => true;
        public Task<IdentityResult> VerifyAsync(string type, string subject, string idempotencyKey, CancellationToken ct)
        {
            Calls++;
            throw new HttpRequestException("boom");
        }
    }

    private sealed class UnconfiguredProvider : IIdentityProvider
    {
        public string Environment => "live";
        public bool IsConfigured => false;
        public Task<IdentityResult> VerifyAsync(string type, string subject, string idempotencyKey, CancellationToken ct) =>
            throw new InvalidOperationException("should not be called");
    }
}
