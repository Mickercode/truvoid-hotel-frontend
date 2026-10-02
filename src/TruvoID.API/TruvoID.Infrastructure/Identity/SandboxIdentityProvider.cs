namespace TruvoID.Infrastructure.Identity;

/// <summary>
/// Deterministic stand-in for IDAccess, used when Verification:Provider = "sandbox".
/// Never makes a network call. Test numbers (documented for integrators):
///
///   NIN   00000000001 match · 00000000002 no match · 00000000003 provider error
///   BVN   22222222221 match · 22222222222 no match · 22222222223 provider error
///   Phone 08000000001 match · 08000000002 no match · 08000000003 provider error
///
/// Any other well-formed number returns "no match", so a real NIN pasted into the
/// sandbox never appears to verify.
/// </summary>
public sealed class SandboxIdentityProvider(TimeSpan? latency = null) : IIdentityProvider
{
    public string Environment => "sandbox";
    public bool IsConfigured => true;

    public static readonly IReadOnlyDictionary<string, (string Match, string NoMatch, string Error)> TestNumbers =
        new Dictionary<string, (string, string, string)>
        {
            ["nin"] = ("00000000001", "00000000002", "00000000003"),
            ["bvn"] = ("22222222221", "22222222222", "22222222223"),
            ["phone"] = ("08000000001", "08000000002", "08000000003"),
        };

    // A neutral silhouette, so UIs can exercise their photo rendering.
    private const string Photo =
        "data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCAxMjAgMTUwIj48cmVjdCB3aWR0aD0iMTIwIiBoZWlnaHQ9IjE1MCIgZmlsbD0iIzFhMjMyYyIvPjxjaXJjbGUgY3g9IjYwIiBjeT0iNTgiIHI9IjI2IiBmaWxsPSIjNjFlN2RkIiBmaWxsLW9wYWNpdHk9Ii41Ii8+PHBhdGggZD0iTTE4IDE1MGM0LTM2IDIwLTUyIDQyLTUyczM4IDE2IDQyIDUyeiIgZmlsbD0iIzYxZTdkZCIgZmlsbC1vcGFjaXR5PSIuNSIvPjwvc3ZnPg==";

    public async Task<IdentityResult> VerifyAsync(string type, string subject, string idempotencyKey, CancellationToken ct)
    {
        // Realistic, slightly variable latency so client timeouts and spinners get exercised.
        await Task.Delay(latency ?? TimeSpan.FromMilliseconds(600 + Random.Shared.Next(600)), ct);

        if (!TestNumbers.TryGetValue(type, out var numbers))
            throw new ArgumentException($"Unsupported verification type '{type}'.");

        if (subject == numbers.Error)
            return IdentityResult.Error("Sandbox: simulated provider outage. You have not been charged.");
        if (subject != numbers.Match)
            return new IdentityResult(IdentityOutcome.NoMatch, null, "No record matches this number.");

        return new IdentityResult(IdentityOutcome.Match, new IdentityRecord(
            FullName: "ADAEZE CHIOMA OKAFOR",
            FirstName: "ADAEZE",
            MiddleName: "CHIOMA",
            LastName: "OKAFOR",
            DateOfBirth: "14-02-1992",
            Gender: "Female",
            Phone: type == "phone" ? subject : "08000000001",
            StateOfOrigin: "Anambra",
            ResidentialAddress: "12 SANDBOX CLOSE, VICTORIA ISLAND, LAGOS",
            Photo: Photo), null);
    }
}
