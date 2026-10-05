using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using TruvoID.API.Auth;
using Npgsql;
using System.Text;
using TruvoID.API.Endpoints;
using TruvoID.Core.Interfaces;
using TruvoID.Infrastructure.Postgres;
using TruvoID.Infrastructure.Identity;
using TruvoID.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Postgres admin commands ────────────────────────────────────────────────
// `dotnet TruvoID.API.dll migrate`           control plane + every tenant schema
// `dotnet TruvoID.API.dll provision-tenants` create schema + role for pending Organizations
// `dotnet TruvoID.API.dll worker`            loop: provision-tenants + relay-revenue every few seconds,
//                                            and refund stale pending verifications every few minutes
// `dotnet TruvoID.API.dll create-platform-admin <email> [full name] [--reset-password]`
// All run with the DDL-owning migrator role as separate processes, so the running
// API only ever holds DML-only credentials.
if (args.FirstOrDefault() is "migrate" or "provision-tenants" or "relay-revenue" or "worker" or "create-platform-admin")
{
    var migratorConnectionString = NormalizePostgresConnectionString(
        builder.Configuration.GetConnectionString("PostgresMigrator")
        ?? throw new InvalidOperationException("ConnectionStrings:PostgresMigrator is not set."));
    using var loggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
    var logger = loggerFactory.CreateLogger("Postgres");
    await PostgresMigrator.EnsureNotSuperuserAsync(migratorConnectionString);

    if (args[0] == "migrate")
    {
        var appRole = builder.Configuration["Postgres:AppRole"] ?? "truvo_app";
        var controlApplied = await PostgresMigrator.MigrateAsync(
            migratorConnectionString, appRole, PostgresMigrator.LoadEmbeddedControlPlaneScripts(), logger);
        var tenantApplied = await PostgresMigrator.MigrateTenantsAsync(
            migratorConnectionString, PostgresMigrator.LoadEmbeddedTenantScripts(), logger);
        Console.WriteLine($"Postgres up to date ({controlApplied} control-plane, {tenantApplied} tenant migration(s) applied).");
    }
    else if (args[0] == "provision-tenants")
    {
        var provisioner = new TenantProvisioner(migratorConnectionString, CreateTenantCredentialProtector(builder.Configuration), logger);
        var provisioned = await provisioner.ProvisionPendingAsync();
        Console.WriteLine($"Provisioned {provisioned} Organization(s).");
    }
    else if (args[0] == "create-platform-admin")
    {
        var reset = args.Contains("--reset-password");
        var positional = args.Skip(1).Where(a => !a.StartsWith("--")).ToArray();
        if (positional.Length == 0)
        {
            Console.Error.WriteLine("Usage: create-platform-admin <email> [full name] [--reset-password]");
            Environment.ExitCode = 2;
            return;
        }

        var bootstrapper = new PlatformAdminBootstrapper(migratorConnectionString);
        try
        {
            var result = reset
                ? await bootstrapper.ResetPasswordAsync(positional[0])
                : await bootstrapper.CreateAsync(positional[0], string.Join(' ', positional.Skip(1)));
            Console.WriteLine(result.Created ? "Platform admin created." : "Platform admin password reset.");
            Console.WriteLine($"  Email:    {result.Email}");
            Console.WriteLine($"  Password: {result.Password}");
            Console.WriteLine("This password is shown once. Sign in and change it immediately.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = 1;
        }
    }
    else if (args[0] == "worker")
    {
        // Newly registered Organizations stay 'pending' until provisioned, so this
        // runs as its own always-on service rather than only at deploy time.
        var interval = TimeSpan.FromSeconds(builder.Configuration.GetValue("Worker:IntervalSeconds", 10));
        var provisioner = new TenantProvisioner(migratorConnectionString, CreateTenantCredentialProtector(builder.Configuration), logger);
        var relay = new RevenueOutboxRelay(migratorConnectionString);

        // Stale-call refunds touch tenant data, so they go through each Organization's own
        // least-privilege role (the factory swaps the migrator's credentials for the tenant's).
        await using var controlPlane = NpgsqlDataSource.Create(migratorConnectionString);
        await using var workerTenants = new TenantConnectionFactory(
            controlPlane, migratorConnectionString, CreateTenantCredentialProtector(builder.Configuration));
        var sweeper = new VerificationSweeper(
            controlPlane, workerTenants, new TenantVerificationService(controlPlane, new TenantWalletService()), logger);
        var sweepEvery = TimeSpan.FromMinutes(builder.Configuration.GetValue("Worker:SweepMinutes", 5));
        var nextSweep = DateTime.UtcNow;

        using var stopping = new CancellationTokenSource();
        using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; stopping.Cancel(); });
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };

        logger.LogInformation("Worker started; polling every {Interval}s.", interval.TotalSeconds);
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                var provisioned = await provisioner.ProvisionPendingAsync(stopping.Token);
                if (provisioned > 0)
                    logger.LogInformation("Provisioned {Count} Organization(s).", provisioned);

                var delivered = await relay.RelayAsync(stopping.Token);
                if (delivered > 0)
                    logger.LogInformation("Delivered {Count} revenue event(s).", delivered);

                if (DateTime.UtcNow >= nextSweep)
                {
                    nextSweep = DateTime.UtcNow + sweepEvery;
                    var swept = await sweeper.SweepAsync(stopping.Token);
                    if (swept > 0)
                        logger.LogWarning("Refunded {Count} stale verification call(s).", swept);
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed Organization rolls back and stays pending, so the next pass retries it.
                logger.LogError(ex, "Worker pass failed; retrying in {Interval}s.", interval.TotalSeconds);
            }

            try { await Task.Delay(interval, stopping.Token); }
            catch (OperationCanceledException) { break; }
        }
        logger.LogInformation("Worker stopped.");
    }
    else
    {
        var delivered = await new RevenueOutboxRelay(migratorConnectionString).RelayAsync();
        Console.WriteLine($"Delivered {delivered} revenue event(s).");
    }
    return;
}

// ── Port / hosting ─────────────────────────────────────────────────────────
var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
builder.WebHost.UseUrls($"http://+:{port}");

// Outer backstop for request sizes (uploads get their own stricter checks). Without
// this Kestrel buffers an arbitrarily large body into memory.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 12 * 1024 * 1024);

var postgresConnectionString = NormalizePostgresConnectionString(
    builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Postgres is required for the Core API runtime. " +
        "Use the DML-only truvo_app role, not PostgresMigrator."));

builder.Services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(postgresConnectionString));
builder.Services.AddSingleton(sp => new PostgresApiKeyStore(
    sp.GetRequiredService<NpgsqlDataSource>(),
    // A sandbox deployment issues trv_test_ keys; production issues trv_live_ keys.
    string.Equals(builder.Configuration["Verification:Provider"], "sandbox", StringComparison.OrdinalIgnoreCase) ? "test" : "live"));
builder.Services.AddSingleton<OrganizationSetupStore>();
builder.Services.AddSingleton<OrganizationBrandingStore>();
builder.Services.AddSingleton<OrganizationInvitationStore>();
builder.Services.AddSingleton(CreateTenantCredentialProtector(builder.Configuration));
builder.Services.AddSingleton<TenantConnectionFactory>(sp => new TenantConnectionFactory(
    sp.GetRequiredService<NpgsqlDataSource>(),
    postgresConnectionString,
    sp.GetRequiredService<TenantCredentialProtector>()));
builder.Services.AddScoped<ControlPlaneIdentityStore>();
builder.Services.AddSingleton<PasswordResetStore>();
builder.Services.AddSingleton(sp => new RefreshTokenStore(
    sp.GetRequiredService<NpgsqlDataSource>(),
    TimeSpan.FromDays(builder.Configuration.GetValue("Jwt:RefreshDays", 30))));
builder.Services.AddScoped<TenantWalletService>();
builder.Services.AddScoped<TenantVerificationService>();
var resendApiKey = builder.Configuration["Resend:ApiKey"] ?? Environment.GetEnvironmentVariable("RESEND_API_KEY");
builder.Services.AddHttpClient("resend", client =>
{
    if (!string.IsNullOrWhiteSpace(resendApiKey))
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {resendApiKey}");
});
builder.Services.AddScoped<IEmailService, ResendEmailService>();
var flutterwaveSecretKey = builder.Configuration["Flutterwave:SecretKey"] ?? Environment.GetEnvironmentVariable("FLUTTERWAVE_SECRET_KEY");
builder.Services.AddHttpClient("flutterwave", client =>
{
    client.BaseAddress = new Uri("https://api.flutterwave.com/");
    if (!string.IsNullOrWhiteSpace(flutterwaveSecretKey))
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {flutterwaveSecretKey}");
});
builder.Services.AddScoped<FlutterwavePaymentService>();

// ── Identity provider ─────────────────────────────────────────────────────
// Verification__Provider=sandbox makes this deployment a sandbox: no upstream calls,
// documented test numbers, responses labelled "environment": "sandbox".
var verificationProvider = (builder.Configuration["Verification:Provider"] ?? "idaccess").Trim().ToLowerInvariant();
builder.Services.AddHttpClient(IdAccessIdentityProvider.HttpClientName, client =>
{
    client.BaseAddress = new Uri((builder.Configuration["IdAccess:BaseUrl"] ?? "https://idaccess.info/v1").TrimEnd('/') + "/");
    client.Timeout = VerificationRunner.ProviderTimeout + TimeSpan.FromSeconds(5);
});
// Every workspace has free test mode, served by this provider (see VerificationRunner).
builder.Services.AddSingleton<SandboxIdentityProvider>();
builder.Services.AddSingleton<IIdentityProvider>(sp => verificationProvider switch
{
    "sandbox" => sp.GetRequiredService<SandboxIdentityProvider>(),
    "idaccess" => new IdAccessIdentityProvider(
        sp.GetRequiredService<IHttpClientFactory>(),
        builder.Configuration["IdAccess:ApiKey"] ?? Environment.GetEnvironmentVariable("IDACCESS_API_KEY"),
        sp.GetRequiredService<ILogger<IdAccessIdentityProvider>>()),
    _ => throw new InvalidOperationException($"Unknown Verification:Provider '{verificationProvider}'. Use 'idaccess' or 'sandbox'."),
});
builder.Services.AddScoped<VerificationRunner>();

// ── JWT auth ──────────────────────────────────────────────────────────────
// Resolve once and share: AuthEndpoints signs with the same instance this validates
// with, so the two can never drift to different secrets.
var jwtSettings = JwtSettings.Resolve(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(jwtSettings);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = jwtSettings.ValidationParameters;
    })
    // Lets /v1/tenant/verification-calls/reserve accept an institution's own API key (X-API-Key header) as
    // an alternative to a JWT — see ApiKeyAuthenticationHandler.
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);

builder.Services.AddAuthorization(options =>
{
    // Deny by default: an endpoint that forgets to declare its policy requires a signed-in
    // user rather than silently becoming anonymous. Public endpoints opt out with AllowAnonymous.
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser().Build();

    // Platform staff only. Must NOT include "Admin": that is the legacy claim every
    // institution_admin / agency_admin carries, so any self-registered Organization
    // would otherwise reach /v1/admin/* (list all orgs, credit its own wallet, ...).
    options.AddPolicy("TruvoAdmin", policy =>
        policy.RequireAuthenticatedUser()
              .RequireRole("PlatformAdmin", "platform_admin"));
    options.AddPolicy("TenantManager", policy =>
        policy.RequireAuthenticatedUser()
              .RequireRole("Admin", "institution_admin", "agency_admin", "agency_user"));
});

// Production default only. Development supplies http://localhost:5173 via appsettings.Development.json,
// so a misconfigured production deploy never silently allows a localhost origin.
var corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>()
    ?? ["https://gettruvoid.com", "https://www.gettruvoid.com"];
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy => policy
        .WithOrigins(corsOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod());
});

// ── Rate limiting ─────────────────────────────────────────────────────────
// "auth": per client IP on credential endpoints (brute force / credential stuffing).
// Generous by default because Nigerian mobile carriers put many users behind one IP.
// "verify": per API key (or Organization) so a leaked key can't drain a wallet at line rate.
var authPerMinute = builder.Configuration.GetValue("RateLimits:AuthPerMinute", 20);
var verifyPerMinute = builder.Configuration.GetValue("RateLimits:VerifyPerMinute", 120);
var webhookPerMinute = builder.Configuration.GetValue("RateLimits:WebhookPerMinute", 300);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, ct) =>
    {
        if (context.Lease.TryGetMetadata(System.Threading.RateLimiting.MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { error = "Too many attempts. Please wait a moment and try again.", code = "rate_limited" }, ct);
    };
    options.AddPolicy("auth", http => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        ClientIp(http), _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
        {
            PermitLimit = authPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
        }));
    options.AddPolicy("verify", http => System.Threading.RateLimiting.RateLimitPartition.GetTokenBucketLimiter(
        http.User.FindFirst("api_key_id")?.Value ?? http.User.FindFirst("organization_id")?.Value ?? ClientIp(http),
        _ => new System.Threading.RateLimiting.TokenBucketRateLimiterOptions
        {
            TokenLimit = verifyPerMinute, TokensPerPeriod = verifyPerMinute,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true,
        }));
    // The Flutterwave webhook is anonymous, so cap it per client IP to blunt abuse.
    options.AddPolicy("webhook", http => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        ClientIp(http), _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
        {
            PermitLimit = webhookPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
        }));
});

// ── Application services ──────────────────────────────────────────────────
builder.Services.AddScoped<IAuditService, PostgresAuditService>();

// ── Build & map endpoints ─────────────────────────────────────────────────
var app = builder.Build();

// Behind Cloudflare → Railway the socket peer is the proxy, so the real client IP
// (used by the "auth" rate limit and audit) and the original scheme come from
// forwarded headers. Trust the immediate proxy; the default only trusts loopback.
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
forwardedHeaders.KnownIPNetworks.Clear();
forwardedHeaders.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeaders);

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    // Unhandled exceptions otherwise return an empty 500 body, which leaves
    // both the frontend and Railway logs with nothing to go on. Log the full
    // exception server-side and return a small JSON body the frontend can show.
    app.UseExceptionHandler(errApp =>
    {
        errApp.Run(async ctx =>
        {
            var feature = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
            if (feature?.Error is { } ex)
            {
                ctx.RequestServices.GetRequiredService<ILogger<Program>>()
                    .LogError(ex, "Unhandled exception on {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
            }

            ctx.Response.ContentType = "application/json";
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await ctx.Response.WriteAsJsonAsync(new { error = "An unexpected error occurred. Please try again." });
        });
    });
}

app.Use(async (ctx, next) =>
{
    var headers = ctx.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    if (!app.Environment.IsDevelopment())
        headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    await next();
});

app.UseRouting();
app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter(); // after auth so the "verify" policy can partition by API key / Organization

var identityProvider = app.Services.GetRequiredService<IIdentityProvider>();
if (!identityProvider.IsConfigured)
    app.Logger.LogWarning("Identity provider '{Provider}' is not configured (missing IDACCESS_API_KEY?). /v1/verify will return 503.", verificationProvider);
else
    app.Logger.LogInformation("Identity provider: {Provider} ({Environment}).", verificationProvider, identityProvider.Environment);

// Email powers invitations and password resets. A missing key used to fail silently at
// send time; say so loudly at startup so a misnamed Railway variable is obvious.
var emailConfigured = !string.IsNullOrWhiteSpace(resendApiKey);
if (emailConfigured)
    app.Logger.LogInformation("Email: Resend configured (from {From}).",
        Environment.GetEnvironmentVariable("EMAIL_FROM_ADDRESS") ?? "TruvoID <noreply@gettruvoid.com>");
else
    app.Logger.LogWarning("Email is NOT configured (set Resend__ApiKey or RESEND_API_KEY). Invitations and password-reset emails cannot be delivered.");

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    environment = identityProvider.Environment,
    email = emailConfigured ? "configured" : "not_configured",
})).AllowAnonymous();
app.MapTruvoIdEndpoints();

app.Run();

// UseForwardedHeaders has already resolved RemoteIpAddress from X-Forwarded-For
// (trusting the proxy chain), so we do not read X-Forwarded-For directly — a client
// can forge that header, which is exactly how the rate-limit key was spoofable.
// CF-Connecting-IP is preferred when present because Cloudflare sets it authoritatively.
static string ClientIp(HttpContext http) =>
    http.Request.Headers["CF-Connecting-IP"].FirstOrDefault()
    ?? http.Connection.RemoteIpAddress?.ToString()
    ?? "unknown";

// Railway (and most hosts) hand out postgres:// URLs, which Npgsql rejects —
// NpgsqlDataSource.Create then throws on first use and every DB-backed endpoint
// returns 500. Accept either form and convert URLs to key/value.
static string NormalizePostgresConnectionString(string value)
{
    NpgsqlConnectionStringBuilder builder;
    if (value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
    {
        var uri = new Uri(value);
        var userInfo = uri.UserInfo.Split(':', 2);
        builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null
        };

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        builder.SslMode = query["sslmode"]?.ToLowerInvariant() switch
        {
            "disable" => SslMode.Disable,
            "allow" => SslMode.Allow,
            "require" => SslMode.Require,
            "verify-ca" => SslMode.VerifyCA,
            "verify-full" => SslMode.VerifyFull,
            // Require (not Prefer): never silently fall back to an unencrypted connection.
            // Certificate validation needs an explicit sslmode=verify-full plus a root cert.
            _ => SslMode.Require
        };
    }
    else
    {
        builder = new NpgsqlConnectionStringBuilder(value);
    }

    // The slim runtime image ships no Kerberos libraries, so Npgsql logs a noisy
    // (non-fatal) "libgssapi_krb5.so.2" warning when it probes for GSS encryption.
    // Disable it; TLS (SslMode) is unaffected.
    builder.GssEncryptionMode = GssEncryptionMode.Disable;
    return builder.ConnectionString;
}

// Postgres:TenantCredentialKey is a base64 32-byte key (openssl rand -base64 32).
static TenantCredentialProtector CreateTenantCredentialProtector(IConfiguration configuration) =>
    TenantCredentialProtector.FromBase64(
        configuration["Postgres:TenantCredentialKeyId"] ?? "k1",
        configuration["Postgres:TenantCredentialKey"]
            ?? throw new InvalidOperationException("Postgres:TenantCredentialKey is not set."));

// Exposed so the integration tests can host this API with WebApplicationFactory<Program>.
public partial class Program;
