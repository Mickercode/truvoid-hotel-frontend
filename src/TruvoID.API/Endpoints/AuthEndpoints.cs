using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using TruvoID.API.Auth;
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
            .AllowAnonymous().RequireRateLimiting("auth");

        authGroup.MapPost("/login", (LoginRequest request, ControlPlaneIdentityStore identities, RefreshTokenStore refreshTokens, IAuditService audit, JwtSettings jwt) =>
                Login(request, identities, refreshTokens, audit, jwt, platformAdmin: false))
            .AllowAnonymous().RequireRateLimiting("auth");

        // Platform staff sign in separately (dashboard route /admin/login); workspace users can't use it.
        app.MapPost("/v1/admin/auth/login", (LoginRequest request, ControlPlaneIdentityStore identities, RefreshTokenStore refreshTokens, IAuditService audit, JwtSettings jwt) =>
                Login(request, identities, refreshTokens, audit, jwt, platformAdmin: true))
            .AllowAnonymous().RequireRateLimiting("auth");

        authGroup.MapPost("/refresh", RefreshToken)
            .AllowAnonymous().RequireRateLimiting("auth");

        authGroup.MapGet("/me", GetCurrentUser)
            .RequireAuthorization();

        authGroup.MapPost("/change-password", ChangePassword)
            .RequireAuthorization().RequireRateLimiting("auth");

        authGroup.MapPost("/deactivate", DeactivateAccount)
            .RequireAuthorization();

        // Revokes the session's refresh-token family. Anonymous on purpose: holding the
        // refresh token is the proof, and an expired access token must not block sign-out.
        authGroup.MapPost("/logout", async (LogoutRequest? request, RefreshTokenStore refreshTokens, CancellationToken ct) =>
            {
                if (!string.IsNullOrWhiteSpace(request?.RefreshToken))
                    await refreshTokens.RevokeFamilyOfAsync(request.RefreshToken, ct);
                return Results.Ok(new { message = "Logged out." });
            })
            .AllowAnonymous().RequireRateLimiting("auth");

        return app;
    }

    private static async Task<IResult> ChangePassword(
        HttpContext ctx,
        ChangePasswordRequest request,
        ControlPlaneIdentityStore identities,
        RefreshTokenStore refreshTokens,
        JwtSettings jwt)
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

        // Sign out every other device; this one gets a fresh session.
        await refreshTokens.RevokeAllForUserAsync(userId);
        var (accessToken, expiresAt) = GenerateAccessToken(jwt,
            user.OrganizationId ?? Guid.Empty, user.Id, ToLegacyClaimRole(user.Role), user.OutletId, user.Role);
        return Results.Ok(new
        {
            message = "Password changed. You have been signed out on other devices.",
            accessToken,
            refreshToken = await refreshTokens.IssueAsync(user.Id),
            expiresAt,
        });
    }

    /// <summary>
    /// Shuts down the whole organization. Organization administrators only, confirmed with
    /// their password — previously any signed-in member (even outlet staff) could do this.
    /// </summary>
    private static async Task<IResult> DeactivateAccount(
        HttpContext ctx,
        DeactivateRequest? request,
        ControlPlaneIdentityStore identities,
        RefreshTokenStore refreshTokens,
        IAuditService audit)
    {
        if (!ctx.IsOrganizationAdmin())
            return Results.Json(new { error = "Only your organization's administrator can deactivate it." },
                statusCode: StatusCodes.Status403Forbidden);
        var user = await identities.FindByIdAsync(ctx.GetUserId());
        if (user is null || !PasswordHasher.Verify(request?.Password ?? "", user.CredentialHash))
            return Results.BadRequest(new { error = "Confirm with your current password to deactivate the organization." });

        var organizationId = ctx.GetOrganizationId();
        var memberIds = await identities.DeactivateOrganizationAsync(organizationId);
        if (memberIds is null)
            return Results.NotFound(new { error = "Organization not found." });
        foreach (var memberId in memberIds)
            await refreshTokens.RevokeAllForUserAsync(memberId);
        await audit.LogAsync(AuditAction.Updated, "Organization", organizationId, user.Id, "User", "deactivated");
        return Results.Ok(new { message = "Organization deactivated. Everyone has been signed out." });
    }

    private static async Task<IResult> Register(
        RegisterRequest request,
        ControlPlaneIdentityStore identities,
        RefreshTokenStore refreshTokens,
        JwtSettings jwt)
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

            var (accessToken, expiresAt) = GenerateAccessToken(jwt,
                registered.OrganizationId,
                registered.UserId,
                "Admin",
                tenantRole: organizationType == OrganizationType.Agency ? "agency_admin" : "institution_admin");

            return Results.Ok(new RegisterResponse
            {
                AccessToken = accessToken,
                RefreshToken = await refreshTokens.IssueAsync(registered.UserId),
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
        RefreshTokenStore refreshTokens,
        IAuditService audit,
        JwtSettings jwt,
        bool platformAdmin)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
            return Results.BadRequest(new { error = "Enter your email address and password." });

        var user = await identities.FindByEmailAsync(request.Email);

        // Always run the password hash — even for an unknown email — so the response
        // time doesn't reveal whether an account exists. Same message either way.
        var passwordOk = PasswordHasher.Verify(request.Password, user?.CredentialHash ?? DummyPasswordHash);
        if (user is null || !passwordOk)
        {
            if (user is not null)
                await identities.RecordLoginFailureAsync(user.Id);
            return Results.Json(new { error = "Incorrect email or password." }, statusCode: StatusCodes.Status401Unauthorized);
        }

        // Locked accounts are refused only after the password is confirmed, so the lock
        // state isn't disclosed to someone who doesn't already know the password.
        if (user.LockedUntil is { } lockedUntil && lockedUntil > DateTime.UtcNow)
            return Results.Json(new
            {
                error = "Too many failed attempts. This account is temporarily locked — try again in a few minutes or reset your password.",
                code = "account_locked",
            }, statusCode: StatusCodes.Status429TooManyRequests);

        // Each sign-in serves exactly one kind of account, and a mismatch answers exactly like a
        // wrong password: neither page confirms an account exists or points to the other page.
        if ((user.Role == "platform_admin") != platformAdmin)
            return Results.Json(new { error = "Incorrect email or password." }, statusCode: StatusCodes.Status401Unauthorized);

        if (user.Status != "active")
            return Results.Json(new { error = user.Status == "invited"
                    ? "This account hasn't been activated yet. Use the link in your invitation email."
                    : "This account has been disabled. Contact your organization administrator." },
                statusCode: StatusCodes.Status403Forbidden);

        if (user.OrganizationStatus is "suspended" or "closed")
            return Results.Json(new { error = "Your organization's access has been suspended. Contact TruvoID support." },
                statusCode: StatusCodes.Status403Forbidden);

        await identities.RecordLoginSuccessAsync(user.Id);
        await audit.LogAsync(AuditAction.Login, "User", user.Id, user.Id, "User");

        var (accessToken, expiresAt) = GenerateAccessToken(jwt,
            user.OrganizationId ?? Guid.Empty,
            user.Id,
            ToLegacyClaimRole(user.Role),
            user.OutletId,
            user.Role);

        return Results.Ok(new LoginResponse
        {
            AccessToken = accessToken,
            RefreshToken = await refreshTokens.IssueAsync(user.Id),
            ExpiresAt = expiresAt
        });
    }

    private static async Task<IResult> RefreshToken(
        RefreshTokenRequest request,
        ControlPlaneIdentityStore identities,
        RefreshTokenStore refreshTokens,
        JwtSettings jwt,
        ILoggerFactory loggerFactory)
    {
        var rotation = await refreshTokens.RotateAsync(request.RefreshToken);
        if (rotation.Outcome == RefreshOutcome.Reused)
            loggerFactory.CreateLogger("Auth").LogWarning(
                "Refresh token reuse for user {UserId}; every session from that sign-in was revoked.", rotation.UserId);
        if (rotation.Outcome != RefreshOutcome.Rotated)
            return Results.Json(new { error = "Your session has expired. Please sign in again.", code = "session_expired" },
                statusCode: StatusCodes.Status401Unauthorized);

        var user = await identities.FindByIdAsync(rotation.UserId);
        if (user is null || user.Status != "active" || user.OrganizationStatus is "suspended" or "closed")
        {
            await refreshTokens.RevokeAllForUserAsync(rotation.UserId);
            return Results.Json(new { error = "This account can no longer sign in. Contact your administrator.", code = "account_inactive" },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        // Role and organization are re-read on every refresh, so changes apply within one access-token lifetime.
        var (accessToken, expiresAt) = GenerateAccessToken(jwt,
            user.OrganizationId ?? Guid.Empty, user.Id, ToLegacyClaimRole(user.Role), user.OutletId, user.Role);

        return Results.Ok(new LoginResponse
        {
            AccessToken = accessToken,
            RefreshToken = rotation.NewToken!,
            ExpiresAt = expiresAt
        });
    }

    private static async Task<IResult> GetCurrentUser(
        HttpContext ctx,
        ControlPlaneIdentityStore identities,
        OrganizationSetupStore setup,
        JwtSettings jwt)
    {
        // Never let a proxy/CDN serve a stale profile (setup status / liveEnabled).
        ctx.Response.Headers.CacheControl = "no-store";
        // Extract claims from the JWT in the Authorization header
        var authHeader = ctx.Request.Headers.Authorization.ToString();
        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Results.Unauthorized();

        var token = authHeader["Bearer ".Length..].Trim();
        var (userId, institutionId, _) = GetClaimsFromToken(jwt, token);

        if (userId == Guid.Empty)
            return Results.Unauthorized();

        var user = await identities.FindByIdAsync(userId);
        if (user is null)
            return Results.Unauthorized();

        string? setupStatus = null;
        var liveEnabled = false;
        if (user.OrganizationId is { } organizationId && user.OrganizationStatus == "active")
        {
            setupStatus = (await setup.GetAsync(organizationId)).Status;
            liveEnabled = await setup.IsLiveEnabledAsync(organizationId);
        }

        return Results.Ok(new AuthProfileResponse
        {
            SetupStatus = setupStatus,
            LiveEnabled = liveEnabled,
            TenantRole = user.Role,
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

    // A real PBKDF2 hash, verified against unknown emails so login timing is the same
    // whether or not the account exists (prevents user enumeration by response time).
    private static readonly string DummyPasswordHash = PasswordHasher.Hash("timing-equalization-only");

    private static string ToLegacyClaimRole(string role) => role switch
    {
        "platform_admin" => "PlatformAdmin",
        "institution_admin" or "agency_admin" => "Admin",
        "institution_staff" or "agency_user" or "outlet_owner" or "outlet_staff" => "Staff",
        _ => role
    };

    private static (string access, DateTime expires) GenerateAccessToken(
        JwtSettings jwt,
        Guid organizationId,
        Guid userId,
        string role,
        Guid? outletId = null,
        string? tenantRole = null)
    {
        var now = DateTime.UtcNow;
        var expiresAt = now.AddMinutes(jwt.ExpiryMinutes);

        var credentials = new SigningCredentials(jwt.SigningKey, SecurityAlgorithms.HmacSha256);

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
            issuer: jwt.Issuer,
            audience: jwt.Audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: credentials
        );

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    private static (Guid userId, Guid institutionId, string role) GetClaimsFromToken(JwtSettings jwt, string token)
    {
        try
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            _ = tokenHandler.ValidateToken(token, jwt.ValidationParameters, out var validatedToken);
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
        /// <summary>Ignored; still accepted so older clients that send it keep working.</summary>
        public string OldAccessToken { get; init; } = "";
        public string RefreshToken { get; init; } = "";
    }

    public record LogoutRequest(string? RefreshToken);

    public record DeactivateRequest(string? Password);

    public record ChangePasswordRequest
    {
        public string CurrentPassword { get; init; } = "";
        public string NewPassword { get; init; } = "";
    }
}

// ── shared DTOs (mirrored by the React client's AuthProfile type) ────────────

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
    /// <summary>incomplete | submitted | needs_changes | approved; null for platform admins.</summary>
    public string? SetupStatus { get; init; }
    /// <summary>False until the organization is approved: only free test-mode verification until then.</summary>
    public bool LiveEnabled { get; init; }
    /// <summary>Precise role (institution_admin, agency_admin, agency_user, outlet_owner, platform_admin, …);
    /// Role above is the coarse legacy claim.</summary>
    public string TenantRole { get; init; } = "";
}
