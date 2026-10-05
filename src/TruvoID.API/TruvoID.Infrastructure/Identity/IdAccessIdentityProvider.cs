using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TruvoID.Infrastructure.Identity;

/// <summary>
/// IDAccess (idaccess.info) client. Response shape, as returned by the API:
/// { success, data: { verdict: "MATCH" | ..., data: { first_name, ..., photograph } }, error: { message } }
/// Never logs the subject number or the identity payload — only outcome and status codes.
/// </summary>
public sealed class IdAccessIdentityProvider(
    IHttpClientFactory clients,
    string? apiKey,
    ILogger<IdAccessIdentityProvider> logger) : IIdentityProvider
{
    public const string HttpClientName = "idaccess";

    public string Environment => "live";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(apiKey);

    public async Task<IdentityResult> VerifyAsync(string type, string subject, string idempotencyKey, CancellationToken ct)
    {
        if (!IsConfigured)
            return IdentityResult.Error("Identity provider is not configured.");

        var (path, field) = type switch
        {
            "nin" => ("identity/nin/advance", "nin"),
            "bvn" => ("identity/bvn/advance", "bvn"),
            "phone" => ("identity/phone/basic", "phone_number"),
            _ => throw new ArgumentException($"Unsupported verification type '{type}'."),
        };

        // Per-request headers: the factory's clients are shared, so DefaultRequestHeaders must not be mutated.
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new Dictionary<string, string> { [field] = subject }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey!.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        HttpResponseMessage response;
        string body;
        try
        {
            response = await clients.CreateClient(HttpClientName).SendAsync(request, ct);
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("IDAccess {Type} call failed before a response: {Error}", type, ex.GetType().Name);
            return IdentityResult.Error("The identity provider did not respond. You have not been charged.");
        }

        using (response)
        {
            JsonElement root;
            try { root = JsonDocument.Parse(body).RootElement.Clone(); }
            catch (JsonException)
            {
                logger.LogWarning("IDAccess {Type} returned non-JSON (HTTP {Status})", type, (int)response.StatusCode);
                return IdentityResult.Error("The identity provider returned an unreadable response. You have not been charged.");
            }

            var success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
            if (response.IsSuccessStatusCode && success && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                var verdict = Str(data, "verdict");
                var identity = data.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : data;
                var outcome = string.Equals(verdict, "MATCH", StringComparison.OrdinalIgnoreCase) ? IdentityOutcome.Match : IdentityOutcome.NoMatch;
                logger.LogInformation("IDAccess {Type} completed: {Outcome}", type, outcome);
                return new IdentityResult(outcome, outcome == IdentityOutcome.Match ? ToRecord(identity) : null,
                    outcome == IdentityOutcome.Match ? null : "No record matches this number.");
            }

            var message = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object ? Str(error, "message") : null;
            // An explicit "no match"/"not found" is a completed (billable) lookup. Anything
            // ambiguous is treated as a provider error and refunded — never charge when unsure.
            if (message?.Contains("no match", StringComparison.OrdinalIgnoreCase) == true
                || message?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true)
            {
                logger.LogInformation("IDAccess {Type} completed: NoMatch (HTTP {Status})", type, (int)response.StatusCode);
                return new IdentityResult(IdentityOutcome.NoMatch, null, "No record matches this number.");
            }

            logger.LogWarning("IDAccess {Type} failed: HTTP {Status}", type, (int)response.StatusCode);
            return IdentityResult.Error("The identity provider could not complete this check. You have not been charged.");
        }
    }

    private static IdentityRecord ToRecord(JsonElement d)
    {
        var first = Str(d, "first_name");
        var middle = Str(d, "middle_name");
        var last = Str(d, "last_name");
        var full = string.Join(' ', new[] { first, middle, last }.Where(p => !string.IsNullOrWhiteSpace(p)));
        var photo = Str(d, "photograph") ?? Str(d, "photo") ?? Str(d, "image");
        return new IdentityRecord(
            string.IsNullOrWhiteSpace(full) ? null : full,
            first, middle, last,
            Str(d, "date_of_birth") ?? Str(d, "dob"),
            FormatGender(Str(d, "gender")),
            Str(d, "phone_number") ?? Str(d, "phone"),
            Str(d, "state_of_origin"),
            Str(d, "residential_address"),
            string.IsNullOrWhiteSpace(photo) ? null : photo.StartsWith("data:") ? photo : $"data:image/jpeg;base64,{photo}");
    }

    private static string? FormatGender(string? g) => g?.Trim().ToLowerInvariant() switch
    {
        "m" or "male" => "Male",
        "f" or "female" => "Female",
        null or "" => null,
        var other => other,
    };

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
