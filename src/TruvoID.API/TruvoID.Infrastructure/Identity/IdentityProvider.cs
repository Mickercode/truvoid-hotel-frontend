using System.Text.RegularExpressions;

namespace TruvoID.Infrastructure.Identity;

public enum IdentityOutcome
{
    /// <summary>The record exists and was returned. Billable.</summary>
    Match,
    /// <summary>The provider answered, but no record matches. Billable — the lookup was performed.</summary>
    NoMatch,
    /// <summary>The provider failed (timeout, 5xx, bad payload). Not billable — the charge is refunded.</summary>
    ProviderError,
}

public sealed record IdentityRecord(
    string? FullName,
    string? FirstName,
    string? MiddleName,
    string? LastName,
    string? DateOfBirth,
    string? Gender,
    string? Phone,
    string? StateOfOrigin,
    string? ResidentialAddress,
    /// <summary>data: URL, returned to the caller but never persisted.</summary>
    string? Photo);

public sealed record IdentityResult(IdentityOutcome Outcome, IdentityRecord? Identity, string? Message)
{
    public static IdentityResult Error(string message) => new(IdentityOutcome.ProviderError, null, message);
}

/// <summary>
/// The one place identity lookups leave TruvoID. Production uses IDAccess; the
/// sandbox environment swaps in <see cref="SandboxIdentityProvider"/> so integrators
/// can test every outcome without real NINs or real charges upstream.
/// </summary>
public interface IIdentityProvider
{
    /// <summary>"live" or "sandbox" — echoed in every API response.</summary>
    string Environment { get; }

    /// <summary>False when required configuration (e.g. an API key) is missing; nothing is charged.</summary>
    bool IsConfigured { get; }

    /// <param name="subject">Already normalized by <see cref="IdentitySubject.Normalize"/>.</param>
    Task<IdentityResult> VerifyAsync(string type, string subject, string idempotencyKey, CancellationToken ct);
}

public static partial class IdentitySubject
{
    public static readonly IReadOnlyList<string> SupportedTypes = ["nin", "bvn", "phone"];

    /// <summary>
    /// Validates and canonicalizes the number before anything is charged.
    /// Phones become 11-digit local format (0803…), accepting +234/234 prefixes.
    /// </summary>
    public static string Normalize(string type, string? raw)
    {
        var value = Regex.Replace(raw ?? "", @"[\s\-()]", "");
        switch (type)
        {
            case "nin":
            case "bvn":
                if (!ElevenDigits().IsMatch(value))
                    throw new ArgumentException($"A {type.ToUpperInvariant()} must be exactly 11 digits.");
                return value;
            case "phone":
                if (value.StartsWith("+234")) value = "0" + value[4..];
                else if (value.StartsWith("234") && value.Length == 13) value = "0" + value[3..];
                if (!NigerianMobile().IsMatch(value))
                    throw new ArgumentException("Enter a Nigerian mobile number, e.g. 08031234567 or +2348031234567.");
                return value;
            default:
                throw new ArgumentException($"Unsupported verification type '{type}'. Use one of: {string.Join(", ", SupportedTypes)}.");
        }
    }

    [GeneratedRegex(@"^\d{11}$")]
    private static partial Regex ElevenDigits();

    [GeneratedRegex(@"^0[789][01]\d{8}$")]
    private static partial Regex NigerianMobile();
}
