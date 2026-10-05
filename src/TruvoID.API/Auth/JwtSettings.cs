using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace TruvoID.API.Auth;

/// <summary>
/// The single source of truth for JWT signing and validation. Previously the token
/// issuer read the secret from environment variables only (with a hardcoded fallback),
/// while the validation middleware read <c>Jwt:SecretKey</c> from configuration first.
/// If the two ever resolved differently the API would validate with the real key but
/// sign with the public dev key. Resolve once at startup and share it.
/// </summary>
public sealed record JwtSettings(string Secret, string Issuer, string Audience, int ExpiryMinutes)
{
    public const string DevFallbackSecret = "dev-secret-key-change-in-production-32chars!!!";

    public static JwtSettings Resolve(IConfiguration configuration, IHostEnvironment environment)
    {
        // The environment-variable provider already maps Jwt__SecretKey to Jwt:SecretKey,
        // so reading configuration covers appsettings and Railway-style env vars alike.
        // JWT_SECRET is kept as a fallback name for existing deployments.
        var secret = configuration["Jwt:SecretKey"]
            ?? Environment.GetEnvironmentVariable("JWT_SECRET");
        if (string.IsNullOrWhiteSpace(secret))
        {
            if (!environment.IsDevelopment())
                throw new InvalidOperationException(
                    "Jwt:SecretKey (or Jwt__SecretKey / JWT_SECRET) is required outside Development.");
            secret = DevFallbackSecret;
        }

        return new JwtSettings(
            secret,
            configuration["Jwt:Issuer"] ?? "TruvoID",
            configuration["Jwt:Audience"] ?? "TruvoID",
            configuration.GetValue("Jwt:ExpiryMinutes", 60));
    }

    public SymmetricSecurityKey SigningKey => new(Encoding.UTF8.GetBytes(Secret));

    public TokenValidationParameters ValidationParameters => new()
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = SigningKey,
        ValidateIssuer = true,
        ValidIssuer = Issuer,
        ValidateAudience = true,
        ValidAudience = Audience,
        ClockSkew = TimeSpan.Zero
    };
}
