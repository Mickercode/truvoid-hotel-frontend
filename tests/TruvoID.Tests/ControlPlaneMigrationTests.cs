using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using TruvoID.Infrastructure.Postgres;

namespace TruvoID.Tests;

// ══════════════════════════════════════════════════════════════════════════════
// Control-plane schema against a real Postgres (requires Docker). These pin down
// the isolation guarantees the build doc relies on — they're enforced by the
// database, so they have to be tested against the database.
// ══════════════════════════════════════════════════════════════════════════════

public sealed class ControlPlaneDatabase : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17")
        .WithDatabase("truvoid")
        .Build();

    public string MigratorConnectionString { get; private set; } = string.Empty;
    public string AppConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var superuser = _container.GetConnectionString();

        await using (var conn = new NpgsqlConnection(superuser))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(ReadResource("LocalInit.01-roles.sql"), conn);
            await cmd.ExecuteNonQueryAsync();
        }

        MigratorConnectionString = WithCredentials(superuser, "truvo_migrator", "truvo_migrator_dev");
        AppConnectionString = WithCredentials(superuser, "truvo_app", "truvo_app_dev");

        await PostgresMigrator.MigrateAsync(
            MigratorConnectionString, "truvo_app",
            PostgresMigrator.LoadEmbeddedControlPlaneScripts(), NullLogger.Instance);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private static string WithCredentials(string connectionString, string user, string password) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Username = user, Password = password }.ConnectionString;

    private static string ReadResource(string name)
    {
        using var stream = typeof(ControlPlaneDatabase).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public class ControlPlaneMigrationTests : IClassFixture<ControlPlaneDatabase>
{
    private const string InsufficientPrivilege = "42501";
    private const string CheckViolation = "23514";

    private readonly ControlPlaneDatabase _db;

    public ControlPlaneMigrationTests(ControlPlaneDatabase db) => _db = db;

    // ── Migrator ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Migrate_SecondRun_AppliesNothing()
    {
        var applied = await PostgresMigrator.MigrateAsync(
            _db.MigratorConnectionString, "truvo_app",
            PostgresMigrator.LoadEmbeddedControlPlaneScripts(), NullLogger.Instance);

        Assert.Equal(0, applied);
    }

    [Fact]
    public async Task Migrate_EditedAppliedMigration_Throws()
    {
        var edited = PostgresMigrator.LoadEmbeddedControlPlaneScripts()
            .Select(s => s with { Sql = s.Sql + "\n-- edited" })
            .ToList();

        await Assert.ThrowsAsync<InvalidOperationException>(() => PostgresMigrator.MigrateAsync(
            _db.MigratorConnectionString, "truvo_app", edited, NullLogger.Instance));
    }

    [Fact]
    public async Task Migrate_InvalidAppRoleName_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => PostgresMigrator.MigrateAsync(
            _db.MigratorConnectionString, "truvo_app; DROP SCHEMA control", [], NullLogger.Instance));
    }

    // ── Runtime role is DML-only ─────────────────────────────────────────────

    [Fact]
    public async Task AppRole_CannotRunDdlInControlSchema()
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsApp("CREATE TABLE control.sneaky (id int)"));

        Assert.Equal(InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task AppRole_CannotRepointOrganizationToAnotherSchema()
    {
        var orgId = await CreateOrganizationAsync("institution");

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsApp($"UPDATE control.organization SET schema_name = 'org_other' WHERE id = '{orgId}'"));

        Assert.Equal(InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task AppRole_CannotDeleteOrganizations()
    {
        var orgId = await CreateOrganizationAsync("agency");

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsApp($"DELETE FROM control.organization WHERE id = '{orgId}'"));

        Assert.Equal(InsufficientPrivilege, ex.SqlState);
    }

    // ── Append-only ledgers ──────────────────────────────────────────────────

    [Fact]
    public async Task AuditLog_AppRoleCanInsertButNotRewrite()
    {
        await ExecuteAsApp("""
            INSERT INTO control.audit_log (occurred_at, actor_type, action, entity)
            VALUES (now(), 'system', 'test.inserted', 'test')
            """);

        var update = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsApp("UPDATE control.audit_log SET action = 'tampered'"));
        var delete = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsApp("DELETE FROM control.audit_log"));

        Assert.Equal(InsufficientPrivilege, update.SqlState);
        Assert.Equal(InsufficientPrivilege, delete.SqlState);
    }

    [Theory]
    [InlineData("control.audit_log", "UPDATE control.audit_log SET action = 'tampered'")]
    [InlineData("control.audit_log", "DELETE FROM control.audit_log")]
    [InlineData("control.audit_log", "TRUNCATE control.audit_log")]
    [InlineData("control.slogani_revenue_ledger", "UPDATE control.slogani_revenue_ledger SET amount_kobo = 1")]
    [InlineData("control.slogani_revenue_ledger", "DELETE FROM control.slogani_revenue_ledger")]
    [InlineData("control.slogani_revenue_ledger", "TRUNCATE control.slogani_revenue_ledger")]
    public async Task Ledgers_AreAppendOnlyEvenForTheOwningRole(string table, string sql)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(_db.MigratorConnectionString, sql));

        Assert.Equal(InsufficientPrivilege, ex.SqlState);
        Assert.Contains($"{table} is append-only", ex.MessageText);
    }

    // ── Identity rules ───────────────────────────────────────────────────────

    [Fact]
    public async Task User_OutletRoleWithoutOutlet_IsRejected()
    {
        var orgId = await CreateOrganizationAsync("agency");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertUserAsync(orgId, "outlet_staff", outletId: null));

        Assert.Equal(CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task User_PlatformAdminWithOrganization_IsRejected()
    {
        var orgId = await CreateOrganizationAsync("institution");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertUserAsync(orgId, "platform_admin", outletId: null));

        Assert.Equal(CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task User_AgencyRoleOnInstitution_IsRejected()
    {
        var orgId = await CreateOrganizationAsync("institution");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertUserAsync(orgId, "agency_admin", outletId: null));

        Assert.Equal(CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task User_ValidRolesForEachOrganizationType_AreAccepted()
    {
        var institutionId = await CreateOrganizationAsync("institution");
        var agencyId = await CreateOrganizationAsync("agency");

        await InsertUserAsync(institutionId, "institution_admin", outletId: null);
        await InsertUserAsync(agencyId, "agency_user", outletId: null);
        await InsertUserAsync(institutionId, "outlet_owner", outletId: Guid.NewGuid());
        await InsertUserAsync(agencyId, "outlet_staff", outletId: Guid.NewGuid());
    }

    [Fact]
    public async Task User_EmailIsUniqueIgnoringCase()
    {
        var orgId = await CreateOrganizationAsync("institution");
        var email = $"{Guid.NewGuid():N}@example.com";

        await InsertUserAsync(orgId, "institution_staff", outletId: null, email);
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertUserAsync(orgId, "institution_staff", outletId: null, email.ToUpperInvariant()));

        Assert.Equal("23505", ex.SqlState);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<Guid> CreateOrganizationAsync(string type)
    {
        var id = Guid.NewGuid();
        var name = $"org_{id:N}";
        await using var conn = new NpgsqlConnection(_db.AppConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO control.organization (id, name, type, schema_name, db_role_name)
            VALUES (@id, @name, @type, @name, @name)
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("type", type);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private async Task InsertUserAsync(Guid? organizationId, string role, Guid? outletId, string? email = null)
    {
        await using var conn = new NpgsqlConnection(_db.AppConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO control.app_user (email, credential_hash, organization_id, outlet_id, role)
            VALUES (@email, 'hash', @org, @outlet, @role)
            """, conn);
        cmd.Parameters.AddWithValue("email", email ?? $"{Guid.NewGuid():N}@example.com");
        cmd.Parameters.AddWithValue("org", (object?)organizationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("outlet", (object?)outletId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("role", role);
        await cmd.ExecuteNonQueryAsync();
    }

    private Task ExecuteAsApp(string sql) => ExecuteAsync(_db.AppConnectionString, sql);

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
