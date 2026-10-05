using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using TruvoID.Infrastructure.Postgres;

namespace TruvoID.IntegrationTests;

/// <summary>
/// Hosts the real API with <see cref="WebApplicationFactory{TEntryPoint}"/> against a real
/// Postgres, so tests cover the actual middleware, authorization policies, and endpoints
/// (not a reimplementation of them).
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string WebhookHash = "integration-webhook-hash";
    public static readonly string TenantCredentialKey = Convert.ToBase64String(new byte[32]);

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17")
        .WithDatabase("truvoid")
        .Build();

    public string AppConnectionString { get; private set; } = string.Empty;
    public string MigratorConnectionString { get; private set; } = string.Empty;

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

        // The API reads its configuration eagerly at startup — before WebApplicationFactory's
        // ConfigureAppConfiguration callbacks run — so it has to come from the environment.
        Set("ConnectionStrings__Postgres", AppConnectionString);
        Set("Jwt__SecretKey", "integration-test-secret-key-at-least-32-chars!!");
        Set("Postgres__TenantCredentialKey", TenantCredentialKey);
        Set("Postgres__TenantCredentialKeyId", "k1");
        Set("Verification__Provider", "sandbox");
        Set("Flutterwave__WebhookHash", WebhookHash);
        Set("Cors__Origins__0", "https://example.com");
        Set("RateLimits__AuthPerMinute", "10000");
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _container.DisposeAsync();
    }

    private static void Set(string name, string value) => Environment.SetEnvironmentVariable(name, value);

    private static string WithCredentials(string connectionString, string user, string password) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Username = user, Password = password }.ConnectionString;

    private static string ReadResource(string name)
    {
        using var stream = typeof(ApiFactory).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
