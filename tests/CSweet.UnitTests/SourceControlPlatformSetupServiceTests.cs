using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using CSweet.Application.Setup;
using CSweet.Application.SourceControl;
using CSweet.Contracts.SourceControl;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.SourceControl;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CSweet.UnitTests;

public sealed class SourceControlPlatformSetupServiceTests
{
    [Fact]
    public async Task InstalledAccountAndRepositoriesAreVisibleBeforeAnyBusinessConnectionExists()
    {
        await using var db = CreateDb();
        var host = new ConfigurableHost("source", "Source")
        {
            Installations = [new(7, 42, "octocat", "User", false, null)],
            Repositories = [new(11, "octocat", "game", "octocat/game", "", "main", true, false, false)]
        };
        db.PlatformGitHubAppCredentials.Add(new()
        {
            Id = Guid.NewGuid(), Kind = PlatformGitHubAppKind.SourceAccess, OwnerLogin = "octocat", OwnerType = "User",
            Status = PlatformGitHubAppCredentialStatus.Active, AppId = 42, InstallUrl = "https://github.com/apps/source/installations/new"
        });
        await db.SaveChangesAsync();
        var service = CreateService(db, host, host, new ManifestClient(new(42, "Source", "source", "", "", "", "octocat", "User")));
        var access = await service.GetAccessOverviewAsync();
        Assert.Equal("octocat", Assert.Single(access.Installations).AccountLogin);
        Assert.Empty(access.Installations[0].Businesses);
        Assert.Equal("octocat/game", Assert.Single(await service.ListInstallationRepositoryAccessAsync(7)).FullName);
        Assert.Empty(db.SourceControlConnections);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ListInstallationRepositoryAccessAsync(999));
        Assert.Single(host.RepositoryInstallationIds);
        host.Installations = [];
        Assert.Empty((await service.GetAccessOverviewAsync()).Installations);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ListInstallationRepositoryAccessAsync(7));
    }

    [Fact]
    public async Task ConnectionInventoryUsesCurrentInstallationGrantsAndExcludesDisconnectedAccounts()
    {
        await using var db = CreateDb();
        var business = new CSweet.Domain.Core.Organization { Id = Guid.NewGuid(), Name = "Studio" };
        db.CoreOrganizations.Add(business);
        var connection = new SourceControlConnection
        {
            Id = Guid.NewGuid(), OrganizationId = business.Id, Provider = SourceControlProvider.GitHub,
            AccountLogin = "octocat", AccountType = "User", Status = SourceControlConnectionStatus.Connected,
            SourceAccessInstallationId = 7, Mode = SourceControlConnectionMode.ExistingGitHub
        };
        db.SourceControlConnections.Add(connection);
        db.SourceControlConnections.Add(new()
        {
            Id = Guid.NewGuid(), OrganizationId = business.Id, Provider = SourceControlProvider.GitHub,
            AccountLogin = "disconnected", Status = SourceControlConnectionStatus.Disconnected, SourceAccessInstallationId = 8
        });
        db.SourceControlRepositories.Add(new()
        {
            Id = Guid.NewGuid(), OrganizationId = business.Id, ConnectionId = connection.Id,
            ExternalRepositoryId = "11", ProviderRepositoryKey = "11", CanonicalPath = "octocat/game", Status = SourceControlRepositoryStatus.Ready
        });
        await db.SaveChangesAsync();
        var host = new ConfigurableHost("source", "Source")
        {
            Repositories = [new(11, "octocat", "game", "octocat/game", "", "main", true, false, false),
                new(12, "octocat", "old", "octocat/old", "", "main", false, true, false),
                new(13, "other", "foreign", "other/foreign", "", "main", true, false, false)]
        };
        var service = CreateService(db, host, host, new ManifestClient(new(42, "Source", "source", "", "", "", "octocat", "User")));
        Assert.Equal("Studio", Assert.Single(await service.ListConnectionsAsync()).BusinessName);
        var granted = await service.ListRepositoryAccessAsync(connection.Id);
        Assert.Equal(2, granted.Count);
        Assert.True(granted[0].SelectedForBusiness);
        Assert.True(granted[1].IsArchived);
        Assert.False(granted[1].SelectedForBusiness);
        host.Repositories = [];
        Assert.Empty(await service.ListRepositoryAccessAsync(connection.Id));
        Assert.All(host.RepositoryInstallationIds, id => Assert.Equal(7, id));
        connection.Status = SourceControlConnectionStatus.Disconnected;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ListRepositoryAccessAsync(connection.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartPersistsCreationChoiceAndResumeKeepsOriginalChoice(bool allowCreation)
    {
        await using var db = CreateDb();
        var host = new ConfigurableHost("source", "Source");
        var service = CreateService(db, host, host, new ManifestClient(new(
            42, "Source", "source", CreatePrivateKeyPem(), "Iv1.source", "secret", "octocat", "User")));
        var user = Guid.NewGuid();
        var setup = await service.StartAsync(user, new("https://csweet.example", AllowRepositoryCreation: allowCreation));
        Assert.Equal(allowCreation, setup.Session!.ProvisionerRequested);
        Assert.Equal(allowCreation, (await service.GetAsync(user)).Session!.ProvisionerRequested);
        var resumed = await service.StartAsync(user, new("https://csweet.example", AllowRepositoryCreation: !allowCreation));
        Assert.Equal(setup.Session.SessionId, resumed.Session!.SessionId);
        Assert.Equal(allowCreation, resumed.Session.ProvisionerRequested);
    }

    [Fact]
    public async Task PersonalSetupNeedsNoLoginAndStoresTheOwnerReturnedByGitHub()
    {
        await using var db = CreateDb();
        var source = new ConfigurableHost("source", "Source");
        var service = CreateService(db, source, source, new ManifestClient(new(
            42, "Source", "source", CreatePrivateKeyPem(), "Iv1.source", "secret", "octocat", "User")));
        var user = Guid.NewGuid();
        var setup = await service.StartAsync(user, new("https://csweet.example"));
        setup = await service.ConfirmOrganizationAsync(user, setup.Session!.SessionId,
            new(null, true, setup.Session.Revision));
        setup = await service.ConfirmReviewAsync(user, setup.Session!.SessionId,
            PlatformGitHubAppKind.SourceAccess, new(true, setup.Session.Revision));
        var launch = await service.CreateManifestAsync(user, setup.Session!.SessionId, PlatformGitHubAppKind.SourceAccess);
        Assert.Equal("/settings/apps/new", new Uri(launch.PostUrl).AbsolutePath);
        await service.CompleteManifestAsync(user, "code", HttpUtility.ParseQueryString(new Uri(launch.PostUrl).Query)["state"]!);
        var credential = Assert.Single(db.PlatformGitHubAppCredentials);
        Assert.Equal("octocat", credential.OwnerLogin);
        Assert.Equal("User", credential.OwnerType);
        Assert.Equal(PlatformGitHubAppCredentialStatus.Verified, credential.Status);
        setup = await service.GetAsync(user);
        setup = await service.ConfirmAppAsync(user, setup.Session!.SessionId, PlatformGitHubAppKind.SourceAccess, new(true, setup.Session.Revision));
        setup = await service.ChooseProvisionerAsync(user, setup.Session!.SessionId, new(false, setup.Session.Revision));
        setup = await service.ActivateAsync(user, setup.Session!.SessionId, new(true, setup.Session.Revision));
        Assert.True(setup.Readiness.ExistingGitHubAvailable);

        // Later creation setup must keep using the personal endpoint, not /organizations/octocat/.
        setup = await service.StartAsync(user, new("https://csweet.example"));
        Assert.Empty(setup.Session!.GitHubOrganization);
        setup = await service.ChooseProvisionerAsync(user, setup.Session.SessionId, new(true, setup.Session.Revision));
        setup = await service.ConfirmReviewAsync(user, setup.Session!.SessionId, PlatformGitHubAppKind.Provisioner, new(true, setup.Session.Revision));
        launch = await service.CreateManifestAsync(user, setup.Session!.SessionId, PlatformGitHubAppKind.Provisioner);
        Assert.Equal("/settings/apps/new", new Uri(launch.PostUrl).AbsolutePath);
    }

    [Theory]
    [InlineData(null, "someone", "Organization")]
    [InlineData("central-org", "other-org", "Organization")]
    [InlineData("central-org", "central-org", "User")]
    [InlineData(null, "", "User")]
    public async Task CallbackRejectsMissingOrMismatchedOwner(string? requestedOrganization, string ownerLogin, string ownerType)
    {
        await using var db = CreateDb();
        var host = new ConfigurableHost("source", "Source");
        var service = CreateService(db, host, host, new ManifestClient(new(
            42, "Source", "source", CreatePrivateKeyPem(), "Iv1.source", "secret", ownerLogin, ownerType)));
        var user = Guid.NewGuid();
        var setup = await service.StartAsync(user, new("https://csweet.example"));
        setup = await service.ConfirmOrganizationAsync(user, setup.Session!.SessionId, new(requestedOrganization, true, setup.Session.Revision));
        setup = await service.ConfirmReviewAsync(user, setup.Session!.SessionId, PlatformGitHubAppKind.SourceAccess, new(true, setup.Session.Revision));
        var launch = await service.CreateManifestAsync(user, setup.Session!.SessionId, PlatformGitHubAppKind.SourceAccess);
        var state = HttpUtility.ParseQueryString(new Uri(launch.PostUrl).Query)["state"]!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompleteManifestAsync(user, "code", state));
        Assert.Empty(db.PlatformGitHubAppCredentials);
        Assert.False((await service.GetAsync(user)).Readiness.ExistingGitHubAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompleteManifestAsync(user, "code", state));
    }

    [Theory]
    [InlineData("octocat", true)]
    [InlineData("different-user", false)]
    public async Task PersonalProvisionerMustBelongToTheVerifiedSourceAppOwner(string provisionerOwner, bool succeeds)
    {
        await using var db = CreateDb();
        var host = new ConfigurableHost("provisioner", "Provisioner");
        var service = CreateService(db, host, host, new ManifestClient(new(
            43, "Provisioner", "provisioner", CreatePrivateKeyPem(), "Iv1.provisioner", "secret", provisionerOwner, "User")));
        db.PlatformGitHubAppCredentials.Add(new()
        {
            Id = Guid.NewGuid(), Kind = PlatformGitHubAppKind.SourceAccess, OwnerLogin = "octocat", OwnerType = "User",
            Status = PlatformGitHubAppCredentialStatus.Active, AppId = 42, ClientId = "Iv1.source", ProtectedClientSecret = "protected",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        });
        await db.SaveChangesAsync();
        var user = Guid.NewGuid();
        var setup = await service.StartAsync(user, new("https://csweet.example"));
        setup = await service.ChooseProvisionerAsync(user, setup.Session!.SessionId, new(true, setup.Session.Revision));
        setup = await service.ConfirmReviewAsync(user, setup.Session!.SessionId, PlatformGitHubAppKind.Provisioner, new(true, setup.Session.Revision));
        var launch = await service.CreateManifestAsync(user, setup.Session!.SessionId, PlatformGitHubAppKind.Provisioner);
        var completion = service.CompleteManifestAsync(user, "code", HttpUtility.ParseQueryString(new Uri(launch.PostUrl).Query)["state"]!);
        if (succeeds)
        {
            await completion;
            Assert.Equal("octocat", db.PlatformGitHubAppCredentials.Single(c => c.Kind == PlatformGitHubAppKind.Provisioner).OwnerLogin);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => completion);
            Assert.Single(db.PlatformGitHubAppCredentials);
        }
    }

    [Fact]
    public async Task GuidedSourceAccessFlowBuildsLeastPrivilegeManifestAndActivatesWithoutReturningKey()
    {
        await using var db = CreateDb();
        var privateKeyPem = CreatePrivateKeyPem();
        var sourceHost = new ConfigurableHost("csweet-source", "C-Sweet Source Access");
        var provisionerHost = new ConfigurableHost("csweet-provisioner", "C-Sweet Provisioner");
        var service = CreateService(db, sourceHost, provisionerHost,
            new ManifestClient(new PlatformGitHubManifestConversion(
                1234, "C-Sweet Source Access", "csweet-source", privateKeyPem,
                "Iv1.source", "source-secret", "central-org", "Organization")));
        var userId = Guid.NewGuid();

        var setup = await service.StartAsync(userId,
            new StartPlatformSourceControlSetupRequest("https://example.test/csweet"));
        setup = await service.ConfirmOrganizationAsync(userId, setup.Session!.SessionId,
            new ConfirmPlatformOrganizationRequest("central-org", true, setup.Session.Revision));
        setup = await service.ConfirmReviewAsync(userId, setup.Session!.SessionId,
            PlatformGitHubAppKind.SourceAccess,
            new ConfirmPlatformAppReviewRequest(true, setup.Session.Revision));
        var launch = await service.CreateManifestAsync(
            userId, setup.Session!.SessionId, PlatformGitHubAppKind.SourceAccess);

        using (var manifest = JsonDocument.Parse(launch.ManifestJson))
        {
            var root = manifest.RootElement;
            var appName = root.GetProperty("name").GetString();
            Assert.NotNull(appName);
            Assert.True(appName.Length <= 34);
            Assert.StartsWith("C-Sweet Source example-te-", appName, StringComparison.Ordinal);
            Assert.True(root.GetProperty("public").GetBoolean());
            Assert.False(root.TryGetProperty("hook_attributes", out _));
            Assert.Empty(root.GetProperty("default_events").EnumerateArray());
            Assert.Equal("write", root.GetProperty("default_permissions").GetProperty("contents").GetString());
            Assert.Equal("write", root.GetProperty("default_permissions").GetProperty("pull_requests").GetString());
            Assert.Equal("read", root.GetProperty("default_permissions").GetProperty("checks").GetString());
            Assert.Equal("read", root.GetProperty("default_permissions").GetProperty("metadata").GetString());
            Assert.Equal("https://example.test/csweet/api/source-control/platform-setup/github-manifest-callback",
                root.GetProperty("redirect_url").GetString());
            Assert.True(root.GetProperty("request_oauth_on_install").GetBoolean());
            Assert.Equal("https://example.test/csweet/source-control/github-callback",
                root.GetProperty("callback_urls")[0].GetString());
            Assert.False(root.TryGetProperty("setup_url", out _));
        }
        Assert.DoesNotContain("PRIVATE KEY", launch.ManifestJson, StringComparison.Ordinal);
        var state = HttpUtility.ParseQueryString(new Uri(launch.PostUrl).Query)["state"]!;
        await service.CompleteManifestAsync(userId, "one-time-code", state);

        setup = await service.GetAsync(userId);
        Assert.Equal("source-access-confirm", setup.Session!.CurrentStep);
        Assert.Equal("Verified", setup.Session.SourceAccessApp!.Status);
        var stored = Assert.Single(db.PlatformGitHubAppCredentials);
        Assert.Equal("Iv1.source", stored.ClientId);
        Assert.DoesNotContain("source-secret", stored.ProtectedClientSecret, StringComparison.Ordinal);
        Assert.DoesNotContain(privateKeyPem, stored.ProtectedPrivateKey, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(privateKeyPem)),
            stored.ProtectedPrivateKey, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", JsonSerializer.Serialize(setup), StringComparison.Ordinal);

        setup = await service.ConfirmAppAsync(userId, setup.Session.SessionId,
            PlatformGitHubAppKind.SourceAccess,
            new ConfirmPlatformAppRequest(true, setup.Session.Revision));
        setup = await service.ChooseProvisionerAsync(userId, setup.Session!.SessionId,
            new ChoosePlatformProvisionerRequest(false, setup.Session.Revision));
        setup = await service.ActivateAsync(userId, setup.Session!.SessionId,
            new ActivatePlatformSourceControlRequest(true, setup.Session.Revision));

        Assert.Equal("Active", setup.Session!.Status);
        Assert.True(setup.Readiness.ExistingGitHubAvailable);
        Assert.False(setup.Readiness.ManagedGitHubAvailable);
        Assert.Equal("CSweetManaged", setup.Readiness.ConfigurationMode);
        Assert.True(sourceHost.Status.Configured);
        Assert.False(provisionerHost.Status.Configured);
    }

    [Fact]
    public async Task LocalhostManifestDoesNotSendAWebhookUrlToGitHub()
    {
        await using var db = CreateDb();
        var host = new ConfigurableHost("source", "Source");
        var service = CreateService(db, host, host,
            new ManifestClient(new PlatformGitHubManifestConversion(
                42, "Source", "source", CreatePrivateKeyPem(), "Iv1.source", "source-secret", "central-org", "Organization")));
        var userId = Guid.NewGuid();

        var setup = await service.StartAsync(userId,
            new StartPlatformSourceControlSetupRequest("http://localhost:5097"));
        setup = await service.ConfirmOrganizationAsync(userId, setup.Session!.SessionId,
            new ConfirmPlatformOrganizationRequest("central-org", true, setup.Session.Revision));
        setup = await service.ConfirmReviewAsync(userId, setup.Session!.SessionId,
            PlatformGitHubAppKind.SourceAccess,
            new ConfirmPlatformAppReviewRequest(true, setup.Session.Revision));

        var launch = await service.CreateManifestAsync(
            userId, setup.Session!.SessionId, PlatformGitHubAppKind.SourceAccess);
        using var manifest = JsonDocument.Parse(launch.ManifestJson);
        var root = manifest.RootElement;
        var suffix = setup.Session.SessionId.ToString("N")[..8];

        Assert.Equal($"C-Sweet Source localhost-{suffix}", root.GetProperty("name").GetString());
        Assert.True(root.GetProperty("name").GetString()!.Length <= 34);
        Assert.False(root.TryGetProperty("hook_attributes", out _));
        Assert.Empty(root.GetProperty("default_events").EnumerateArray());
        Assert.Equal(
            "http://localhost:5097/api/source-control/platform-setup/github-manifest-callback",
            root.GetProperty("redirect_url").GetString());
    }

    [Fact]
    public async Task ManifestStateIsSingleUseAndBoundToTheStartingAdministrator()
    {
        await using var db = CreateDb();
        var host = new ConfigurableHost("source", "Source");
        var service = CreateService(db, host, host,
            new ManifestClient(new PlatformGitHubManifestConversion(
                42, "Source", "source", CreatePrivateKeyPem(), "Iv1.source", "source-secret", "central-org", "Organization")));
        var owner = Guid.NewGuid();
        var setup = await service.StartAsync(owner,
            new StartPlatformSourceControlSetupRequest("https://csweet.example"));
        setup = await service.ConfirmOrganizationAsync(owner, setup.Session!.SessionId,
            new ConfirmPlatformOrganizationRequest("central-org", true, setup.Session.Revision));
        setup = await service.ConfirmReviewAsync(owner, setup.Session!.SessionId,
            PlatformGitHubAppKind.SourceAccess,
            new ConfirmPlatformAppReviewRequest(true, setup.Session.Revision));
        var launch = await service.CreateManifestAsync(owner, setup.Session!.SessionId,
            PlatformGitHubAppKind.SourceAccess);
        var state = HttpUtility.ParseQueryString(new Uri(launch.PostUrl).Query)["state"]!;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CompleteManifestAsync(
            Guid.NewGuid(), "code", state));
        await service.CompleteManifestAsync(owner, "code", state);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompleteManifestAsync(
            owner, "code", state));
    }

    [Fact]
    public async Task OnlyOneAdministratorCanOwnAnActiveEnterpriseSetupSession()
    {
        await using var db = CreateDb();
        var host = new ConfigurableHost("source", "Source");
        var service = CreateService(db, host, host,
            new ManifestClient(new PlatformGitHubManifestConversion(
                42, "Source", "source", CreatePrivateKeyPem(), "Iv1.source", "source-secret", "central-org", "Organization")));

        await service.StartAsync(Guid.NewGuid(),
            new StartPlatformSourceControlSetupRequest("https://csweet.example"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartAsync(Guid.NewGuid(),
                new StartPlatformSourceControlSetupRequest("https://csweet.example")));
        Assert.Contains("Another system administrator", exception.Message, StringComparison.Ordinal);
    }

    private static SourceControlPlatformSetupService CreateService(
        CSweetDbContext db,
        ITrustedSourceControlHostClient source,
        ITrustedProvisioningHostClient provisioner,
        IPlatformGitHubManifestClient manifests) => new(
        db,
        new EphemeralDataProtectionProvider(),
        manifests,
        source,
        provisioner,
        new ConfigurationBuilder().Build(),
        new NoOpAuditWriter(),
        TimeProvider.System);

    private static CSweetDbContext CreateDb() => new(
        new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase($"platform-source-control-{Guid.NewGuid():N}")
            .Options);

    private static string CreatePrivateKeyPem()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }

    private sealed class ManifestClient(PlatformGitHubManifestConversion result)
        : IPlatformGitHubManifestClient
    {
        public Task<PlatformGitHubManifestConversion> ConvertAsync(
            string code, CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class ConfigurableHost(string slug, string name)
        : ITrustedSourceControlHostClient, ITrustedProvisioningHostClient
    {
        public IReadOnlyList<TrustedRepositoryDescriptor> Repositories { get; set; } = [];
        public IReadOnlyList<TrustedInstallationDescriptor> Installations { get; set; } = [];
        public Task<IReadOnlyList<TrustedInstallationDescriptor>> ListInstallationsAsync(CancellationToken ct = default) => Task.FromResult(Installations);
        public List<long> RepositoryInstallationIds { get; } = [];
        public TrustedGitHubAppConfigurationStatus Status { get; private set; } =
            new(false, null, 0, null, null, null);

        public Task<TrustedGitHubAppConfigurationStatus> GetConfigurationStatusAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(Status);
        public Task<TrustedGitHubAppConfigurationStatus> ValidateConfigurationAsync(
            TrustedGitHubAppConfiguration configuration,
            CancellationToken cancellationToken = default) => Task.FromResult(
            new TrustedGitHubAppConfigurationStatus(
                true, configuration.AppId, configuration.Revision, slug, name, null));
        public Task<TrustedGitHubAppConfigurationStatus> ActivateConfigurationAsync(
            TrustedGitHubAppConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            Status = new TrustedGitHubAppConfigurationStatus(
                true, configuration.AppId, configuration.Revision, slug, name, null);
            return Task.FromResult(Status);
        }
        public Task<TrustedInstallationDescriptor> DescribeInstallationAsync(long installationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TrustedRepositoryDescriptor>> ListRepositoriesAsync(long installationId, CancellationToken cancellationToken = default)
        {
            RepositoryInstallationIds.Add(installationId);
            return Task.FromResult(Repositories);
        }
        public Task<TrustedMergeResult> MergeAsync(TrustedMergeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TrustedWorkspaceSnapshot> PrepareWorkspaceAsync(TrustedWorkspaceSnapshotRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TrustedRepositoryProvisioningResult> ProvisionAsync(TrustedRepositoryProvisioningRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoOpAuditWriter : IAuditEventWriter
    {
        public Task WriteAsync(string eventType, string entityType, Guid? entityId, string? summary,
            string? metadataJson = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Guid> AppendAsync(AuditEventWriteRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(Guid.NewGuid());
    }
}
