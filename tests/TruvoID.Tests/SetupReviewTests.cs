using System.Text.Json;
using Npgsql;
using TruvoID.Infrastructure.Postgres;

namespace TruvoID.Tests;

/// <summary>Profile review gates live verification. Runs as the DML-only app role.</summary>
public class SetupReviewTests(TenantDatabase t) : IClassFixture<TenantDatabase>
{
    private OrganizationSetupStore Store => new(t.ControlPlane);
    private static readonly JsonElement Section = JsonDocument.Parse("""{"name":"x"}""").RootElement;

    private async Task<Guid> NewInstitutionAsync() =>
        await t.Registry.RegisterAsync($"Inst {Guid.NewGuid():N}", OrganizationType.Institution);

    private async Task<Guid> ReviewerAsync() =>
        (await new PlatformAdminBootstrapper(t.Database.MigratorConnectionString)
            .CreateAsync($"reviewer-{Guid.NewGuid():N}@gettruvoid.com", null)).UserId;

    [Fact]
    public async Task Institution_IsTestOnly_UntilApproved()
    {
        var org = await NewInstitutionAsync();
        Assert.False(await Store.IsLiveEnabledAsync(org));

        Assert.True(await Store.SubmitAsync(org));
        Assert.False(await Store.IsLiveEnabledAsync(org));      // submitted isn't enough

        Assert.True(await Store.ReviewAsync(org, approve: true, null, await ReviewerAsync()));
        Assert.True(await Store.IsLiveEnabledAsync(org));
        Assert.Equal("approved", (await Store.GetAsync(org)).Status);
    }

    [Fact]
    public async Task Agencies_AreLive_BecauseOpsCreatedThem() =>
        Assert.True(await Store.IsLiveEnabledAsync(t.AgencyId));

    [Fact]
    public async Task SubmittedOrApproved_ProfileIsLocked()
    {
        var org = await NewInstitutionAsync();
        await Store.SaveSectionAsync(org, "general", Section);
        await Store.SubmitAsync(org);

        await Assert.ThrowsAsync<SetupLockedException>(() => Store.SaveSectionAsync(org, "general", Section));
        Assert.False(await Store.SubmitAsync(org));             // can't resubmit while under review

        await Store.ReviewAsync(org, approve: true, null, await ReviewerAsync());
        await Assert.ThrowsAsync<SetupLockedException>(() => Store.SetAttestationAsync(org, false));
    }

    [Fact]
    public async Task RequestChanges_NeedsANote_ReopensEditing_AndAllowsResubmission()
    {
        var org = await NewInstitutionAsync();
        await Store.SubmitAsync(org);
        var reviewer = await ReviewerAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => Store.ReviewAsync(org, approve: false, " ", reviewer));
        Assert.True(await Store.ReviewAsync(org, approve: false, "Upload your CAC certificate.", reviewer));

        var snapshot = await Store.GetAsync(org);
        Assert.Equal("needs_changes", snapshot.Status);
        Assert.Equal("Upload your CAC certificate.", snapshot.ReviewNote);
        Assert.False(await Store.IsLiveEnabledAsync(org));
        await Store.SaveSectionAsync(org, "general", Section);   // editable again
        Assert.True(await Store.SubmitAsync(org));
    }

    [Fact]
    public async Task OnlySubmittedProfiles_CanBeReviewed()
    {
        var org = await NewInstitutionAsync();
        Assert.False(await Store.ReviewAsync(org, approve: true, null, await ReviewerAsync()));
    }
}
