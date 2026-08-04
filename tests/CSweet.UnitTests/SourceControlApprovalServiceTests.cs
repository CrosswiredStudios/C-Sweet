using CSweet.Contracts.SourceControl;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.SourceControl;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class SourceControlApprovalServiceTests
{
    [Fact]
    public async Task ManagerApprovalReleasesDurableProvisioningJob()
    {
        var organizationId = Guid.NewGuid();
        var applicationUserId = Guid.NewGuid();
        await using var db = CreateDb();
        var manager = SeedUser(db, organizationId, applicationUserId, OrganizationPermissionLevel.Manager);
        var provisioning = SeedProvisioning(db, organizationId);
        var approval = SeedApproval(db, organizationId, manager.Id, provisioning.Id);
        await db.SaveChangesAsync();
        var service = new SourceControlApprovalService(db, TimeProvider.System);

        var result = await service.DecideAsync(
            organizationId, applicationUserId, approval.Id,
            new DecideSourceControlApprovalRequest(true, null, approval.Revision));

        Assert.Equal("Approved", result.Status);
        Assert.Equal(RepositoryProvisioningStatus.Pending, provisioning.Status);
        Assert.Equal(ApprovalStatus.Approved, approval.Status);
        Assert.Equal(manager.Id, approval.DecidedByOrganizationUserId);
    }

    [Fact]
    public async Task RejectionRequiresFeedbackAndCancelsWithoutProviderCall()
    {
        var organizationId = Guid.NewGuid();
        var applicationUserId = Guid.NewGuid();
        await using var db = CreateDb();
        var manager = SeedUser(db, organizationId, applicationUserId, OrganizationPermissionLevel.Owner);
        var provisioning = SeedProvisioning(db, organizationId);
        var approval = SeedApproval(db, organizationId, manager.Id, provisioning.Id);
        await db.SaveChangesAsync();
        var service = new SourceControlApprovalService(db, TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentException>(() => service.DecideAsync(
            organizationId, applicationUserId, approval.Id,
            new DecideSourceControlApprovalRequest(false, "", approval.Revision)));
        var result = await service.DecideAsync(
            organizationId, applicationUserId, approval.Id,
            new DecideSourceControlApprovalRequest(false, "Not this quarter.", approval.Revision));

        Assert.Equal("Rejected", result.Status);
        Assert.Equal(RepositoryProvisioningStatus.Cancelled, provisioning.Status);
        Assert.Equal("Not this quarter.", provisioning.FailureMessage);
    }

    private static OrganizationUser SeedUser(
        CSweetDbContext db,
        Guid organizationId,
        Guid applicationUserId,
        OrganizationPermissionLevel permission)
    {
        var user = new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId,
            ApplicationUserId = applicationUserId, DisplayName = "Manager",
            EmployeeType = EmployeeType.Human, PermissionLevel = permission,
            IsActive = true, CreatedAt = DateTimeOffset.UtcNow
        };
        db.CoreOrganizationUsers.Add(user);
        return user;
    }

    private static RepositoryProvisioningRequest SeedProvisioning(
        CSweetDbContext db,
        Guid organizationId)
    {
        var now = DateTimeOffset.UtcNow;
        var request = new RepositoryProvisioningRequest
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId,
            ConnectionId = Guid.NewGuid(), PolicyId = Guid.NewGuid(), TemplateId = Guid.NewGuid(),
            RequestedByOrganizationUserId = Guid.NewGuid(), ProjectDisplayName = "Project",
            Description = "Description", RepositoryName = "project", IdempotencyKey = "project-once",
            Status = RepositoryProvisioningStatus.AwaitingApproval,
            CreatedAt = now, UpdatedAt = now
        };
        db.RepositoryProvisioningRequests.Add(request);
        return request;
    }

    private static SourceControlApproval SeedApproval(
        CSweetDbContext db,
        Guid organizationId,
        Guid requesterId,
        Guid provisioningRequestId)
    {
        var now = DateTimeOffset.UtcNow;
        var approval = new SourceControlApproval
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId,
            Kind = SourceControlApprovalKind.RepositoryProvisioning,
            Status = ApprovalStatus.Pending,
            RequestedByOrganizationUserId = requesterId,
            ProvisioningRequestId = provisioningRequestId,
            IdempotencyKey = $"approval:{provisioningRequestId:N}",
            CreatedAt = now, UpdatedAt = now
        };
        db.SourceControlApprovals.Add(approval);
        return approval;
    }

    private static CSweetDbContext CreateDb() => new(
        new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase($"source-control-approval-{Guid.NewGuid():N}")
            .Options);
}
