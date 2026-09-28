using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using TruvoID.API.Auth;
using Npgsql;
using System.Text;
using TruvoID.API.Endpoints;
using TruvoID.Core.Interfaces;
using TruvoID.Infrastructure.Postgres;

var builder = WebApplication.CreateBuilder(args);

// ── Postgres admin commands ────────────────────────────────────────────────
// `dotnet TruvoID.API.dll migrate`           control plane + every tenant schema
// `dotnet TruvoID.API.dll provision-tenants` create schema + role for pending Organizations
// Both run with the DDL-owning migrator role as separate steps, so the running
// API only ever holds DML-only credentials.
if (args.FirstOrDefault() is "migrate" or "provision-tenants" or "relay-revenue")
{
    var migratorConnectionString = builder.Configuration.GetConnectionString("PostgresMigrator")
        ?? throw new InvalidOperationException("ConnectionStrings:PostgresMigrator is not set.");
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

var postgresConnectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Postgres is required for the Core API runtime. " +
        "Use the DML-only truvo_app role, not PostgresMigrator.");

builder.Services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(postgresConnectionString));
builder.Services.AddSingleton<PostgresApiKeyStore>();
builder.Services.AddSingleton(CreateTenantCredentialProtector(builder.Configuration));
builder.Services.AddSingleton<TenantConnectionFactory>(sp => new TenantConnectionFactory(
    sp.GetRequiredService<NpgsqlDataSource>(),
    postgresConnectionString,
    sp.GetRequiredService<TenantCredentialProtector>()));
builder.Services.AddScoped<ControlPlaneIdentityStore>();
builder.Services.AddScoped<TenantWalletService>();
builder.Services.AddScoped<TenantVerificationService>();

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
    // Lets /v1/verify/* accept an institution's own API key (X-API-Key header) as
    // an alternative to a JWT — see ApiKeyAuthenticationHandler.
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("TruvoAdmin", policy =>
        policy.RequireAuthenticatedUser()
              .RequireRole("Admin", "SuperAdmin", "PlatformAdmin", "platform_admin"));
    options.AddPolicy("TenantManager", policy =>
        policy.RequireAuthenticatedUser()
              .RequireRole("Admin", "institution_admin", "agency_admin", "agency_user"));
});

var corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>()
    ?? ["http://localhost:5173"];
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

app.UseRouting();
app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();

app.MapTruvoIdEndpoints();

app.Run();

// Postgres:TenantCredentialKey is a base64 32-byte key (openssl rand -base64 32).
static TenantCredentialProtector CreateTenantCredentialProtector(IConfiguration configuration) =>
    TenantCredentialProtector.FromBase64(
        configuration["Postgres:TenantCredentialKeyId"] ?? "k1",
        configuration["Postgres:TenantCredentialKey"]
            ?? throw new InvalidOperationException("Postgres:TenantCredentialKey is not set."));
