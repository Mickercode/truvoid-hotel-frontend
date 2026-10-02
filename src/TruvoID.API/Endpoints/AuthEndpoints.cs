using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using TruvoID.Core.Interfaces;
using TruvoID.Domain.Enums;
using TruvoID.Infrastructure.Postgres;

namespace TruvoID.API.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var authGroup = app.MapGroup("/v1/auth");

        authGroup.MapPost("/register", Register)
            .AllowAnonymous();

        authGroup.MapPost("/login", Login)
            .AllowAnonymous();

        authGroup.MapPost("/refresh", RefreshToken)
            .AllowAnonymous();

        authGroup.MapGet("/me", GetCurrentUser)
            .RequireAuthorization();

        authGroup.MapPost("/change-password", ChangePassword)
            .RequireAuthorization();

        authGroup.MapPost("/deactivate", DeactivateAccount)
            .RequireAuthorization();

        // JWTs are stateless here (no server-side session/token blocklist), so
        // there's nothing to actually invalidate — this exists so the frontend's
        // logout call has something to hit instead of a 404.
        authGroup.MapPost("/logout", () => Results.Ok(new { message = "Logged out." }))
            .RequireAuthorization();

        return app;
    }

    private static async Task<IResult> ChangePassword(
        HttpContext ctx,
        ChangePasswordRequest request,
        ControlPlaneIdentityStore identities)
    {
        var userId = ctx.GetUserId();
        if (userId == Guid.Empty) return Results.Unauthorized();

        if (AuthValidation.ValidatePassword(request.NewPassword) is { } passwordError)
            return Results.BadRequest(new { error = passwordError });

        var user = await identities.FindByIdAsync(userId);
        if (user is null) return Results.Unauthorized();

        if (!PasswordHasher.Verify(request.CurrentPassword, user.CredentialHash))
            return Results.BadRequest(new { error = "Current password is incorrect." });

        await identities.UpdatePasswordAsync(userId, PasswordHasher.Hash(request.NewPassword));

        return Results.Ok(new { message = "Password changed successfully." });
    }

    private static async Task<IResult> DeactivateAccount(
        HttpContext ctx,
        ControlPlaneIdentityStore identities)
    {
        var organizationId = ctx.GetOrganizationId();
        if (organizationId == Guid.Empty) return Results.Unauthorized();
        if (!await identities.DeactivateOrganizationAsync(organizationId))
            return Results.NotFound(new { error = "Organization not found." });

        return Results.Ok(new { message = "Account deactivated." });
    }

    private static async Task<IResult> Register(
        RegisterRequest request,
        ControlPlaneIdentityStore identities)
    {
        if (AuthValidation.ValidateRegistration(request.InstitutionName, request.AdminFullName, request.AdminEmail, request.Password) is { } validationError)
            return Results.BadRequest(new { error = validationError });

        try
        {
            var organizationType = request.Type.Trim().ToLowerInvariant() switch
            {
                "institution" => OrganizationType.Institution,
                _ => throw new ArgumentException("Public registration is for institutions only. Agencies are invited by platform administrators.", nameof(request.Type))
            };
            var registered = await identities.RegisterOrganizationAsync(
                request.InstitutionName,
                organizationType,
                request.AdminFullName,
                request.AdminEmail,
                request.Password);

            var (accessToken, refreshToken, expiresAt) = GenerateTokens(
                registered.OrganizationId,
                registered.UserId,
                "Admin",
                tenantRole: organizationType == OrganizationType.Agency ? "agency_admin" : "institution_admin");

            return Results.Ok(new RegisterResponse
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ExpiresAt = expiresAt
            });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Results.Conflict(new { error = "An account with this email or institution name already exists." });
        }
    }

    private static async Task<IResult> Login(
        LoginRequest request,
        ControlPlaneIdentityStore identities,
        IAuditService audit)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
            return Results.BadRequest(new { error = "Enter your email address and password." });

        var user = await identities.FindByEmailAsync(request.Email);

        // Same message for unknown email and wrong password, so login can't be used to probe accounts.
        if (user is null || !PasswordHasher.Verify(request.Password, user.CredentialHash))
            return Results.Json(new { error = "Incorrect email or password." }, statusCode: StatusCodes.Status401Unauthorized);

        if (user.Status != "active")
            return Results.Json(new { error = user.Status == "invited"
                    ? "This account hasn't been activated yet. Use the link in your invitation email."
                    : "This account has been disabled. Contact your organization administrator." },
                statusCode: StatusCodes.Status403Forbidden);

        if (user.OrganizationStatus is "suspended" or "closed")
            return Results.Json(new { error = "Your organization's access has been suspended. Contact TruvoID support." },
                statusCode: StatusCodes.Status403Forbidden);

        await identities.MarkLoginAsync(user.Id);
        await audit.LogAsync(AuditAction.Login, "User", user.Id, user.Id, "User");

        var (accessToken, refreshToken, expiresAt) = GenerateTokens(
            user.OrganizationId ?? Guid.Empty,
            user.Id,
            ToLegacyClaimRole(user.Role),
            user.OutletId,
            user.Role);

        return Results.Ok(new LoginResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = expiresAt
        });
    }

    private static async Task<IResult> RefreshToken(
        RefreshTokenRequest request,
        ControlPlaneIdentityStore identities)
    {
        // Validate the old access token and issue new tokens.
        // institutionId == Guid.Empty is valid for platform-level accounts — only a
        // missing/invalid userId means the token itself didn't parse.
        var (userId, _, _) = GetClaimsFromToken(request.OldAccessToken);

        if (userId == Guid.Empty)
            return Results.Unauthorized();

        var user = await identities.FindByIdAsync(userId);
        if (user is null)
            return Results.Unauthorized();

        if (user.Status != "active")
            return Results.Forbid();

        var (accessToken, refreshToken, expiresAt) = GenerateTokens(
            user.OrganizationId ?? Guid.Empty,
            userId,
            ToLegacyClaimRole(user.Role),
            user.OutletId,
            user.Role);

        return Results.Ok(new LoginResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = expiresAt
        });
    }

    private static async Task<IResult> GetCurrentUser(
        HttpContext ctx,
        ControlPlaneIdentityStore identities)
    {
        // Extract claims from the JWT in the Authorization header
        var authHeader = ctx.Request.Headers.Authorization.ToString();
        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Results.Unauthorized();

        var token = authHeader["Bearer ".Length..].Trim();
        var (userId, institutionId, _) = GetClaimsFromToken(token);

        if (userId == Guid.Empty)
            return Results.Unauthorized();

        var user = await identities.FindByIdAsync(userId);
        if (user is null)
            return Results.Unauthorized();

        return Results.Ok(new AuthProfileResponse
        {
            UserId = user.Id.ToString(),
            InstitutionId = (user.OrganizationId ?? Guid.Empty).ToString(),
            Email = user.Email,
            FullName = user.FullName ?? string.Empty,
            Role = ToLegacyClaimRole(user.Role),
            InstitutionName = user.OrganizationName,
            OutletId = user.OutletId?.ToString(),
            OrganizationStatus = user.OrganizationStatus
        });
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    internal static string HashPassword(string password)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ToLegacyClaimRole(string role) => role switch
    {
        "platform_admin" => "PlatformAdmin",
        "institution_admin" or "agency_admin" => "Admin",
        "institution_staff" or "agency_user" or "outlet_owner" or "outlet_staff" => "Staff",
        _ => role
    };

    // Railway sets Jwt__SecretKey / Jwt__Issuer / Jwt__Audience / Jwt__ExpiryMinutes
    // (JWT_SECRET is kept as a fallback name). Must match what Program.cs reads for
    // token validation, or tokens issued here never validate.
    private static string JwtSecret =>
        Environment.GetEnvironmentVariable("Jwt__SecretKey")
        ?? Environment.GetEnvironmentVariable("JWT_SECRET")
        ?? "dev-secret-key-change-in-production-32chars!!!";

    private static string JwtIssuer => Environment.GetEnvironmentVariable("Jwt__Issuer") ?? "TruvoID";
    private static string JwtAudience => Environment.GetEnvironmentVariable("Jwt__Audience") ?? "TruvoID";
    private static int JwtExpiryMinutes =>
        int.TryParse(Environment.GetEnvironmentVariable("Jwt__ExpiryMinutes"), out var m) ? m : 60;

    private static (string access, string refresh, DateTime expires) GenerateTokens(
        Guid organizationId,
        Guid userId,
        string role,
        Guid? outletId = null,
        string? tenantRole = null)
    {
        var now = DateTime.UtcNow;
        var expiresAt = now.AddMinutes(JwtExpiryMinutes);

        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSecret));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim("institution_id", organizationId.ToString()),
            new Claim("organization_id", organizationId.ToString()),
            new Claim("role", role),
            new Claim("tenant_role", tenantRole ?? role),
            new Claim(ClaimTypes.Role, role),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(JwtRegisteredClaimNames.Iat, new DateTimeOffset(now).ToUnixTimeSeconds().ToString() /* now is UTC; pairing it with the local offset throws off-UTC hosts */, ClaimValueTypes.Integer64)
        };

        if (outletId.HasValue)
            claims = claims.Append(new Claim("outlet_id", outletId.Value.ToString())).ToArray();
        if (!string.IsNullOrWhiteSpace(tenantRole) && tenantRole != role)
            claims = claims.Append(new Claim(ClaimTypes.Role, tenantRole)).ToArray();

        var token = new JwtSecurityToken(
            issuer: JwtIssuer,
            audience: JwtAudience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: credentials
        );

        var accessToken = new JwtSecurityTokenHandler().WriteToken(token);

        // Refresh token: cryptographically random, stored server-side in production
        var refreshTokenBytes = new byte[32];
        RandomNumberGenerator.Fill(refreshTokenBytes);
        var refreshToken = Convert.ToBase64String(refreshTokenBytes);

        return (accessToken, refreshToken, expiresAt);
    }

    private static (Guid userId, Guid institutionId, string role) GetClaimsFromToken(string token)
    {
        try
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSecret));
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ValidateIssuer = true,
                ValidIssuer = JwtIssuer,
                ValidateAudience = true,
                ValidAudience = JwtAudience,
                ClockSkew = TimeSpan.Zero
            };

            _ = tokenHandler.ValidateToken(token, validationParameters, out var validatedToken);
            var jwtToken = (JwtSecurityToken)validatedToken;

            // Read from the raw token claims, not the ClaimsPrincipal — JwtSecurityTokenHandler
            // silently remaps short claim names like "sub" to long URIs (e.g. ClaimTypes.NameIdentifier)
            // by default, so principal.FindFirst("sub") returns null even though the token has it.
            var userIdClaim = jwtToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sub)?.Value;
            var userId = Guid.TryParse(userIdClaim, out var uid) ? uid : Guid.Empty;

            var institutionIdClaim = jwtToken.Claims.FirstOrDefault(c => c.Type == "institution_id")?.Value;
            var institutionId = Guid.TryParse(institutionIdClaim, out var iid) ? iid : Guid.Empty;

            var role = jwtToken.Claims.FirstOrDefault(c => c.Type == "role")?.Value ?? "Admin";

            return (userId, institutionId, role);
        }
        catch { }
        return (Guid.Empty, Guid.Empty, "");
    }

    // ── request/response models ────────────────────────────────────────────────

    public record RegisterRequest
    {
        public string InstitutionName { get; init; } = "";
        public string ContactEmail { get; init; } = "";
        public string ContactPhone { get; init; } = "";
        public string AdminFullName { get; init; } = "";
        public string AdminEmail { get; init; } = "";
        public string AdminPhone { get; init; } = "";
        public string Password { get; init; } = "";
        public string Type { get; init; } = "institution";
    }

    public record LoginRequest
    {
        public string Email { get; init; } = "";
        public string Password { get; init; } = "";
    }

    public record RefreshTokenRequest
    {
        public string OldAccessToken { get; init; } = "";
        public string RefreshToken { get; init; } = "";
    }

    public record ChangePasswordRequest
    {
        public string CurrentPassword { get; init; } = "";
        public string NewPassword { get; init; } = "";
    }
}

// ── shared DTOs (mirrored from Components/Services/FrontendModels.cs) ─────────

public record RegisterResponse
{
    public string AccessToken { get; init; } = "";
    public string RefreshToken { get; init; } = "";
    public DateTime ExpiresAt { get; init; }
}

public record LoginResponse
{
    public string AccessToken { get; init; } = "";
    public string RefreshToken { get; init; } = "";
    public DateTime ExpiresAt { get; init; }
}

public record AuthProfileResponse
{
    public string UserId { get; init; } = "";
    public string InstitutionId { get; init; } = "";
    public string Email { get; init; } = "";
    public string? FullName { get; init; }
    public string Role { get; init; } = "";
    public string InstitutionName { get; init; } = "";
    public string? OutletId { get; init; }
    /// <summary>pending | active | suspended | closed; null for platform admins.</summary>
    public string? OrganizationStatus { get; init; }
}
