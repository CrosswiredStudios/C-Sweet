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
    public async Task GuidedSourceAccessFlowBuildsLeastPrivilegeManifestAndActivatesWithoutReturningKey()
    {
        await using var db = CreateDb();
        var privateKeyPem = CreatePrivateKeyPem();
        var sourceHost = new ConfigurableHost("csweet-source", "C-Sweet Source Access");
        var provisionerHost = new ConfigurableHost("csweet-provisioner", "C-Sweet Provisioner");
        var service = CreateService(db, sourceHost, provisionerHost,
            new ManifestClient(new PlatformGitHubManifestConversion(
                1234, "C-Sweet Source Access", "csweet-source", privateKeyPem)));
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
            Assert.Equal("https://example.test/csweet/source-control/github-callback",
                root.GetProperty("setup_url").GetString());
        }
        Assert.DoesNotContain("PRIVATE KEY", launch.ManifestJson, StringComparison.Ordinal);
        var state = HttpUtility.ParseQueryString(new Uri(launch.PostUrl).Query)["state"]!;
        await service.CompleteManifestAsync(userId, "one-time-code", state);

        setup = await service.GetAsync(userId);
        Assert.Equal("source-access-confirm", setup.Session!.CurrentStep);
        Assert.Equal("Verified", setup.Session.SourceAccessApp!.Status);
        var stored = Assert.Single(db.PlatformGitHubAppCredentials);
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
                42, "Source", "source", CreatePrivateKeyPem())));
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
                42, "Source", "source", CreatePrivateKeyPem())));
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
                42, "Source", "source", CreatePrivateKeyPem())));

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
        public Task<IReadOnlyList<TrustedRepositoryDescriptor>> ListRepositoriesAsync(long installationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
