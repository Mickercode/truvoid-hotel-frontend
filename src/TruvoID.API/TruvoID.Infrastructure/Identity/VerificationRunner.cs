using System.Text.Json;
using Microsoft.Extensions.Logging;
using TruvoID.Infrastructure.Postgres;

namespace TruvoID.Infrastructure.Identity;

public sealed record VerificationOutcome(
    Guid CallId,
    string Type,
    string Environment,
    /// <summary>match | no_match | provider_error | pending</summary>
    string Status,
    IdentityRecord? Identity,
    string? Message,
    long AmountKobo,
    bool Refunded,
    long? BalanceAfterKobo,
    DateTime CreatedAt,
    /// <summary>True when this is the stored answer to an earlier request with the same idempotency key.</summary>
    bool Replayed);

public sealed class IdentityProviderUnavailableException()
    : InvalidOperationException("Identity verification is temporarily unavailable. You have not been charged.");

/// <summary>
/// One verification, end to end:
///   1. validate the number (nothing is charged for bad input)
///   2. reserve: debit the wallet and record a pending call — one tenant transaction
///   3. ask the provider — outside any transaction, with a timeout
///   4. complete: store the minimized result, refunding on provider error — second transaction
/// Step 4 ignores client cancellation so a dropped connection can't leave a call charged and pending.
/// </summary>
public sealed class VerificationRunner(
    TenantConnectionFactory tenants,
    TenantVerificationService verifications,
    IIdentityProvider provider,
    ILogger<VerificationRunner> logger)
{
    public static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(30);

    public string Environment => provider.Environment;

    public async Task<VerificationOutcome> RunAsync(
        TenantScope scope,
        string type,
        string? rawSubject,
        Guid? userId,
        Guid? apiKeyId,
        string? idempotencyKey,
        CancellationToken ct)
    {
        type = (type ?? "").Trim().ToLowerInvariant();
        var subject = IdentitySubject.Normalize(type, rawSubject);
        idempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
        if (idempotencyKey is { Length: > 128 })
            throw new ArgumentException("Idempotency-Key must be 128 characters or fewer.");
        if (!provider.IsConfigured)
            throw new IdentityProviderUnavailableException();

        if (idempotencyKey is not null)
        {
            await using var lookup = await tenants.BeginAsync(scope, ct);
            if (await verifications.FindByIdempotencyKeyAsync(lookup, idempotencyKey, userId, apiKeyId, ct) is { } stored)
                return FromStored(stored);
        }

        VerificationReservation reservation;
        await using (var session = await tenants.BeginAsync(scope, ct))
        {
            reservation = await verifications.ReserveAsync(session, type, subject, userId, apiKeyId, idempotencyKey, ct);
            await session.CommitAsync(ct);
        }
        var createdAt = DateTime.UtcNow;

        IdentityResult result;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProviderTimeout);
            result = await provider.VerifyAsync(type, subject, idempotencyKey ?? $"tvd_{reservation.CallId:N}", timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Verification {CallId} ({Type}) provider call failed: {Error}", reservation.CallId, type, ex.GetType().Name);
            result = IdentityResult.Error(ex is OperationCanceledException && !ct.IsCancellationRequested
                ? "The identity provider took too long to respond. You have not been charged."
                : "The identity provider could not complete this check. You have not been charged.");
        }

        var billable = result.Outcome != IdentityOutcome.ProviderError;
        long? refundedBalance;
        await using (var session = await tenants.BeginAsync(scope, CancellationToken.None))
        {
            refundedBalance = await verifications.CompleteAsync(session, reservation.CallId, billable,
                ToStoredJson(result, provider.Environment), result.Message, CancellationToken.None);
            await session.CommitAsync(CancellationToken.None);
        }

        return new VerificationOutcome(
            reservation.CallId, type, provider.Environment, StatusOf(result.Outcome), result.Identity, result.Message,
            reservation.PriceKobo, Refunded: !billable,
            BalanceAfterKobo: refundedBalance ?? reservation.BalanceAfterKobo,
            createdAt, Replayed: false);
    }

    private static string StatusOf(IdentityOutcome outcome) => outcome switch
    {
        IdentityOutcome.Match => "match",
        IdentityOutcome.NoMatch => "no_match",
        _ => "provider_error",
    };

    /// <summary>
    /// What gets persisted. Deliberately excludes the photo and residential address:
    /// the caller receives them once, live; history keeps only what reconciliation needs.
    /// </summary>
    private static string ToStoredJson(IdentityResult result, string environment) => JsonSerializer.Serialize(new
    {
        verdict = StatusOf(result.Outcome),
        environment,
        message = result.Message,
        identity = result.Identity is { } i ? new
        {
            fullName = i.FullName, firstName = i.FirstName, middleName = i.MiddleName, lastName = i.LastName,
            dateOfBirth = i.DateOfBirth, gender = i.Gender, phone = i.Phone, stateOfOrigin = i.StateOfOrigin,
        } : null,
    });

    private VerificationOutcome FromStored(StoredVerification stored)
    {
        if (stored.Status == "pending" || stored.ResultJson is null)
            return new VerificationOutcome(stored.CallId, stored.VerificationType, provider.Environment, "pending", null,
                "This request is still being processed. Retry with the same Idempotency-Key shortly.",
                stored.AmountKobo, false, null, stored.CreatedAt, Replayed: true);

        using var doc = JsonDocument.Parse(stored.ResultJson);
        var root = doc.RootElement;
        string? S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        IdentityRecord? identity = root.TryGetProperty("identity", out var id) && id.ValueKind == JsonValueKind.Object
            ? new IdentityRecord(S(id, "fullName"), S(id, "firstName"), S(id, "middleName"), S(id, "lastName"),
                S(id, "dateOfBirth"), S(id, "gender"), S(id, "phone"), S(id, "stateOfOrigin"), null, null)
            : null;
        return new VerificationOutcome(stored.CallId, stored.VerificationType, S(root, "environment") ?? provider.Environment,
            S(root, "verdict") ?? (stored.Status == "succeeded" ? "match" : "provider_error"), identity, S(root, "message"),
            stored.AmountKobo, Refunded: stored.Status == "failed", null, stored.CreatedAt, Replayed: true);
    }
}
