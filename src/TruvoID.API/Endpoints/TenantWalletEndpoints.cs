using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TruvoID.Infrastructure.Identity;
using TruvoID.Infrastructure.Postgres;

namespace TruvoID.API.Endpoints;

public static class TenantWalletEndpoints
{
    public static IEndpointRouteBuilder MapTenantWalletEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/tenant/wallet").RequireAuthorization();
        group.MapGet("/balance", GetBalance);
        group.MapGet("/ledger", GetLedger);
        app.MapPost("/v1/admin/tenant-wallets/{organizationId:guid}/credit", CreditOrganizationWallet)
            .RequireAuthorization("TruvoAdmin");
        group.MapPost("/outlets/{outletId:guid}/purchase-credit", PurchaseOutletCredit);
        group.MapPost("/sandbox-funds", AddSandboxFunds).RequireAuthorization("TenantManager");
        return app;
    }

    /// <summary>
    /// Sandbox only: free test credit so integrators can exercise billing without paying.
    /// Returns 404 on live deployments, so it can't be found there, let alone abused.
    /// </summary>
    private static async Task<IResult> AddSandboxFunds(
        HttpContext ctx,
        SandboxFundsRequest? request,
        IIdentityProvider provider,
        TenantConnectionFactory tenants,
        TenantWalletService wallets,
        CancellationToken ct)
    {
        if (provider.Environment != "sandbox")
            return Results.NotFound();
        var amount = request?.AmountKobo ?? 1_000_000; // ₦10,000
        if (amount is <= 0 or > 100_000_000)
            return Results.BadRequest(new { error = "Test funds must be between ₦0.01 and ₦1,000,000 per top-up." });
        try
        {
            await using var session = await tenants.BeginAsync(TenantScope.Organization(ctx.GetOrganizationId()), ct);
            var mutation = await wallets.CreditAsync(session, amount, null, $"sandbox-funds-{Guid.NewGuid():N}", ct: ct);
            await session.CommitAsync(ct);
            return Results.Ok(new { creditedKobo = amount, balanceAfterKobo = mutation.BalanceAfterKobo, environment = "sandbox" });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not an active, provisioned tenant"))
        {
            return Results.Conflict(new { error = "Your workspace is still being set up.", code = "workspace_not_ready" });
        }
    }

    public sealed record SandboxFundsRequest(long? AmountKobo);

    private static async Task<IResult> GetBalance(
        HttpContext ctx,
        TenantConnectionFactory tenants,
        TenantWalletService wallets,
        CancellationToken ct)
    {
        try
        {
            await using var session = await tenants.BeginAsync(ctx.GetTenantScope(), ct);
            var balance = await wallets.GetBalanceAsync(session, ct);
            return Results.Ok(new { walletId = balance.WalletId, balanceKobo = balance.BalanceKobo });
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
    }

    private static async Task<IResult> GetLedger(
        HttpContext ctx,
        int page,
        int pageSize,
        TenantConnectionFactory tenants,
        TenantWalletService wallets,
        CancellationToken ct)
    {
        try
        {
            await using var session = await tenants.BeginAsync(ctx.GetTenantScope(), ct);
            var rows = await wallets.GetLedgerAsync(session, page, pageSize, ct);
            return Results.Ok(rows);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
    }

    private static async Task<IResult> CreditOrganizationWallet(
        Guid organizationId,
        CreditWalletRequest request,
        TenantConnectionFactory tenants,
        TenantWalletService wallets,
        CancellationToken ct)
    {
        try
        {
            await using var session = await tenants.BeginAsync(TenantScope.Organization(organizationId), ct);
            var mutation = await wallets.CreditAsync(session, request.AmountKobo, null,
                request.Reference ?? $"slogani-credit-{Guid.NewGuid():N}", request.UnitPriceKobo, ct);
            await wallets.AddRevenueOutboxEventAsync(session, new
            {
                organizationId,
                amountKobo = request.AmountKobo,
                reference = request.Reference,
                entryType = "credit_sale"
            }, ct);
            await session.CommitAsync(ct);
            return Results.Ok(new { mutation.WalletId, mutation.BalanceAfterKobo, mutation.LedgerEntryId });
        }
        catch (InsufficientWalletBalanceException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    private static async Task<IResult> PurchaseOutletCredit(
        Guid outletId,
        HttpContext ctx,
        CreditWalletRequest request,
        TenantConnectionFactory tenants,
        TenantWalletService wallets,
        CancellationToken ct)
    {
        try
        {
            var organizationId = ctx.GetOrganizationId();
            await using var session = await tenants.BeginAsync(TenantScope.Organization(organizationId), ct);
            var transfer = await wallets.TransferToOutletAsync(session, outletId, request.AmountKobo,
                request.Reference ?? $"outlet-resale-{Guid.NewGuid():N}", ct);
            await wallets.AddRevenueOutboxEventAsync(session, new
            {
                organizationId,
                outletId,
                amountKobo = request.AmountKobo,
                reference = request.Reference,
                entryType = "outlet_resale"
            }, ct);
            await session.CommitAsync(ct);
            return Results.Ok(new
            {
                outletId,
                sellerBalanceAfterKobo = transfer.SellerDebit.BalanceAfterKobo,
                outletBalanceAfterKobo = transfer.OutletCredit.BalanceAfterKobo
            });
        }
        catch (InsufficientWalletBalanceException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    public sealed record CreditWalletRequest(long AmountKobo, long? UnitPriceKobo, string? Reference);
}
