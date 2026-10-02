using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using TruvoID.Infrastructure.Postgres;

namespace TruvoID.Infrastructure.Identity;

/// <summary>
/// Refunds verification calls left 'pending' — the API charged the wallet but died
/// before recording the provider's answer. Runs in the worker. The age threshold is
/// far above <see cref="VerificationRunner.ProviderTimeout"/>, so a call still in
/// flight is never swept out from under the request that owns it.
/// </summary>
public sealed class VerificationSweeper(
    NpgsqlDataSource controlPlane,
    TenantConnectionFactory tenants,
    TenantVerificationService verifications,
    ILogger logger)
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var organizations = new List<Guid>();
        await using (var list = controlPlane.CreateCommand("SELECT id FROM control.organization WHERE status = 'active'"))
        await using (var reader = await list.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                organizations.Add(reader.GetGuid(0));

        var refunded = 0;
        foreach (var organizationId in organizations)
        {
            try
            {
                refunded += await SweepOrganizationAsync(organizationId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One broken tenant must not stop refunds for everyone else.
                logger.LogError(ex, "Sweeping stale verifications failed for organization {OrganizationId}", organizationId);
            }
        }
        return refunded;
    }

    private async Task<int> SweepOrganizationAsync(Guid organizationId, CancellationToken ct)
    {
        var stale = new List<(Guid CallId, Guid? OutletId)>();
        await using (var session = await tenants.BeginAsync(TenantScope.Organization(organizationId), ct))
        {
            await using var find = session.CreateCommand("""
                SELECT id, outlet_id FROM verification_call
                WHERE status = 'pending' AND created_at < now() - make_interval(secs => @age)
                ORDER BY created_at LIMIT 200
                """);
            find.Parameters.AddWithValue("age", StaleAfter.TotalSeconds);
            await using var reader = await find.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                stale.Add((reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1)));
        }

        var refunded = 0;
        foreach (var (callId, outletId) in stale)
        {
            // Outlet calls are refunded in outlet scope so an agency outlet's own wallet gets the money back.
            var scope = outletId is { } outlet ? TenantScope.Outlet(organizationId, outlet) : TenantScope.Organization(organizationId);
            await using var session = await tenants.BeginAsync(scope, ct);
            try
            {
                await verifications.CompleteAsync(session, callId, succeeded: false, Result, Message, ct);
                await session.CommitAsync(ct);
                refunded++;
                logger.LogWarning("Refunded stale verification {CallId} for organization {OrganizationId}", callId, organizationId);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already been completed"))
            {
                // The owning request finished between our scan and this call — nothing to do.
            }
        }
        return refunded;
    }

    private const string Message = "The check did not complete in time. You have not been charged.";

    private static readonly string Result = JsonSerializer.Serialize(new
    {
        verdict = "provider_error",
        message = Message,
        sweptAt = "stale-call-sweeper",
    });
}
