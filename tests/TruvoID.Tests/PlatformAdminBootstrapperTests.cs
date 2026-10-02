using Npgsql;
using TruvoID.Infrastructure.Postgres;

namespace TruvoID.Tests;

public class PlatformAdminBootstrapperTests(ControlPlaneDatabase db) : IClassFixture<ControlPlaneDatabase>
{
    [Fact]
    public async Task Create_InsertsActivePlatformAdminWithWorkingPassword()
    {
        var bootstrapper = new PlatformAdminBootstrapper(db.MigratorConnectionString);

        var result = await bootstrapper.CreateAsync("  Founder@GetTruvoID.com ", "Ada Founder");

        Assert.True(result.Created);
        Assert.Equal("founder@gettruvoid.com", result.Email);
        var (role, status, orgId, hash, fullName) = await ReadUserAsync(result.UserId);
        Assert.Equal("platform_admin", role);
        Assert.Equal("active", status);
        Assert.Null(orgId);
        Assert.Equal("Ada Founder", fullName);
        Assert.True(PasswordHasher.Verify(result.Password, hash));
        Assert.Equal(1, await CountAuditAsync("platform_admin.created", result.UserId));
    }

    [Fact]
    public async Task Create_RejectsDuplicateEmail()
    {
        var bootstrapper = new PlatformAdminBootstrapper(db.MigratorConnectionString);
        await bootstrapper.CreateAsync("dup-admin@gettruvoid.com", null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => bootstrapper.CreateAsync("DUP-ADMIN@gettruvoid.com", null));
        Assert.Contains("--reset-password", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("a@b@c.com")]
    [InlineData("@gettruvoid.com")]
    public async Task Create_RejectsInvalidEmail(string email)
    {
        var bootstrapper = new PlatformAdminBootstrapper(db.MigratorConnectionString);
        await Assert.ThrowsAsync<ArgumentException>(() => bootstrapper.CreateAsync(email, null));
    }

    [Fact]
    public async Task ResetPassword_ReplacesHashAndReactivates()
    {
        var bootstrapper = new PlatformAdminBootstrapper(db.MigratorConnectionString);
        var created = await bootstrapper.CreateAsync("reset-admin@gettruvoid.com", null);
        await ExecuteAsync("UPDATE control.app_user SET status = 'disabled' WHERE id = @id", created.UserId);

        var reset = await bootstrapper.ResetPasswordAsync("Reset-Admin@gettruvoid.com");

        Assert.False(reset.Created);
        Assert.Equal(created.UserId, reset.UserId);
        var (_, status, _, hash, _) = await ReadUserAsync(created.UserId);
        Assert.Equal("active", status);
        Assert.True(PasswordHasher.Verify(reset.Password, hash));
        Assert.False(PasswordHasher.Verify(created.Password, hash));
    }

    [Fact]
    public async Task ResetPassword_FailsForUnknownEmail()
    {
        var bootstrapper = new PlatformAdminBootstrapper(db.MigratorConnectionString);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => bootstrapper.ResetPasswordAsync("nobody@gettruvoid.com"));
    }

    private async Task<(string Role, string Status, Guid? OrgId, string Hash, string? FullName)> ReadUserAsync(Guid id)
    {
        await using var conn = new NpgsqlConnection(db.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT role, status, organization_id, credential_hash, full_name FROM control.app_user WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private async Task<long> CountAuditAsync(string action, Guid userId)
    {
        await using var conn = new NpgsqlConnection(db.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM control.audit_log WHERE action = @action AND entity_id = @id", conn);
        cmd.Parameters.AddWithValue("action", action);
        cmd.Parameters.AddWithValue("id", userId.ToString());
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql, Guid id)
    {
        await using var conn = new NpgsqlConnection(db.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync();
    }
}
