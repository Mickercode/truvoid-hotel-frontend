using Npgsql;
using TruvoID.Infrastructure.Postgres;

namespace TruvoID.Tests;

/// <summary>Runs as the DML-only truvo_app role, so it also proves the migration's grants suffice.</summary>
public class RefreshTokenStoreTests(ControlPlaneDatabase db) : IClassFixture<ControlPlaneDatabase>
{
    private RefreshTokenStore Store(TimeSpan? lifetime = null) => new(NpgsqlDataSource.Create(db.AppConnectionString), lifetime);

    private async Task<Guid> NewUserAsync()
    {
        var admin = await new PlatformAdminBootstrapper(db.MigratorConnectionString)
            .CreateAsync($"rt-{Guid.NewGuid():N}@gettruvoid.com", null);
        return admin.UserId;
    }

    // Simulates time passing beyond the reuse grace window.
    private async Task AgeRevocationAsync(Guid userId)
    {
        await using var conn = new NpgsqlConnection(db.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE control.refresh_token SET revoked_at = now() - interval '5 minutes' WHERE user_id = @u AND revoked_at IS NOT NULL", conn);
        cmd.Parameters.AddWithValue("u", userId);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Rotate_ReturnsNewTokenAndRetiresOld()
    {
        var store = Store();
        var user = await NewUserAsync();
        var first = await store.IssueAsync(user);

        var rotated = await store.RotateAsync(first);

        Assert.Equal(RefreshOutcome.Rotated, rotated.Outcome);
        Assert.Equal(user, rotated.UserId);
        Assert.NotEqual(first, rotated.NewToken);
        Assert.Equal(RefreshOutcome.Rotated, (await store.RotateAsync(rotated.NewToken!)).Outcome);
    }

    [Fact]
    public async Task ConcurrentReuse_WithinGrace_IsHarmless()
    {
        var store = Store();
        var user = await NewUserAsync();
        var first = await store.IssueAsync(user);
        var second = (await store.RotateAsync(first)).NewToken!;

        // A second tab presents the token that was rotated a moment ago.
        Assert.Equal(RefreshOutcome.Superseded, (await store.RotateAsync(first)).Outcome);
        // The legitimate successor still works: nothing was revoked.
        Assert.Equal(RefreshOutcome.Rotated, (await store.RotateAsync(second)).Outcome);
    }

    [Fact]
    public async Task Reuse_AfterGrace_RevokesTheWholeFamily()
    {
        var store = Store();
        var user = await NewUserAsync();
        var stolen = await store.IssueAsync(user);
        var current = (await store.RotateAsync(stolen)).NewToken!;
        await AgeRevocationAsync(user);

        Assert.Equal(RefreshOutcome.Reused, (await store.RotateAsync(stolen)).Outcome);
        Assert.NotEqual(RefreshOutcome.Rotated, (await store.RotateAsync(current)).Outcome);
    }

    [Fact]
    public async Task Reuse_DoesNotAffectOtherSignIns()
    {
        var store = Store();
        var user = await NewUserAsync();
        var phone = await store.IssueAsync(user);
        var laptop = await store.IssueAsync(user);
        await store.RotateAsync(phone);
        await AgeRevocationAsync(user);

        await store.RotateAsync(phone); // reuse → phone's family revoked

        Assert.Equal(RefreshOutcome.Rotated, (await store.RotateAsync(laptop)).Outcome);
    }

    [Fact]
    public async Task ExpiredToken_IsRejected()
    {
        var store = Store(TimeSpan.FromMilliseconds(-1));
        var token = await store.IssueAsync(await NewUserAsync());
        Assert.Equal(RefreshOutcome.Expired, (await store.RotateAsync(token)).Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-real-token")]
    public async Task UnknownToken_IsRejected(string token) =>
        Assert.Equal(RefreshOutcome.Unknown, (await Store().RotateAsync(token)).Outcome);

    [Fact]
    public async Task Logout_RevokesOnlyThatSession()
    {
        var store = Store();
        var user = await NewUserAsync();
        var a = await store.IssueAsync(user);
        var b = await store.IssueAsync(user);

        await store.RevokeFamilyOfAsync(a);

        Assert.NotEqual(RefreshOutcome.Rotated, (await store.RotateAsync(a)).Outcome);
        Assert.Equal(RefreshOutcome.Rotated, (await store.RotateAsync(b)).Outcome);
    }

    [Fact]
    public async Task RevokeAllForUser_EndsEverySession()
    {
        var store = Store();
        var user = await NewUserAsync();
        var a = await store.IssueAsync(user);
        var b = await store.IssueAsync(user);

        await store.RevokeAllForUserAsync(user);

        Assert.NotEqual(RefreshOutcome.Rotated, (await store.RotateAsync(a)).Outcome);
        Assert.NotEqual(RefreshOutcome.Rotated, (await store.RotateAsync(b)).Outcome);
    }
}
