using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TruvoID.Infrastructure.Postgres;

namespace TruvoID.API.Auth;

/// <summary>
/// Authenticates requests carrying an "X-API-Key" header against control.api_key,
/// as an alternative to a JWT bearer token. On success the resulting
/// principal carries the same "institution_id" claim shape the JWT flow uses, so
/// existing endpoint code (HttpContextExtensions.GetInstitutionId) works unchanged
/// regardless of which scheme actually authenticated the request.
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";
    private const string HeaderName = "X-API-Key";

    private readonly PostgresApiKeyStore _keys;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        PostgresApiKeyStore keys)
        : base(options, logger, encoder)
    {
        _keys = keys;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var headerValues))
            return AuthenticateResult.NoResult();

        var rawKey = headerValues.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(rawKey))
            return AuthenticateResult.NoResult();

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();
        var key = await _keys.FindByHashAsync(hash, Context.RequestAborted);

        if (key is null)
            return AuthenticateResult.Fail("Invalid API key.");
        // control.api_key stores lowercase ('active' | 'revoked'); a case-sensitive
        // "Active" check here once rejected every valid key as revoked.
        if (!string.Equals(key.Status, "active", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.Fail("This API key has been revoked.");

        // Fire-and-forget usage tracking — don't block the request on it.
        _ = _keys.MarkUsedAsync(key.Id);

        var claims = new List<Claim>
        {
            new Claim("institution_id", key.OrganizationId.ToString()),
            new Claim("organization_id", key.OrganizationId.ToString()),
            new Claim("api_key_id", key.Id.ToString()),
            new Claim("key_environment", key.Environment), // "test" or "live": decides the verification mode
            new Claim(ClaimTypes.Role, "ApiKey")
        };
        if (key.OutletId is { } outletId)
            claims.Add(new Claim("outlet_id", outletId.ToString()));
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }
}
