using System.Web;
using CSweet.Application.SourceControl;
using CSweet.Contracts.SourceControl;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.SourceControl;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CSweet.UnitTests;

public sealed class SourceControlOnboardingServiceTests
{
    [Fact]
    public async Task AuthenticatedReconnectionPreservesNameAndClearsDisconnectedTimestamp()
    {
        var business = Guid.NewGuid(); var user = Guid.NewGuid(); await using var db = CreateDb(); SeedManager(db, business, user);
        var connection = new SourceControlConnection { Id = Guid.NewGuid(), OrganizationId = business, Name = "My project hosting", Provider = SourceControlProvider.GitHub,
            Mode = SourceControlConnectionMode.ExistingGitHub, ProviderAccountId = "99", AccountLogin = "approved-org", AccountType = "Organization",
            Status = SourceControlConnectionStatus.Disconnected, DisconnectedAt = DateTimeOffset.UtcNow.AddDays(-1) };
        db.Add(connection); await db.SaveChangesAsync();
        var host = new InstallationHost(new TrustedInstallationDescriptor(10, 99, "approved-org", "Organization", false, null));
        var service = CreateService(db, host, host);
        var started = await service.StartAsync(business, user, new StartSourceControlOnboardingRequest("ExistingGitHub"));
        await service.CompleteGitHubInstallationAsync(business, user, started.SessionId,
            new CompleteGitHubAppInstallationRequest(ReadState(started.AuthorizationUrl), 10, "SourceAccess", "oauth-code"));
        db.ChangeTracker.Clear(); var reconnected = await db.SourceControlConnections.SingleAsync();
        Assert.Equal(connection.Id, reconnected.Id); Assert.Equal("My project hosting", reconnected.Name);
        Assert.Equal(SourceControlConnectionStatus.Connected, reconnected.Status); Assert.Null(reconnected.DisconnectedAt);
    }

    [Fact]
    public void RuntimeDependencyInjectionHasSinglePublicConstructor()
    {
        Assert.Single(typeof(SourceControlOnboardingService).GetConstructors());
    }

    [Fact]
    public async Task DashboardReportsUnavailableModesBeforeConnectIsAttempted()
    {
        var organizationId = Guid.NewGuid();
        var applicationUserId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedManager(db, organizationId, applicationUserId);
        await db.SaveChangesAsync();
        var host = new InstallationHost(new TrustedInstallationDescriptor(
            10, 99, "approved-org", "Organization", false, null));
        var service = new SourceControlOnboardingService(
            db, host, host, new ConfigurationBuilder().Build(), new AuthorizingUser(), TimeProvider.System);

        var dashboard = await service.GetDashboardAsync(organizationId, applicationUserId);

        Assert.True(dashboard.CanManageSourceControl);
        Assert.False(dashboard.PlatformReadiness.ExistingGitHubAvailable);
        Assert.False(dashboard.PlatformReadiness.ManagedGitHubAvailable);
        Assert.Contains("platform administrator", dashboard.PlatformReadiness.UserMessage!,
            StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(
            organizationId,
            applicationUserId,
            new StartSourceControlOnboardingRequest("ExistingGitHub")));
        Assert.Empty(db.SourceControlOnboardingSessions);
    }

    [Theory]
    [InlineData(OrganizationPermissionLevel.Viewer, false)]
    [InlineData(OrganizationPermissionLevel.Contributor, false)]
    [InlineData(OrganizationPermissionLevel.Manager, true)]
    [InlineData(OrganizationPermissionLevel.Owner, true)]
    public async Task DashboardReportsWhetherActorCanManageSourceControl(
        OrganizationPermissionLevel permissionLevel,
        bool expected)
    {
        var organizationId = Guid.NewGuid();
        var applicationUserId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedActor(db, organizationId, applicationUserId, permissionLevel);
        await db.SaveChangesAsync();
        var host = new InstallationHost(new TrustedInstallationDescriptor(
            10, 99, "approved-org", "Organization", false, null));
        var service = new SourceControlOnboardingService(
            db, host, host, new ConfigurationBuilder().Build(), new AuthorizingUser(), TimeProvider.System);

        var dashboard = await service.GetDashboardAsync(organizationId, applicationUserId);

        Assert.Equal(expected, dashboard.CanManageSourceControl);
    }

    [Fact]
    public async Task DashboardReportsSourceOnlyAndFullyConfiguredReadiness()
    {
        var organizationId = Guid.NewGuid();
        var applicationUserId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedManager(db, organizationId, applicationUserId);
        await db.SaveChangesAsync();
        var host = new InstallationHost(new TrustedInstallationDescriptor(
            10, 99, "approved-org", "Organization", false, null));

        var sourceOnly = await CreateService(
            db, host, host, provisionerConfigured: false)
            .GetDashboardAsync(organizationId, applicationUserId);
        var fullyConfigured = await CreateService(
            db, host, host, provisionerConfigured: true)
            .GetDashboardAsync(organizationId, applicationUserId);

        Assert.True(sourceOnly.PlatformReadiness.ExistingGitHubAvailable);
        Assert.False(sourceOnly.PlatformReadiness.ManagedGitHubAvailable);
        Assert.NotNull(sourceOnly.PlatformReadiness.UserMessage);
        Assert.True(fullyConfigured.PlatformReadiness.ExistingGitHubAvailable);
        Assert.True(fullyConfigured.PlatformReadiness.ManagedGitHubAvailable);
        Assert.Null(fullyConfigured.PlatformReadiness.UserMessage);
    }

    [Fact]
    public async Task ManagedSetupRequiresTwoMatchingOrganizationInstallations()
    {
        var organizationId = Guid.NewGuid();
        var applicationUserId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedManager(db, organizationId, applicationUserId);
        await db.SaveChangesAsync();
        var source = new InstallationHost(new TrustedInstallationDescriptor(
            10, 99, "approved-org", "Organization", false, null));
        var provisioner = new InstallationHost(new TrustedInstallationDescriptor(
            11, 99, "approved-org", "Organization", false, null));
        var service = CreateService(db, source, provisioner);

        var started = await service.StartAsync(
            organizationId, applicationUserId,
            new StartSourceControlOnboardingRequest("ManagedGitHub"));
        var sourceState = ReadState(started.AuthorizationUrl);
        var sourceResult = await service.CompleteGitHubInstallationAsync(
            organizationId, applicationUserId, started.SessionId,
            new CompleteGitHubAppInstallationRequest(sourceState, 10, "SourceAccess", "oauth-code"));
        Assert.False(sourceResult.InstallationSetupComplete);
        Assert.NotNull(sourceResult.NextAuthorizationUrl);

        var provisionerState = ReadState(sourceResult.NextAuthorizationUrl!);
        var result = await service.CompleteGitHubInstallationAsync(
            organizationId, applicationUserId, started.SessionId,
            new CompleteGitHubAppInstallationRequest(provisionerState, 11, "Provisioner", "oauth-code"));

        Assert.True(result.InstallationSetupComplete);
        var connection = await db.SourceControlConnections.SingleAsync();
        Assert.Equal(10, connection.SourceAccessInstallationId);
        Assert.Equal(11, connection.ProvisionerInstallationId);
        Assert.Equal(SourceControlConnectionStatus.Connected, connection.Status);
        Assert.DoesNotContain(connection.GetType().GetProperties(), property =>
            property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("PrivateKey", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ManagedSetupAcceptsPersonalInstallationAndRequiresProvisioner()
    {
        var organizationId = Guid.NewGuid();
        var applicationUserId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedManager(db, organizationId, applicationUserId);
        await db.SaveChangesAsync();
        var source = new InstallationHost(new TrustedInstallationDescriptor(
            10, 99, "personal-user", "User", false, null));
        var service = CreateService(db, source, source);
        var started = await service.StartAsync(
            organizationId, applicationUserId,
            new StartSourceControlOnboardingRequest("ManagedGitHub"));

        var next = await service.CompleteGitHubInstallationAsync(
            organizationId, applicationUserId, started.SessionId,
            new CompleteGitHubAppInstallationRequest(ReadState(started.AuthorizationUrl), 10, "SourceAccess", "oauth-code"));
        var connection = Assert.Single(db.SourceControlConnections);
        Assert.Equal("User", connection.AccountType);
        Assert.Equal(10, connection.SourceAccessInstallationId);
        Assert.Null(connection.ProvisionerInstallationId);
        Assert.NotEqual(SourceControlConnectionStatus.Connected, connection.Status);
        Assert.NotNull(next.NextAuthorizationUrl);
    }

    [Fact]
    public async Task SamePersonalAccountCanConnectToMoreThanOneBusiness()
    {
        var organizationId = Guid.NewGuid();
        var otherOrganizationId = Guid.NewGuid();
        var applicationUserId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedManager(db, organizationId, applicationUserId);
        var now = DateTimeOffset.UtcNow;
        var otherConnectionId = Guid.NewGuid();
        db.SourceControlConnections.Add(new SourceControlConnection
        {
            Id = otherConnectionId, OrganizationId = otherOrganizationId, Name = "Other",
            Provider = SourceControlProvider.GitHub,
            Mode = SourceControlConnectionMode.ExistingGitHub,
            Status = SourceControlConnectionStatus.Connected,
            ProviderAccountId = "99", AccountLogin = "personal-user", AccountType = "User",
            SourceAccessInstallationId = 7, CreatedAt = now, UpdatedAt = now
        });
        await db.SaveChangesAsync();
        var repositories = new[]
        {
            new TrustedRepositoryDescriptor(
                77, "personal-user", "shared-project", "personal-user/shared-project",
                "https://github.com/personal-user/shared-project.git", "main", true, false, false)
        };
        var host = new InstallationHost(new TrustedInstallationDescriptor(
            10, 99, "personal-user", "User", false, null), repositories);
        var service = CreateService(db, host, host);
        var started = await service.StartAsync(
            organizationId, applicationUserId,
            new StartSourceControlOnboardingRequest("ExistingGitHub"));

        var completed = await service.CompleteGitHubInstallationAsync(
            organizationId, applicationUserId, started.SessionId,
            new CompleteGitHubAppInstallationRequest(
                ReadState(started.AuthorizationUrl), 10, "SourceAccess", "oauth-code"));

        Assert.Equal(2, await db.SourceControlConnections.CountAsync());

        db.SourceControlRepositories.Add(new SourceControlRepository
        {
            Id = Guid.NewGuid(), OrganizationId = otherOrganizationId,
            ConnectionId = otherConnectionId, ExternalRepositoryId = "77",
            ProviderRepositoryKey = "github:77", Owner = "personal-user", Name = "shared-project",
            CanonicalPath = "personal-user/shared-project", CloneUrl = repositories[0].CloneUrl,
            DefaultBranch = "main", IsPrivate = true, Status = SourceControlRepositoryStatus.Ready,
            CreatedAt = now, UpdatedAt = now
        });
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.SelectExistingRepositoriesAsync(
                organizationId, applicationUserId, completed.ConnectionId,
                new SelectExistingCodeProjectsRequest(["77"])));
    }

    [Fact]
    public async Task ManagedPolicyCanOnlyUseProviderVerifiedTemplateAndTeam()
    {
        var organizationId = Guid.NewGuid();
        var applicationUserId = Guid.NewGuid();
        await using var db = CreateDb();
        var manager = SeedManager(db, organizationId, applicationUserId);
        var team = new OrganizationTeam
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId,
            TeamKey = "software", NormalizedName = "software", Name = "Software",
            Description = "Builds software", LeadOrganizationUserId = manager.Id,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.OrganizationTeams.Add(team);
        await db.SaveChangesAsync();
        var repositories = new[]
        {
            new TrustedRepositoryDescriptor(
                77, "approved-org", "starter", "approved-org/starter",
                "https://github.com/approved-org/starter.git", "main", true, false, true)
        };
        var source = new InstallationHost(new TrustedInstallationDescriptor(
            10, 99, "approved-org", "Organization", false, null), repositories);
        var service = CreateService(db, source, source);
        var started = await service.StartAsync(
            organizationId, applicationUserId,
            new StartSourceControlOnboardingRequest("ManagedGitHub"));
        var sourceResult = await service.CompleteGitHubInstallationAsync(
            organizationId, applicationUserId, started.SessionId,
            new CompleteGitHubAppInstallationRequest(ReadState(started.AuthorizationUrl), 10, "SourceAccess", "oauth-code"));
        var installed = await service.CompleteGitHubInstallationAsync(
            organizationId, applicationUserId, started.SessionId,
            new CompleteGitHubAppInstallationRequest(ReadState(sourceResult.NextAuthorizationUrl!), 11, "Provisioner", "oauth-code"));

        var policy = await service.ConfigureManagedRepositoriesAsync(
            organizationId, applicationUserId, installed.ConnectionId,
            new ConfigureManagedCodeProjectsRequest(
                ["77"], "acme", 25, true, team.Id, null));

        Assert.Equal(team.Id, policy.DefaultTeamId);
        Assert.Equal("acme", policy.NamePrefix);
        Assert.Single(policy.ApprovedTemplateIds);
        Assert.Single(db.SourceControlRepositoryTemplates);
        Assert.Equal(SourceControlOnboardingStatus.Completed,
            (await db.SourceControlOnboardingSessions.SingleAsync()).Status);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ConfigureManagedRepositoriesAsync(
                organizationId, applicationUserId, installed.ConnectionId,
                new ConfigureManagedCodeProjectsRequest(
                    ["88"], "acme", 25, true, team.Id, policy.Revision)));
    }

    private static SourceControlOnboardingService CreateService(
        CSweetDbContext db,
        ITrustedSourceControlHostClient source,
        ITrustedProvisioningHostClient provisioner,
        bool provisionerConfigured = true)
    {
        var values = new Dictionary<string, string?>
        {
            ["CSweet:SourceControl:SourceAccessInstallUrl"] = "https://github.com/apps/csweet-source/installations/new",
            ["CSweet:SourceControl:SourceAccessClientId"] = "source-client",
            ["CSweet:SourceControl:SourceAccessClientSecret"] = "source-secret",
            ["CSweet:SourceControl:GitHostBaseUrl"] = "http://githost/"
        };
        if (provisionerConfigured)
        {
            values["CSweet:SourceControl:ProvisionerInstallUrl"] =
                "https://github.com/apps/csweet-provisioner/installations/new";
            values["CSweet:SourceControl:ProvisionerClientId"] = "provisioner-client";
            values["CSweet:SourceControl:ProvisionerClientSecret"] = "provisioner-secret";
            values["CSweet:SourceControl:ProvisionerHostBaseUrl"] = "http://provisionerhost/";
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new SourceControlOnboardingService(
            db, source, provisioner, configuration, new AuthorizingUser(), TimeProvider.System);
    }

    private static string ReadState(string url)
    {
        var query = HttpUtility.ParseQueryString(new Uri(url).Query);
        return query["state"] ?? throw new InvalidOperationException("No state was returned.");
    }

    private static OrganizationUser SeedManager(
        CSweetDbContext db,
        Guid organizationId,
        Guid applicationUserId) =>
        SeedActor(db, organizationId, applicationUserId, OrganizationPermissionLevel.Owner);

    private static OrganizationUser SeedActor(
        CSweetDbContext db,
        Guid organizationId,
        Guid applicationUserId,
        OrganizationPermissionLevel permissionLevel)
    {
        var actor = new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId,
            ApplicationUserId = applicationUserId, DisplayName = permissionLevel.ToString(),
            EmployeeType = EmployeeType.Human,
            PermissionLevel = permissionLevel,
            CreatedAt = DateTimeOffset.UtcNow, IsActive = true
        };
        db.CoreOrganizationUsers.Add(actor);
        return actor;
    }

    private static CSweetDbContext CreateDb() => new(
        new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase($"source-control-onboarding-{Guid.NewGuid():N}")
            .Options);

    private sealed class InstallationHost(
        TrustedInstallationDescriptor descriptor,
        IReadOnlyList<TrustedRepositoryDescriptor>? repositories = null)
        : ITrustedSourceControlHostClient, ITrustedProvisioningHostClient
    {
        public Task<TrustedInstallationDescriptor> DescribeInstallationAsync(
            long installationId,
            CancellationToken cancellationToken = default) => Task.FromResult(
            descriptor with { InstallationId = installationId });

        public Task<IReadOnlyList<TrustedRepositoryDescriptor>> ListRepositoriesAsync(
            long installationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(repositories ?? []);

        public Task<TrustedMergeResult> MergeAsync(
            TrustedMergeRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TrustedWorkspaceSnapshot> PrepareWorkspaceAsync(
            TrustedWorkspaceSnapshotRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TrustedRepositoryProvisioningResult> ProvisionAsync(
            TrustedRepositoryProvisioningRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class AuthorizingUser : IGitHubUserAuthorizationClient
    {
        public Task<GitHubAuthorizedInstallation> VerifyInstallationAsync(
            PlatformGitHubUserAuthorizationConfiguration configuration,
            string code,
            long installationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitHubAuthorizedInstallation(installationId, 123, "installer"));
    }
}
