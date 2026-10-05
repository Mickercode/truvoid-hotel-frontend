using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TruvoID.Infrastructure.Postgres;

namespace TruvoID.IntegrationTests;

/// <summary>
/// End-to-end HTTP coverage against the real API: authorization, the Flutterwave webhook,
/// and the per-account login lockout. Requires Docker (Testcontainers Postgres).
/// </summary>
public class HttpEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Health_is_public_and_reports_status()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", body.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("/v1/tenant/wallet/balance")]
    [InlineData("/v1/tenant/setup")]
    [InlineData("/v1/admin/api-keys")]
    [InlineData("/v1/admin/pricing")]
    public async Task Protected_endpoints_require_authentication(string path)
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_rejects_a_missing_signature()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsync(
            "/v1/payments/flutterwave/webhook",
            new StringContent("{\"data\":{\"status\":\"successful\"}}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_rejects_a_wrong_signature()
    {
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments/flutterwave/webhook")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("verif-hash", "not-the-hash");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_ignores_a_payment_that_is_not_successful()
    {
        var client = factory.CreateClient();
        var response = await client.SendAsync(SignedWebhook("{\"data\":{\"status\":\"failed\"}}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_rejects_malformed_json_instead_of_crashing()
    {
        var client = factory.CreateClient();
        var response = await client.SendAsync(SignedWebhook("not-json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_locks_the_account_after_repeated_failures()
    {
        await using var dataSource = NpgsqlDataSource.Create(factory.AppConnectionString);
        var identities = new ControlPlaneIdentityStore(dataSource);
        var email = $"lock-{Guid.NewGuid():N}@gettruvoid.com";
        await identities.RegisterOrganizationAsync(
            $"Lock {Guid.NewGuid():N}", OrganizationType.Institution, "Lock Tester", email, "CorrectPass123");

        var client = factory.CreateClient();
        for (var attempt = 0; attempt < ControlPlaneIdentityStore.MaxFailedLoginAttempts; attempt++)
        {
            var failed = await client.PostAsJsonAsync("/v1/auth/login", new { email, password = "wrong-password" });
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        var locked = await client.PostAsJsonAsync("/v1/auth/login", new { email, password = "CorrectPass123" });
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
    }

    [Fact]
    public async Task Webhook_credits_a_wallet_once_and_is_idempotent()
    {
        await using var control = NpgsqlDataSource.Create(factory.AppConnectionString);
        var identities = new ControlPlaneIdentityStore(control);
        var registered = await identities.RegisterOrganizationAsync(
            $"Pay {Guid.NewGuid():N}", OrganizationType.Institution,
            "Pay Tester", $"pay-{Guid.NewGuid():N}@gettruvoid.com", "CorrectPass123");

        // Provision the organization's schema + role, then seed a pending payment row.
        var protector = TenantCredentialProtector.FromBase64("k1", ApiFactory.TenantCredentialKey);
        var provisioner = new TenantProvisioner(factory.MigratorConnectionString, protector, NullLogger.Instance);
        await provisioner.ProvisionPendingAsync();

        var txRef = $"trv_{registered.OrganizationId:N}_{Guid.NewGuid():N}";
        var tenants = new TenantConnectionFactory(control, factory.AppConnectionString, protector);
        await using (var session = await tenants.BeginAsync(TenantScope.Organization(registered.OrganizationId), CancellationToken.None))
        {
            await using var insert = session.CreateCommand(
                "INSERT INTO wallet_payment (tx_ref, amount_kobo, currency, status) VALUES (@ref, 100000, 'NGN', 'pending')");
            insert.Parameters.AddWithValue("ref", txRef);
            await insert.ExecuteNonQueryAsync();
            await session.CommitAsync(CancellationToken.None);
        }

        var client = factory.CreateClient();
        var payload = $"{{\"data\":{{\"status\":\"successful\",\"tx_ref\":\"{txRef}\",\"id\":987,\"amount\":1000,\"currency\":\"NGN\"}}}}";

        var first = await client.SendAsync(SignedWebhook(payload));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("credited", firstBody.GetProperty("status").GetString());

        // A redelivered webhook must not credit the same reference twice.
        var second = await client.SendAsync(SignedWebhook(payload));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("already_credited", secondBody.GetProperty("status").GetString());
    }

    private static HttpRequestMessage SignedWebhook(string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments/flutterwave/webhook")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("verif-hash", ApiFactory.WebhookHash);
        return request;
    }
}
