using Microsoft.AspNetCore.Routing;

namespace TruvoID.API.Endpoints;

public static class EndpointRegistration
{
    public static IEndpointRouteBuilder MapTruvoIdEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapAuthEndpoints();
        app.MapAgencyInvitationEndpoints();
        app.MapOrganizationSetupEndpoints();
        app.MapAdminDashboardEndpoints();
        app.MapApiKeyEndpoints();
        app.MapTenantEndpoints();
        app.MapTenantWalletEndpoints();
        app.MapTenantVerificationEndpoints();

        return app;
    }
}
