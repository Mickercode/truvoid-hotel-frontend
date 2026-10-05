using Npgsql;
using TruvoID.API.Endpoints;
using TruvoID.Infrastructure.Postgres;

namespace TruvoID.Tests;

// Regression coverage for the key-environment rules: an organization can always mint
// a free test key (trv_test_…) — approval only gates live keys — and a sandbox
// deployment can never issue a live key.
public class ApiKeyEnvironmentTests(ControlPlaneDatabase db) : IClassFixture<ControlPlaneDatabase>
{
    private static async Task<Guid> NewOrganizationAsync(NpgsqlDataSource dataSource)
    {
        var identities = new ControlPlaneIdentityStore(dataSource);
        var registered = await identities.RegisterOrganizationAsync(
            $"Key Test {Guid.NewGuid():N}", OrganizationType.Institution,
            "Key Tester", $"keys-{Guid.NewGuid():N}@gettruvoid.com", "Password123");
        return registered.OrganizationId;
    }

    [Fact]
    public async Task Live_deployment_issues_a_test_key_by_default()
    {
        await using var dataSource = NpgsqlDataSource.Create(db.AppConnectionString);
        var organizationId = await NewOrganizationAsync(dataSource);
        var keys = new PostgresApiKeyStore(dataSource, "live");

        var (stored, raw) = await ApiKeyEndpoints.CreateAsync(
            keys, organizationId, null, "integration", Guid.Empty, default, environment: null);

        Assert.StartsWith("trv_test_", raw);
        Assert.Equal("test", stored.Environment);
        Assert.Equal(organizationId, stored.OrganizationId);
    }

    [Fact]
    public async Task Live_deployment_issues_a_live_key_when_asked()
    {
        await using var dataSource = NpgsqlDataSource.Create(db.AppConnectionString);
        var organizationId = await NewOrganizationAsync(dataSource);
        var keys = new PostgresApiKeyStore(dataSource, "live");

        var (stored, raw) = await ApiKeyEndpoints.CreateAsync(
            keys, organizationId, null, "production", Guid.Empty, default, environment: "live");

        Assert.StartsWith("trv_live_", raw);
        Assert.Equal("live", stored.Environment);
    }

    [Fact]
    public async Task Sandbox_deployment_forces_test_keys()
    {
        await using var dataSource = NpgsqlDataSource.Create(db.AppConnectionString);
        var organizationId = await NewOrganizationAsync(dataSource);
        var keys = new PostgresApiKeyStore(dataSource, "test");

        var (stored, raw) = await ApiKeyEndpoints.CreateAsync(
            keys, organizationId, null, "wants live", Guid.Empty, default, environment: "live");

        Assert.StartsWith("trv_test_", raw);
        Assert.Equal("test", stored.Environment);
    }
}
