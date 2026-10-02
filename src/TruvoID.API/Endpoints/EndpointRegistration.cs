using Microsoft.AspNetCore.Routing;

namespace TruvoID.API.Endpoints;

public static class EndpointRegistration
{
    public static IEndpointRouteBuilder MapTruvoIdEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapAuthEndpoints();
        app.MapAgencyInvitationEndpoints();
        app.MapAdminOrganizationEndpoints();
        app.MapOrganizationSetupEndpoints();
        app.MapOrganizationBrandingEndpoints();
        app.MapAdminDashboardEndpoints();
        app.MapApiKeyEndpoints();
        app.MapTenantEndpoints();
        app.MapTenantWalletEndpoints();
        app.MapFlutterwavePaymentEndpoints();
        app.MapTenantVerificationEndpoints();
        app.MapTenantVerificationHistoryEndpoints();
        app.MapTenantTeamEndpoints();
        app.MapPricingEndpoints();
        app.MapPasswordResetEndpoints();

        return app;
    }
}
