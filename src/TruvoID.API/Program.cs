using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using TruvoID.API.Auth;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using System.Text;
using TruvoID.API.Endpoints;
using TruvoID.Core.Interfaces;
using TruvoID.Infrastructure.Data;
using TruvoID.Infrastructure.Postgres;
using TruvoID.Infrastructure.Services;

// MongoDB.Driver 3.x no longer assumes a Guid representation — every entity here
// uses a Guid Id, so without this any insert/update touching a Guid field throws
// "Cannot serialize a Guid without knowing its representation" at the first write.
BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));

var builder = WebApplication.CreateBuilder(args);

// ── Postgres migrations (`dotnet TruvoID.API.dll migrate`) ─────────────────
// Run as a separate deploy step with the DDL-owning migrator role, so the
// running API only ever holds the DML-only app role's credentials.
if (args.FirstOrDefault() == "migrate")
{
    var migratorConnectionString = builder.Configuration.GetConnectionString("PostgresMigrator")
        ?? throw new InvalidOperationException("ConnectionStrings:PostgresMigrator is not set.");
    var appRole = builder.Configuration["Postgres:AppRole"] ?? "truvo_app";

    using var loggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
    var applied = await PostgresMigrator.MigrateAsync(
        migratorConnectionString,
        appRole,
        PostgresMigrator.LoadEmbeddedControlPlaneScripts(),
        loggerFactory.CreateLogger("PostgresMigrator"));
    Console.WriteLine($"Postgres control plane up to date ({applied} migration(s) applied).");
    return;
}

// ── Port / hosting ─────────────────────────────────────────────────────────
var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
builder.WebHost.UseUrls($"http://+:{port}");

// ── Configuration ─────────────────────────────────────────────────────────
// Railway sets ConnectionStrings__MongoDb, which ASP.NET's env var provider
// maps to config key "ConnectionStrings:MongoDb" — read it via GetConnectionString
// rather than a made-up key/env var name that nothing actually sets.
var mongoConnectionString = builder.Configuration.GetConnectionString("MongoDb")
    ?? Environment.GetEnvironmentVariable("MONGO_CONNECTION_STRING")
    ?? "mongodb://localhost:27017";
var mongoDatabase = builder.Configuration["MongoDb:Database"]
    ?? Environment.GetEnvironmentVariable("MONGO_DATABASE")
    ?? "truvoid";

// ── MongoDB ───────────────────────────────────────────────────────────────
builder.Services.AddSingleton<IMongoClient>(new MongoClient(mongoConnectionString));
builder.Services.AddSingleton<MongoDbContext>(sp =>
{
    var client = sp.GetRequiredService<IMongoClient>();
    return new MongoDbContext(client, mongoDatabase);
});

// ── JWT auth ──────────────────────────────────────────────────────────────
// Railway sets Jwt__SecretKey / Jwt__Issuer / Jwt__Audience (maps to Jwt:SecretKey
// etc. via the env var provider) — must read the same keys AuthEndpoints uses to
// sign tokens, or issued tokens fail validation here.
var jwtSecret = builder.Configuration["Jwt:SecretKey"]
    ?? Environment.GetEnvironmentVariable("JWT_SECRET")
    ?? "dev-secret-key-change-in-production-32chars!!!";
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
              .RequireRole("Admin", "SuperAdmin", "PlatformAdmin"));
});

// ── HTTP clients for upstream services ────────────────────────────────────
builder.Services.AddHttpClient("idaccess", client =>
{
    client.BaseAddress = new Uri("https://idaccess.info/v1/");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});
var resendApiKey = Environment.GetEnvironmentVariable("Resend_API_Key")
    ?? Environment.GetEnvironmentVariable("RESEND_API_KEY");
builder.Services.AddHttpClient("resend", client =>
{
    client.BaseAddress = new Uri("https://api.resend.com/");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    if (!string.IsNullOrEmpty(resendApiKey))
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {resendApiKey}");
});

// ── Application services ──────────────────────────────────────────────────
builder.Services.AddScoped<IPricingService, PricingService>();
builder.Services.AddScoped<INimcConfigService, NimcConfigService>();
builder.Services.AddScoped<NotificationFeedService>();
builder.Services.AddScoped<NotificationPreferenceService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IWalletService, WalletService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IVerificationService, VerificationService>();

// Mailer: swap ResendEmailService for a no-op dev implementation if you
// set NOTIFICATIONS_DISABLED=1 so local dev doesn't blow up on missing API keys.
if (Environment.GetEnvironmentVariable("NOTIFICATIONS_DISABLED") == "1")
{
    builder.Services.AddScoped<IEmailService, DevNullEmailService>();
}
else
{
    builder.Services.AddScoped<IEmailService, ResendEmailService>();
}

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
app.UseAuthentication();
app.UseAuthorization();

app.MapTruvoIdEndpoints();

app.Run();

// ── Dev-only no-op mailer ─────────────────────────────────────────────────
public class DevNullEmailService : IEmailService
{
    public Task SendAsync(string toEmail, string toName, string subject, string htmlBody)
    {
        Console.WriteLine($"[DEV NULL EMAIL] Would send to {toEmail}: {subject}");
        return Task.CompletedTask;
    }
}
