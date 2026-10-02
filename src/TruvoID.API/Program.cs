using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using TruvoID.API.Auth;
using Npgsql;
using System.Text;
using TruvoID.API.Endpoints;
using TruvoID.Core.Interfaces;
using TruvoID.Infrastructure.Postgres;
using TruvoID.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Postgres admin commands ────────────────────────────────────────────────
// `dotnet TruvoID.API.dll migrate`           control plane + every tenant schema
// `dotnet TruvoID.API.dll provision-tenants` create schema + role for pending Organizations
// `dotnet TruvoID.API.dll worker`            loop: provision-tenants + relay-revenue every few seconds
// All run with the DDL-owning migrator role as separate processes, so the running
// API only ever holds DML-only credentials.
if (args.FirstOrDefault() is "migrate" or "provision-tenants" or "relay-revenue" or "worker")
{
    var migratorConnectionString = NormalizePostgresConnectionString(
        builder.Configuration.GetConnectionString("PostgresMigrator")
        ?? throw new InvalidOperationException("ConnectionStrings:PostgresMigrator is not set."));
    using var loggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
    var logger = loggerFactory.CreateLogger("Postgres");

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
    else if (args[0] == "worker")
    {
        // Newly registered Organizations stay 'pending' until provisioned, so this
        // runs as its own always-on service rather than only at deploy time.
        var interval = TimeSpan.FromSeconds(builder.Configuration.GetValue("Worker:IntervalSeconds", 10));
        var provisioner = new TenantProvisioner(migratorConnectionString, CreateTenantCredentialProtector(builder.Configuration), logger);
        var relay = new RevenueOutboxRelay(migratorConnectionString);

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

var postgresConnectionString = NormalizePostgresConnectionString(
    builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Postgres is required for the Core API runtime. " +
        "Use the DML-only truvo_app role, not PostgresMigrator."));

builder.Services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(postgresConnectionString));
builder.Services.AddSingleton<PostgresApiKeyStore>();
builder.Services.AddSingleton<OrganizationSetupStore>();
builder.Services.AddSingleton<OrganizationBrandingStore>();
builder.Services.AddSingleton(CreateTenantCredentialProtector(builder.Configuration));
builder.Services.AddSingleton<TenantConnectionFactory>(sp => new TenantConnectionFactory(
    sp.GetRequiredService<NpgsqlDataSource>(),
    postgresConnectionString,
    sp.GetRequiredService<TenantCredentialProtector>()));
builder.Services.AddScoped<ControlPlaneIdentityStore>();
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

// ── JWT auth ──────────────────────────────────────────────────────────────
// Railway sets Jwt__SecretKey / Jwt__Issuer / Jwt__Audience (maps to Jwt:SecretKey
// etc. via the env var provider) — must read the same keys AuthEndpoints uses to
// sign tokens, or issued tokens fail validation here.
var jwtSecret = builder.Configuration["Jwt:SecretKey"]
    ?? Environment.GetEnvironmentVariable("JWT_SECRET")
    ?? (builder.Environment.IsDevelopment() ? "dev-secret-key-change-in-production-32chars!!!" : null);
if (string.IsNullOrWhiteSpace(jwtSecret))
    throw new InvalidOperationException("Jwt:SecretKey (or JWT_SECRET) is required outside Development.");
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "TruvoID";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "TruvoID";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ClockSkew = TimeSpan.Zero
        };
    })
    // Lets /v1/tenant/verification-calls/reserve accept an institution's own API key (X-API-Key header) as
    // an alternative to a JWT — see ApiKeyAuthenticationHandler.
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);

builder.Services.AddAuthorization(options =>
{
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

var corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>()
    ?? ["https://gettruvoid.com", "https://www.gettruvoid.com", "http://localhost:5173"];
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy => policy
        .WithOrigins(corsOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod());
});

// ── Application services ──────────────────────────────────────────────────
builder.Services.AddScoped<IAuditService, PostgresAuditService>();

// ── Build & map endpoints ─────────────────────────────────────────────────
var app = builder.Build();

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

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapTruvoIdEndpoints();

app.Run();

// Railway (and most hosts) hand out postgres:// URLs, which Npgsql rejects —
// NpgsqlDataSource.Create then throws on first use and every DB-backed endpoint
// returns 500. Accept either form and convert URLs to key/value.
static string NormalizePostgresConnectionString(string value)
{
    if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
        !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        return value;

    var uri = new Uri(value);
    var userInfo = uri.UserInfo.Split(':', 2);
    var builder = new NpgsqlConnectionStringBuilder
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
        _ => SslMode.Prefer
    };
    return builder.ConnectionString;
}

// Postgres:TenantCredentialKey is a base64 32-byte key (openssl rand -base64 32).
static TenantCredentialProtector CreateTenantCredentialProtector(IConfiguration configuration) =>
    TenantCredentialProtector.FromBase64(
        configuration["Postgres:TenantCredentialKeyId"] ?? "k1",
        configuration["Postgres:TenantCredentialKey"]
            ?? throw new InvalidOperationException("Postgres:TenantCredentialKey is not set."));
