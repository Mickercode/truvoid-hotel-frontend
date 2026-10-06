using System.Net;
using System.Net.Http.Headers;
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

    [Fact]
    public async Task Outlet_users_read_scoped_history_and_admins_manage_outlets()
    {
        await using var control = NpgsqlDataSource.Create(factory.AppConnectionString);
        var identities = new ControlPlaneIdentityStore(control);
        var adminEmail = $"agency-{Guid.NewGuid():N}@gettruvoid.com";
        var agency = await identities.RegisterOrganizationAsync(
            $"Agency {Guid.NewGuid():N}", OrganizationType.Agency, "Agency Admin", adminEmail, "CorrectPass123");

        var protector = TenantCredentialProtector.FromBase64("k1", ApiFactory.TenantCredentialKey);
        await new TenantProvisioner(factory.MigratorConnectionString, protector, NullLogger.Instance).ProvisionPendingAsync();

        var tenants = new TenantConnectionFactory(control, factory.AppConnectionString, protector);
        Guid outletId;
        await using (var session = await tenants.BeginAsync(TenantScope.Organization(agency.OrganizationId), CancellationToken.None))
        {
            outletId = await TenantOutlets.CreateAsync(session, "Lagos branch", null);
            await session.CommitAsync(CancellationToken.None);
        }

        // An outlet_staff user pinned to that outlet (the CHECK constraint requires both ids).
        var outletEmail = $"outlet-{Guid.NewGuid():N}@gettruvoid.com";
        await using (var insert = control.CreateCommand("""
            INSERT INTO control.app_user (id, email, full_name, credential_hash, organization_id, outlet_id, role, status)
            VALUES (@id, @email, 'Outlet Staff', @hash, @org, @outlet, 'outlet_staff', 'active')
            """))
        {
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("email", outletEmail);
            insert.Parameters.AddWithValue("hash", PasswordHasher.Hash("CorrectPass123"));
            insert.Parameters.AddWithValue("org", agency.OrganizationId);
            insert.Parameters.AddWithValue("outlet", outletId);
            await insert.ExecuteNonQueryAsync();
        }

        var client = factory.CreateClient();

        // An outlet user may read verification history (previously 403) and is RLS-scoped.
        var outletLogin = await client.PostAsJsonAsync("/v1/auth/login", new { email = outletEmail, password = "CorrectPass123" });
        Assert.Equal(HttpStatusCode.OK, outletLogin.StatusCode);
        var outletToken = (await outletLogin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        var outletClient = factory.CreateClient();
        outletClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", outletToken);
        Assert.Equal(HttpStatusCode.OK, (await outletClient.GetAsync("/v1/tenant/verification-calls")).StatusCode);

        // An agency admin can read outlet detail and suspend/reactivate it.
        var adminLogin = await client.PostAsJsonAsync("/v1/auth/login", new { email = adminEmail, password = "CorrectPass123" });
        Assert.Equal(HttpStatusCode.OK, adminLogin.StatusCode);
        var adminToken = (await adminLogin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        var adminClient = factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var detail = await adminClient.GetAsync($"/v1/tenant/outlets/{outletId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal("active", (await detail.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.OK, (await adminClient.PostAsync($"/v1/tenant/outlets/{outletId}/suspend", new StringContent(""))).StatusCode);
        var after = await adminClient.GetAsync($"/v1/tenant/outlets/{outletId}");
        Assert.Equal("suspended", (await after.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Profile_setup_can_upload_submit_and_be_approved()
    {
        await using var control = NpgsqlDataSource.Create(factory.AppConnectionString);
        var identities = new ControlPlaneIdentityStore(control);
        var email = $"setup-{Guid.NewGuid():N}@gettruvoid.com";
        var organization = await identities.RegisterOrganizationAsync(
            $"Setup {Guid.NewGuid():N}", OrganizationType.Institution, "Setup Admin", email, "CorrectPass123");

        // Live mode also depends on the organization being provisioned (active).
        var protector = TenantCredentialProtector.FromBase64("k1", ApiFactory.TenantCredentialKey);
        await new TenantProvisioner(factory.MigratorConnectionString, protector, NullLogger.Instance).ProvisionPendingAsync();

        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/v1/auth/login", new { email, password = "CorrectPass123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        foreach (var section in new[] { "general", "contacts", "business", "ownership", "directors", "services", "compliance", "legal" })
        {
            object payload = section == "general"
                ? new { registeredCompanyName = "Acme Ltd", country = "Nigeria" }
                : new { filled = true };
            var save = await client.PutAsJsonAsync($"/v1/tenant/setup/{section}", payload);
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/v1/tenant/setup/access-level", new { level = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/v1/tenant/setup/attestation", new { accepted = true })).StatusCode);

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.4 test document"));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "certificate.pdf");
        form.Add(new StringContent("certificate_of_incorporation"), "documentType");
        var upload = await client.PostAsync("/v1/tenant/setup/documents", form);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        var submit = await client.PostAsync("/v1/tenant/setup/submit", new StringContent(""));
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);

        var adminEmail = $"platform-{Guid.NewGuid():N}@gettruvoid.com";
        var bootstrap = await new PlatformAdminBootstrapper(factory.MigratorConnectionString).CreateAsync(adminEmail, "Platform Admin");
        var adminClient = factory.CreateClient();
        var adminLogin = await adminClient.PostAsJsonAsync("/v1/admin/auth/login", new { email = adminEmail, password = bootstrap.Password });
        Assert.Equal(HttpStatusCode.OK, adminLogin.StatusCode);
        var adminToken = (await adminLogin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        // The admin review must see the submitted section data, not an empty profile.
        var review = await adminClient.GetAsync($"/v1/admin/organizations/{organization.OrganizationId}/setup");
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        var reviewBody = await review.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("submitted", reviewBody.GetProperty("status").GetString());
        Assert.Equal("Acme Ltd", reviewBody.GetProperty("sections").GetProperty("general").GetProperty("registeredCompanyName").GetString());

        var approve = await adminClient.PostAsJsonAsync($"/v1/admin/organizations/{organization.OrganizationId}/setup/approve", new { note = (string?)null });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        // Approval flips the organization to live mode.
        var me = await client.GetAsync("/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var meBody = await me.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("approved", meBody.GetProperty("setupStatus").GetString());
        Assert.True(meBody.GetProperty("liveEnabled").GetBoolean());
    }

    [Fact]
    public async Task Document_upload_rejects_a_disallowed_file_type()
    {
        await using var control = NpgsqlDataSource.Create(factory.AppConnectionString);
        var identities = new ControlPlaneIdentityStore(control);
        var email = $"reject-{Guid.NewGuid():N}@gettruvoid.com";
        await identities.RegisterOrganizationAsync(
            $"Reject {Guid.NewGuid():N}", OrganizationType.Institution, "Reject Admin", email, "CorrectPass123");

        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/v1/auth/login", new { email, password = "CorrectPass123" });
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.ASCII.GetBytes("MZ")), "file", "malware.exe");
        form.Add(new StringContent("other"), "documentType");
        var response = await client.PostAsync("/v1/tenant/setup/documents", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Platform_admin_can_credit_an_organization_wallet()
    {
        await using var control = NpgsqlDataSource.Create(factory.AppConnectionString);
        var identities = new ControlPlaneIdentityStore(control);
        var organization = await identities.RegisterOrganizationAsync(
            $"Credit {Guid.NewGuid():N}", OrganizationType.Institution, "Credit Admin",
            $"credit-{Guid.NewGuid():N}@gettruvoid.com", "CorrectPass123");

        var protector = TenantCredentialProtector.FromBase64("k1", ApiFactory.TenantCredentialKey);
        await new TenantProvisioner(factory.MigratorConnectionString, protector, NullLogger.Instance).ProvisionPendingAsync();

        var adminEmail = $"platform-{Guid.NewGuid():N}@gettruvoid.com";
        var bootstrap = await new PlatformAdminBootstrapper(factory.MigratorConnectionString).CreateAsync(adminEmail, "Platform Admin");
        var adminClient = factory.CreateClient();
        var adminLogin = await adminClient.PostAsJsonAsync("/v1/admin/auth/login", new { email = adminEmail, password = bootstrap.Password });
        var adminToken = (await adminLogin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var credit = await adminClient.PostAsJsonAsync(
            $"/v1/admin/tenant-wallets/{organization.OrganizationId}/credit", new { amountKobo = 500_000 });
        Assert.Equal(HttpStatusCode.OK, credit.StatusCode);
        var body = await credit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(500_000, body.GetProperty("balanceAfterKobo").GetInt64());

        // The organizations list echoes the balance so the dashboard can show it.
        var list = await adminClient.GetAsync("/v1/admin/organizations");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var row = (await list.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .First(o => o.GetProperty("id").GetString() == organization.OrganizationId.ToString());
        Assert.Equal(500_000, row.GetProperty("balanceKobo").GetInt64());

        var invalid = await adminClient.PostAsJsonAsync(
            $"/v1/admin/tenant-wallets/{organization.OrganizationId}/credit", new { amountKobo = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Submit_rejects_a_profile_with_only_blank_sections()
    {
        await using var control = NpgsqlDataSource.Create(factory.AppConnectionString);
        var identities = new ControlPlaneIdentityStore(control);
        var email = $"blank-{Guid.NewGuid():N}@gettruvoid.com";
        await identities.RegisterOrganizationAsync(
            $"Blank {Guid.NewGuid():N}", OrganizationType.Institution, "Blank Admin", email, "CorrectPass123");

        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/v1/auth/login", new { email, password = "CorrectPass123" });
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // A section with only blank values is not "provided" and must not count as complete.
        foreach (var section in new[] { "general", "contacts", "business", "ownership", "directors", "services", "compliance", "legal" })
        {
            var save = await client.PutAsJsonAsync($"/v1/tenant/setup/{section}", new { field = "" });
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        }

        var submit = await client.PostAsync("/v1/tenant/setup/submit", new StringContent(""));
        Assert.Equal(HttpStatusCode.Conflict, submit.StatusCode);
    }

    [Fact]
    public async Task Admin_can_request_changes_before_submission_but_not_approve()
    {
        await using var control = NpgsqlDataSource.Create(factory.AppConnectionString);
        var identities = new ControlPlaneIdentityStore(control);
        var organization = await identities.RegisterOrganizationAsync(
            $"Incomplete {Guid.NewGuid():N}", OrganizationType.Institution, "Incomplete Admin",
            $"incomplete-{Guid.NewGuid():N}@gettruvoid.com", "CorrectPass123");

        var adminEmail = $"platform-{Guid.NewGuid():N}@gettruvoid.com";
        var bootstrap = await new PlatformAdminBootstrapper(factory.MigratorConnectionString).CreateAsync(adminEmail, "Platform Admin");
        var adminClient = factory.CreateClient();
        var adminLogin = await adminClient.PostAsJsonAsync("/v1/admin/auth/login", new { email = adminEmail, password = bootstrap.Password });
        var adminToken = (await adminLogin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        // A reviewer can send an unfinished profile back with guidance...
        var changes = await adminClient.PostAsJsonAsync(
            $"/v1/admin/organizations/{organization.OrganizationId}/setup/request-changes",
            new { note = "Please finish and resubmit." });
        Assert.Equal(HttpStatusCode.OK, changes.StatusCode);

        // ...but cannot approve a profile that was never submitted.
        var approve = await adminClient.PostAsJsonAsync(
            $"/v1/admin/organizations/{organization.OrganizationId}/setup/approve", new { note = (string?)null });
        Assert.Equal(HttpStatusCode.Conflict, approve.StatusCode);
    }

    [Fact]
    public async Task Live_verification_is_accepted_after_approval()
    {
        await using var control = NpgsqlDataSource.Create(factory.AppConnectionString);
        var identities = new ControlPlaneIdentityStore(control);
        var email = $"live-{Guid.NewGuid():N}@gettruvoid.com";
        var organization = await identities.RegisterOrganizationAsync(
            $"Live {Guid.NewGuid():N}", OrganizationType.Institution, "Live Admin", email, "CorrectPass123");

        var protector = TenantCredentialProtector.FromBase64("k1", ApiFactory.TenantCredentialKey);
        await new TenantProvisioner(factory.MigratorConnectionString, protector, NullLogger.Instance).ProvisionPendingAsync();

        // Approved profile, a NIN price to bill against, and a funded wallet.
        await using (var approve = control.CreateCommand("""
            INSERT INTO control.organization_setup (organization_id, status, access_level, attested_at)
            VALUES (@org, 'approved', 2, now())
            ON CONFLICT (organization_id) DO UPDATE SET status = 'approved'
            """))
        {
            approve.Parameters.AddWithValue("org", organization.OrganizationId);
            await approve.ExecuteNonQueryAsync();
        }
        await using (var rate = control.CreateCommand(
            "INSERT INTO control.platform_rate (verification_type, price_kobo, cost_kobo, effective_from) VALUES ('nin', 5000, 1000, now())"))
        {
            await rate.ExecuteNonQueryAsync();
        }

        var tenants = new TenantConnectionFactory(control, factory.AppConnectionString, protector);
        await using (var session = await tenants.BeginAsync(TenantScope.Organization(organization.OrganizationId), CancellationToken.None))
        {
            await new TenantWalletService().CreditAsync(session, 1_000_000, null, "integration-funds", ct: CancellationToken.None);
            await session.CommitAsync(CancellationToken.None);
        }

        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/v1/auth/login", new { email, password = "CorrectPass123" });
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var me = await client.GetAsync("/v1/auth/me");
        Assert.True((await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("liveEnabled").GetBoolean());

        // A live-mode verification must not be refused with live_not_enabled.
        var verify = new HttpRequestMessage(HttpMethod.Post, "/v1/verify/nin")
        {
            Content = JsonContent.Create(new { number = "00000000001" }),
        };
        verify.Headers.Add("X-TruvoID-Mode", "live");

        var response = await client.SendAsync(verify);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Verification_history_works_for_free_test_mode_calls()
    {
        await using var control = NpgsqlDataSource.Create(factory.AppConnectionString);
        var identities = new ControlPlaneIdentityStore(control);
        var email = $"history-{Guid.NewGuid():N}@gettruvoid.com";
        await identities.RegisterOrganizationAsync(
            $"History {Guid.NewGuid():N}", OrganizationType.Institution, "History Admin", email, "CorrectPass123");

        var protector = TenantCredentialProtector.FromBase64("k1", ApiFactory.TenantCredentialKey);
        await new TenantProvisioner(factory.MigratorConnectionString, protector, NullLogger.Instance).ProvisionPendingAsync();

        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/v1/auth/login", new { email, password = "CorrectPass123" });
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // A free test-mode call has no wallet charge, so ledger_entry_id is null.
        var verify = new HttpRequestMessage(HttpMethod.Post, "/v1/verify/nin")
        {
            Content = JsonContent.Create(new { number = "00000000001" }),
        };
        verify.Headers.Add("X-TruvoID-Mode", "test");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(verify)).StatusCode);

        var history = await client.GetAsync("/v1/tenant/verification-calls");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var items = (await history.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items");
        Assert.True(items.GetArrayLength() >= 1);
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("ledgerEntryId").ValueKind);
    }

    [Fact]
    public async Task Change_password_rotates_the_login_credentials()
    {
        await using var control = NpgsqlDataSource.Create(factory.AppConnectionString);
        var identities = new ControlPlaneIdentityStore(control);
        var email = $"pw-{Guid.NewGuid():N}@gettruvoid.com";
        await identities.RegisterOrganizationAsync(
            $"PW {Guid.NewGuid():N}", OrganizationType.Institution, "PW Admin", email, "CorrectPass123");

        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/v1/auth/login", new { email, password = "CorrectPass123" });
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var change = await client.PostAsJsonAsync("/v1/auth/change-password",
            new { currentPassword = "CorrectPass123", newPassword = "NewPass456" });
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateClient().PostAsJsonAsync("/v1/auth/login", new { email, password = "CorrectPass123" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await factory.CreateClient().PostAsJsonAsync("/v1/auth/login", new { email, password = "NewPass456" })).StatusCode);
    }

    [Fact]
    public async Task Organization_admin_can_deactivate_the_organization()
    {
        await using var control = NpgsqlDataSource.Create(factory.AppConnectionString);
        var identities = new ControlPlaneIdentityStore(control);
        var email = $"deact-{Guid.NewGuid():N}@gettruvoid.com";
        await identities.RegisterOrganizationAsync(
            $"Deact {Guid.NewGuid():N}", OrganizationType.Institution, "Deact Admin", email, "CorrectPass123");

        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/v1/auth/login", new { email, password = "CorrectPass123" });
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var deactivate = await client.PostAsJsonAsync("/v1/auth/deactivate", new { password = "CorrectPass123" });
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        // The account/organization can no longer sign in.
        var after = await factory.CreateClient().PostAsJsonAsync("/v1/auth/login", new { email, password = "CorrectPass123" });
        Assert.NotEqual(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task Platform_admin_can_read_the_audit_log()
    {
        var adminEmail = $"platform-{Guid.NewGuid():N}@gettruvoid.com";
        var bootstrap = await new PlatformAdminBootstrapper(factory.MigratorConnectionString).CreateAsync(adminEmail, "Platform Admin");
        var adminClient = factory.CreateClient();
        var adminLogin = await adminClient.PostAsJsonAsync("/v1/admin/auth/login", new { email = adminEmail, password = bootstrap.Password });
        var adminToken = (await adminLogin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var audit = await adminClient.GetAsync("/v1/admin/audit?page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        var items = (await audit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items");
        Assert.True(items.GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Platform_admin_can_read_financials()
    {
        await using var control = NpgsqlDataSource.Create(factory.AppConnectionString);
        var identities = new ControlPlaneIdentityStore(control);
        var organization = await identities.RegisterOrganizationAsync(
            $"Fin {Guid.NewGuid():N}", OrganizationType.Institution, "Fin Admin",
            $"fin-{Guid.NewGuid():N}@gettruvoid.com", "CorrectPass123");

        await using (var seed = control.CreateCommand("""
            INSERT INTO control.slogani_revenue_ledger (occurred_at, organization_id, entry_type, amount_kobo, verification_type)
            VALUES (now(), @org, 'credit_sale', 5000, 'nin'),
                   (now(), @org, 'refund', -1000, 'nin')
            """))
        {
            seed.Parameters.AddWithValue("org", organization.OrganizationId);
            await seed.ExecuteNonQueryAsync();
        }

        var adminEmail = $"platform-{Guid.NewGuid():N}@gettruvoid.com";
        var bootstrap = await new PlatformAdminBootstrapper(factory.MigratorConnectionString).CreateAsync(adminEmail, "Platform Admin");
        var adminClient = factory.CreateClient();
        var adminLogin = await adminClient.PostAsJsonAsync("/v1/admin/auth/login", new { email = adminEmail, password = bootstrap.Password });
        var adminToken = (await adminLogin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var financials = await adminClient.GetAsync("/v1/admin/financials?days=30");
        Assert.Equal(HttpStatusCode.OK, financials.StatusCode);
        var totals = (await financials.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("totals");
        Assert.True(totals.GetProperty("creditSalesKobo").GetInt64() >= 5000);
        Assert.True(totals.GetProperty("netKobo").GetInt64() >= 4000);
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
