using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TruvoID.Infrastructure.Postgres;

namespace TruvoID.Tests;

// ══════════════════════════════════════════════════════════════════════════════
// Per-Organization schemas, roles and RLS against a real Postgres (requires
// Docker). One Institution and two Agencies are provisioned up front; each test
// checks that the database itself — not application code — enforces isolation.
// ══════════════════════════════════════════════════════════════════════════════

public sealed class TenantDatabase : IAsyncLifetime
{
    public ControlPlaneDatabase Database { get; } = new();
    public TenantCredentialProtector Protector { get; } = new("test", RandomNumberGenerator.GetBytes(32));
    public NpgsqlDataSource ControlPlane { get; private set; } = null!;
    public TenantConnectionFactory Factory { get; private set; } = null!;
    public OrganizationRegistry Registry { get; private set; } = null!;

    public Guid InstitutionId { get; private set; }
    public Guid AgencyId { get; private set; }
    public Guid OtherAgencyId { get; private set; }

    public async Task InitializeAsync()
    {
        await Database.InitializeAsync();
        ControlPlane = NpgsqlDataSource.Create(Database.AppConnectionString);
        Registry = new OrganizationRegistry(ControlPlane);

        InstitutionId = await Registry.RegisterAsync("Eko Hotels", OrganizationType.Institution);
        AgencyId = await Registry.RegisterAsync("Lagos Verify Agency", OrganizationType.Agency);
        OtherAgencyId = await Registry.RegisterAsync("Abuja Verify Agency", OrganizationType.Agency);

        await NewProvisioner().ProvisionPendingAsync();
        Factory = new TenantConnectionFactory(ControlPlane, Database.AppConnectionString, Protector);
    }

    public TenantProvisioner NewProvisioner(IReadOnlyList<MigrationScript>? scripts = null) =>
        new(Database.MigratorConnectionString, Protector, NullLogger.Instance, scripts);

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await ControlPlane.DisposeAsync();
        await Database.DisposeAsync();
    }
}

public class TenantIsolationTests : IClassFixture<TenantDatabase>
{
    private const string InsufficientPrivilege = "42501";

    private readonly TenantDatabase _t;

    public TenantIsolationTests(TenantDatabase tenants) => _t = tenants;

    // ── Provisioning ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Provisioning_ActivatesOrganizationsWithLeastPrivilegeRoles()
    {
        await using var conn = new NpgsqlConnection(_t.Database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT o.status, r.rolsuper, r.rolbypassrls, r.rolinherit, r.rolcreaterole,
                   (SELECT tableowner FROM pg_tables WHERE schemaname = o.schema_name AND tablename = 'wallet')
            FROM control.organization o
            JOIN pg_roles r ON r.rolname = o.db_role_name
            WHERE o.id = ANY(@ids)
            """, conn);
        cmd.Parameters.AddWithValue("ids", new[] { _t.InstitutionId, _t.AgencyId, _t.OtherAgencyId });
        await using var reader = await cmd.ExecuteReaderAsync();

        var rows = 0;
        while (await reader.ReadAsync())
        {
            rows++;
            Assert.Equal("active", reader.GetString(0));
            Assert.False(reader.GetBoolean(1));  // not superuser
            Assert.False(reader.GetBoolean(2));  // cannot bypass RLS
            Assert.False(reader.GetBoolean(3));  // inherits nothing
            Assert.False(reader.GetBoolean(4));  // cannot create roles
            Assert.Equal("truvo_migrator", reader.GetString(5)); // tenant role doesn't own its tables
        }
        Assert.Equal(3, rows);
    }

    [Fact]
    public async Task Provisioning_FailureLeavesNoRoleSchemaOrActiveOrganization()
    {
        var orgId = await _t.Registry.RegisterAsync("Doomed Agency", OrganizationType.Agency);
        var broken = new[] { new MigrationScript("0001_broken", "SELECT 1/0;") };

        await Assert.ThrowsAsync<PostgresException>(() => _t.NewProvisioner(broken).ProvisionPendingAsync());

        await using var conn = new NpgsqlConnection(_t.Database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT o.status,
                   EXISTS (SELECT 1 FROM pg_roles WHERE rolname = o.db_role_name),
                   EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = o.schema_name),
                   EXISTS (SELECT 1 FROM control.organization_db_credential WHERE organization_id = o.id)
            FROM control.organization o WHERE o.id = @id
            """, conn);
        cmd.Parameters.AddWithValue("id", orgId);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();

        Assert.Equal("pending", reader.GetString(0));
        Assert.False(reader.GetBoolean(1));
        Assert.False(reader.GetBoolean(2));
        Assert.False(reader.GetBoolean(3));
    }

    [Fact]
    public async Task Factory_RejectsOrganizationsThatArentProvisioned()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _t.Factory.BeginAsync(TenantScope.Organization(Guid.NewGuid())));
    }

    [Fact]
    public void StoredCredential_OnlyDecryptsForItsOwnOrganization()
    {
        var blob = _t.Protector.Protect(_t.AgencyId, "secret");

        Assert.Equal("secret", _t.Protector.Unprotect(_t.AgencyId, "test", blob));
        Assert.ThrowsAny<CryptographicException>(() => _t.Protector.Unprotect(_t.OtherAgencyId, "test", blob));
    }

    // ── Organization ↔ Organization isolation ────────────────────────────────

    [Fact]
    public async Task TenantRole_CannotReadAnotherOrganizationsSchema()
    {
        var otherSchema = $"org_{_t.OtherAgencyId:N}";
        await using var session = await _t.Factory.BeginAsync(TenantScope.Organization(_t.AgencyId));

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            ScalarAsync<long>(session, $"SELECT count(*) FROM {otherSchema}.wallet"));

        Assert.Equal(InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task TenantRole_CannotReadTheControlPlane()
    {
        await using var session = await _t.Factory.BeginAsync(TenantScope.Organization(_t.AgencyId));

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            ScalarAsync<long>(session, "SELECT count(*) FROM control.organization_db_credential"));

        Assert.Equal(InsufficientPrivilege, ex.SqlState);
    }

    // ── Outlet ↔ Outlet isolation (RLS) ──────────────────────────────────────

    [Fact]
    public async Task OrganizationScope_SeesAllOutlets_OutletScope_SeesOnlyItself()
    {
        var (first, second) = await CreateTwoOutletsAsync(_t.AgencyId);

        await using (var org = await _t.Factory.BeginAsync(TenantScope.Organization(_t.AgencyId)))
        {
            var visible = await ListIdsAsync(org, "SELECT id FROM outlet");
            Assert.Contains(first, visible);
            Assert.Contains(second, visible);
        }

        await using (var outlet = await _t.Factory.BeginAsync(TenantScope.Outlet(_t.AgencyId, first)))
        {
            Assert.Equal([first], await ListIdsAsync(outlet, "SELECT id FROM outlet"));
        }
    }

    [Fact]
    public async Task ConnectionWithoutScope_SeesNothing()
    {
        await CreateTwoOutletsAsync(_t.AgencyId);
        var dataSource = await _t.Factory.GetDataSourceAsync(_t.AgencyId, CancellationToken.None);
        await using var conn = await dataSource.OpenConnectionAsync();

        await using var outlets = new NpgsqlCommand("SELECT count(*) FROM outlet", conn);
        await using var wallets = new NpgsqlCommand("SELECT count(*) FROM wallet", conn);

        Assert.Equal(0L, await outlets.ExecuteScalarAsync());
        Assert.Equal(0L, await wallets.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Scope_DoesNotLeakToTheNextUseOfAPooledConnection()
    {
        var (outletId, _) = await CreateTwoOutletsAsync(_t.AgencyId);
        // Fresh factory → fresh pool, so the second open deterministically reuses the first connection.
        await using var factory = new TenantConnectionFactory(_t.ControlPlane, _t.Database.AppConnectionString, _t.Protector);
        var dataSource = await factory.GetDataSourceAsync(_t.AgencyId, CancellationToken.None);

        int scopedPid;
        await using (var session = await factory.BeginAsync(TenantScope.Outlet(_t.AgencyId, outletId)))
        {
            scopedPid = await ScalarAsync<int>(session, "SELECT pg_backend_pid()");
            await session.CommitAsync();
        }

        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT pg_backend_pid(), coalesce(current_setting('app.scope', true), ''), coalesce(current_setting('app.outlet_id', true), '')", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();

        Assert.Equal(scopedPid, reader.GetInt32(0)); // same physical connection, reused from the pool
        Assert.Equal("", reader.GetString(1));
        Assert.Equal("", reader.GetString(2));
    }

    [Fact]
    public async Task OutletScope_CannotCreateOutlets()
    {
        var (outletId, _) = await CreateTwoOutletsAsync(_t.InstitutionId);
        await using var session = await _t.Factory.BeginAsync(TenantScope.Outlet(_t.InstitutionId, outletId));

        var ex = await Assert.ThrowsAsync<PostgresException>(() => TenantOutlets.CreateAsync(session, "Rogue branch", null));

        Assert.Equal(InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task OutletScope_CannotRecordCallsAgainstASiblingOutlet()
    {
        var (first, second) = await CreateTwoOutletsAsync(_t.AgencyId);
        await using var session = await _t.Factory.BeginAsync(TenantScope.Outlet(_t.AgencyId, first));

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(session, $"""
            INSERT INTO verification_call (outlet_id, user_id, verification_type, subject_ref)
            VALUES ('{second}', '{Guid.NewGuid()}', 'nin', 'hash')
            """));

        Assert.Equal(InsufficientPrivilege, ex.SqlState);
    }

    // ── Wallet scope (§2.3) ──────────────────────────────────────────────────

    [Fact]
    public async Task InstitutionOutlets_ShareTheOrganizationWallet()
    {
        var (first, second) = await CreateTwoOutletsAsync(_t.InstitutionId);

        await using var org = await _t.Factory.BeginAsync(TenantScope.Organization(_t.InstitutionId));
        var organizationWallet = await ScalarAsync<Guid>(org, "SELECT id FROM wallet WHERE kind = 'organization'");
        var outletWallets = await ListIdsAsync(org, $"SELECT wallet_id FROM outlet WHERE id IN ('{first}', '{second}')");

        Assert.All(outletWallets, w => Assert.Equal(organizationWallet, w));

        // The branch sees (and can later debit) the shared wallet, but not its sibling's rows.
        await using var branch = await _t.Factory.BeginAsync(TenantScope.Outlet(_t.InstitutionId, first));
        Assert.Equal([organizationWallet], await ListIdsAsync(branch, "SELECT id FROM wallet"));
        Assert.Equal([first], await ListIdsAsync(branch, "SELECT id FROM outlet"));
    }

    [Fact]
    public async Task AgencyOutlets_EachGetTheirOwnWallet()
    {
        var (first, second) = await CreateTwoOutletsAsync(_t.AgencyId);

        await using var org = await _t.Factory.BeginAsync(TenantScope.Organization(_t.AgencyId));
        var firstWallet = await ScalarAsync<Guid>(org, $"SELECT wallet_id FROM outlet WHERE id = '{first}'");
        var secondWallet = await ScalarAsync<Guid>(org, $"SELECT wallet_id FROM outlet WHERE id = '{second}'");
        Assert.NotEqual(firstWallet, secondWallet);

        await using var outlet = await _t.Factory.BeginAsync(TenantScope.Outlet(_t.AgencyId, first));
        Assert.Equal([firstWallet], await ListIdsAsync(outlet, "SELECT id FROM wallet"));
    }

    [Fact]
    public async Task AgencyOutlet_CannotBePointedAtAnotherOutletsWallet()
    {
        var (first, _) = await CreateTwoOutletsAsync(_t.AgencyId);
        await using var org = await _t.Factory.BeginAsync(TenantScope.Organization(_t.AgencyId));
        var takenWallet = await ScalarAsync<Guid>(org, $"SELECT wallet_id FROM outlet WHERE id = '{first}'");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(org,
            $"INSERT INTO outlet (name, wallet_id) VALUES ('Freeloader', '{takenWallet}')"));

        Assert.Equal("23514", ex.SqlState);
    }

    [Fact]
    public async Task Agency_CanSellCreditToItsOutlet_AsTheTenantRole()
    {
        var (outletId, _) = await CreateTwoOutletsAsync(_t.AgencyId);
        var wallets = new TenantWalletService();
        await using (var fund = await _t.Factory.BeginAsync(TenantScope.Organization(_t.AgencyId)))
        {
            await wallets.CreditAsync(fund, 50_000, null, $"fund-{Guid.NewGuid():N}");
            await fund.CommitAsync();
        }

        await using var org = await _t.Factory.BeginAsync(TenantScope.Organization(_t.AgencyId));
        var (seller, outlet) = await wallets.TransferToOutletAsync(org, outletId, 20_000, $"resale-{Guid.NewGuid():N}");
        await org.CommitAsync();

        Assert.Equal(20_000, outlet.BalanceAfterKobo);
        await using var outletScope = await _t.Factory.BeginAsync(TenantScope.Outlet(_t.AgencyId, outletId));
        Assert.Equal(20_000, (await wallets.GetBalanceAsync(outletScope)).BalanceKobo);
        Assert.True(seller.BalanceAfterKobo >= 30_000);
    }

    [Fact]
    public async Task Ledger_IsAppendOnlyForTheTenantRole()
    {
        await using var org = await _t.Factory.BeginAsync(TenantScope.Organization(_t.AgencyId));
        var walletId = await ScalarAsync<Guid>(org, "SELECT id FROM wallet WHERE kind = 'organization'");
        await ExecuteAsync(org, $"""
            INSERT INTO wallet_ledger_entry (wallet_id, entry_type, amount_kobo, balance_after_kobo)
            VALUES ('{walletId}', 'credit', 100000, 100000)
            """);

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(org, "UPDATE wallet_ledger_entry SET amount_kobo = 1"));

        Assert.Equal(InsufficientPrivilege, ex.SqlState);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<(Guid First, Guid Second)> CreateTwoOutletsAsync(Guid organizationId)
    {
        await using var session = await _t.Factory.BeginAsync(TenantScope.Organization(organizationId));
        var first = await TenantOutlets.CreateAsync(session, "Branch A", null);
        var second = await TenantOutlets.CreateAsync(session, "Branch B", null);
        await session.CommitAsync();
        return (first, second);
    }

    private static async Task<T> ScalarAsync<T>(TenantSession session, string sql)
    {
        await using var cmd = session.CreateCommand(sql);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<List<Guid>> ListIdsAsync(TenantSession session, string sql)
    {
        var ids = new List<Guid>();
        await using var cmd = session.CreateCommand(sql);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            ids.Add(reader.GetGuid(0));
        return ids;
    }

    private static async Task ExecuteAsync(TenantSession session, string sql)
    {
        await using var cmd = session.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }
}
