using CSweet.Application.SourceControl;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.SourceControl;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class RepositoryProvisioningProcessorTests
{
    [Fact]
    public async Task CreatesOnlyCoreResolvedPrivateManagedRepository()
    {
        var organizationId = Guid.NewGuid();
        await using var db = CreateDb();
        var request = SeedReadyRequest(db, organizationId);
        await db.SaveChangesAsync();
        var host = new RecordingProvisioner(new TrustedRepositoryProvisioningResult(
            true, false, 42, "approved-org", "csweet-project", "main"));
        var processor = new RepositoryProvisioningProcessor(db, host, TimeProvider.System);

        Assert.True(await processor.TryProcessNextAsync());

        var repository = await db.SourceControlRepositories.SingleAsync();
        Assert.True(repository.IsPrivate);
        Assert.True(repository.IsManaged);
        Assert.Equal(SourceControlRepositoryStatus.Ready, repository.Status);
        Assert.Equal("approved-org/csweet-project", repository.CanonicalPath);
        Assert.Equal(request.Id, host.LastRequest!.ProvisioningRequestId);
        Assert.Equal("approved-org", host.LastRequest.OrganizationLogin);
        Assert.Equal("approved/template", $"{host.LastRequest.TemplateOwner}/{host.LastRequest.TemplateRepository}");
        Assert.Equal(RepositoryProvisioningStatus.Completed, request.Status);
    }

    [Fact]
    public async Task PersonalAccountOrMissingProvisionerFailsWithoutProviderCall()
    {
        var organizationId = Guid.NewGuid();
        await using var db = CreateDb();
        var request = SeedReadyRequest(db, organizationId);
        request.Connection!.AccountType = "User";
        request.Connection.ProvisionerInstallationId = null;
        await db.SaveChangesAsync();
        var host = new RecordingProvisioner(new TrustedRepositoryProvisioningResult(
            true, false, 42, "wrong", "wrong", "main"));
        var processor = new RepositoryProvisioningProcessor(db, host, TimeProvider.System);

        Assert.True(await processor.TryProcessNextAsync());

        Assert.Null(host.LastRequest);
        Assert.Equal(RepositoryProvisioningStatus.Failed, request.Status);
        Assert.Empty(db.SourceControlRepositories);
    }

    [Fact]
    public async Task PartialProviderConfigurationIsQuarantinedAndNeverDeleted()
    {
        var organizationId = Guid.NewGuid();
        await using var db = CreateDb();
        var request = SeedReadyRequest(db, organizationId);
        await db.SaveChangesAsync();
        var host = new RecordingProvisioner(new TrustedRepositoryProvisioningResult(
            true, true, 42, "approved-org", "csweet-project", "main",
            "branch_protection_failed", "Organization policy rejected the baseline."));
        var processor = new RepositoryProvisioningProcessor(db, host, TimeProvider.System);

        Assert.True(await processor.TryProcessNextAsync());

        var repository = await db.SourceControlRepositories.SingleAsync();
        Assert.Equal(SourceControlRepositoryStatus.AttentionRequired, repository.Status);
        Assert.Equal(RepositoryProvisioningStatus.Quarantined, request.Status);
        Assert.Equal("branch_protection_failed", request.FailureCode);
    }

    private static RepositoryProvisioningRequest SeedReadyRequest(
        CSweetDbContext db,
        Guid organizationId)
    {
        var now = DateTimeOffset.UtcNow;
        var connection = new SourceControlConnection
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, Name = "Managed GitHub",
            Provider = SourceControlProvider.GitHub,
            Mode = SourceControlConnectionMode.ManagedGitHub,
            Status = SourceControlConnectionStatus.Connected,
            ProviderAccountId = "99", AccountLogin = "approved-org", AccountType = "Organization",
            SourceAccessInstallationId = 10, ProvisionerInstallationId = 11,
            CreatedAt = now, UpdatedAt = now
        };
        var policy = new RepositoryProvisioningPolicy
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, ConnectionId = connection.Id,
            NamePrefix = "csweet", NamingPattern = "{prefix}-{slug}",
            MaximumRepositories = 20, IsEnabled = true, Revision = 4,
            CreatedAt = now, UpdatedAt = now
        };
        var template = new SourceControlRepositoryTemplate
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, ConnectionId = connection.Id,
            ExternalRepositoryId = "7", Owner = "approved", Name = "template",
            DisplayName = "Approved template", DefaultBranch = "main", IsEnabled = true,
            CreatedAt = now, UpdatedAt = now
        };
        var request = new RepositoryProvisioningRequest
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, ConnectionId = connection.Id,
            PolicyId = policy.Id, PolicyRevision = policy.Revision,
            TemplateId = template.Id, RequestedByOrganizationUserId = Guid.NewGuid(),
            ProjectDisplayName = "Project", Description = "Private project",
            RepositoryName = "csweet-project", IdempotencyKey = "provision-1",
            Status = RepositoryProvisioningStatus.Pending, CreatedAt = now, UpdatedAt = now,
            Connection = connection, Policy = policy, Template = template
        };
        db.AddRange(connection, policy, template, request);
        return request;
    }

    private static CSweetDbContext CreateDb() => new(
        new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase($"provisioning-{Guid.NewGuid():N}")
            .Options);

    private sealed class RecordingProvisioner(TrustedRepositoryProvisioningResult result)
        : ITrustedProvisioningHostClient
    {
        public TrustedRepositoryProvisioningRequest? LastRequest { get; private set; }

        public Task<TrustedInstallationDescriptor> DescribeInstallationAsync(
            long installationId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TrustedRepositoryDescriptor>> ListRepositoriesAsync(
            long installationId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TrustedRepositoryProvisioningResult> ProvisionAsync(
            TrustedRepositoryProvisioningRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(result);
        }
    }
}
