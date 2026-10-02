using Npgsql;
using TruvoID.Infrastructure.Postgres;

namespace TruvoID.Tests;

/// <summary>Runs as the DML-only truvo_app role, so it also proves the migration's grants suffice.</summary>
public class PasswordResetStoreTests(ControlPlaneDatabase db) : IClassFixture<ControlPlaneDatabase>
{
    private readonly PasswordResetStore _store = new(NpgsqlDataSource.Create(db.AppConnectionString));

    private async Task<(Guid Id, string Email)> NewUserAsync()
    {
        var email = $"reset-{Guid.NewGuid():N}@gettruvoid.com";
        var admin = await new PlatformAdminBootstrapper(db.MigratorConnectionString).CreateAsync(email, "Reset Tester");
        return (admin.UserId, email);
    }

    private async Task<string> CredentialHashAsync(Guid userId)
    {
        await using var conn = new NpgsqlConnection(db.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT credential_hash FROM control.app_user WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", userId);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task ExecAsync(string sql, Guid userId)
    {
        await using var conn = new NpgsqlConnection(db.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", userId);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Redeem_SetsNewPassword_AndTokenIsSingleUse()
    {
        var (id, email) = await NewUserAsync();
        var issued = await _store.IssueAsync(email.ToUpperInvariant());

        Assert.Equal(email, issued!.Value.Email);
        Assert.Equal(id, await _store.RedeemAsync(issued.Value.Token, PasswordHasher.Hash("NewPassw0rd")));
        Assert.True(PasswordHasher.Verify("NewPassw0rd", await CredentialHashAsync(id)));
        Assert.Null(await _store.RedeemAsync(issued.Value.Token, PasswordHasher.Hash("Another1pass")));
    }

    [Fact]
    public async Task NewerLink_InvalidatesOlderOne()
    {
        var (_, email) = await NewUserAsync();
        var first = await _store.IssueAsync(email);
        var second = await _store.IssueAsync(email);

        Assert.Null(await _store.RedeemAsync(first!.Value.Token, PasswordHasher.Hash("NewPassw0rd")));
        Assert.NotNull(await _store.RedeemAsync(second!.Value.Token, PasswordHasher.Hash("NewPassw0rd")));
    }

    [Fact]
    public async Task ExpiredLink_IsRejected()
    {
        var (id, email) = await NewUserAsync();
        var issued = await _store.IssueAsync(email);
        await ExecAsync("UPDATE control.password_reset SET expires_at = now() - interval '1 minute' WHERE user_id = @id", id);

        Assert.Null(await _store.RedeemAsync(issued!.Value.Token, PasswordHasher.Hash("NewPassw0rd")));
    }

    [Fact]
    public async Task UnknownOrDisabledAccounts_GetNoLink()
    {
        Assert.Null(await _store.IssueAsync("nobody-here@gettruvoid.com"));

        var (id, email) = await NewUserAsync();
        await ExecAsync("UPDATE control.app_user SET status = 'disabled' WHERE id = @id", id);
        Assert.Null(await _store.IssueAsync(email));
    }

    [Fact]
    public async Task UserDisabledAfterLinkWasSent_CannotRedeem()
    {
        var (id, email) = await NewUserAsync();
        var original = await CredentialHashAsync(id);
        var issued = await _store.IssueAsync(email);
        await ExecAsync("UPDATE control.app_user SET status = 'disabled' WHERE id = @id", id);

        Assert.Null(await _store.RedeemAsync(issued!.Value.Token, PasswordHasher.Hash("NewPassw0rd")));
        Assert.Equal(original, await CredentialHashAsync(id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("made-up-token")]
    public async Task BogusToken_IsRejected(string token) =>
        Assert.Null(await _store.RedeemAsync(token, PasswordHasher.Hash("NewPassw0rd")));
}
