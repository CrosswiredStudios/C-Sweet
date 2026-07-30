using CSweet.Application.Core;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ApprovalDashboardServiceTests
{
    [Fact]
    public async Task GetAsync_AggregatesPendingApprovalsAndAssignsManagerDecision()
    {
        await using var db = new CSweetDbContext(
            new DbContextOptionsBuilder<CSweetDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        var organizationId = Guid.NewGuid();
        var applicationUserId = Guid.NewGuid();
        var owner = new OrganizationUser
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ApplicationUserId = applicationUserId,
            DisplayName = "Owner",
            EmployeeType = EmployeeType.Human,
            PermissionLevel = OrganizationPermissionLevel.Owner,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var installationId = Guid.NewGuid();
        var productManager = new OrganizationUser
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            AgentInstallationId = installationId,
            ReportsToOrganizationUserId = owner.Id,
            DisplayName = "Product Manager",
            EmployeeType = EmployeeType.Agent,
            PermissionLevel = OrganizationPermissionLevel.Contributor,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var createdAt = DateTimeOffset.UtcNow;
        var resourceChange = new ResourceChangeRequestResponse(
            Guid.NewGuid(),
            organizationId,
            productManager.Id,
            installationId,
            owner.Id,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Technical feasibility spike",
            "Validate the browser experience.",
            2,
            [],
            [],
            [],
            [],
            null,
            "Pending",
            "DeliveredInChat",
            null,
            createdAt,
            null);
        db.AddRange(
            owner,
            productManager,
            new ActionProposal
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                AgentInstallationId = installationId,
                ActionType = "update-business-profile",
                Summary = "Update the product focus.",
                PayloadJson = "{}",
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                Status = ProposalStatus.Pending,
                CreatedAt = createdAt
            },
            new StaffingActionProposal
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                RequestingInstallationId = installationId,
                CandidateId = "candidate:1",
                CandidateSource = "catalog",
                ActionType = "install-and-hire",
                PayloadJson = """{"roleTitle":"Web Game Developer"}""",
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                Status = ProposalStatus.Pending,
                CreatedAt = createdAt
            },
            new Artifact
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                Title = "Product brief",
                Content = "Brief",
                Version = 1,
                ApprovalStatus = ApprovalStatus.Pending,
                CreatedAt = createdAt,
                UpdatedAt = createdAt
            });
        await db.SaveChangesAsync();
        var service = new ApprovalDashboardService(
            db,
            new StubResourceChangeService([resourceChange]));

        var result = await service.GetAsync(
            organizationId,
            applicationUserId);

        Assert.Equal(4, result.PendingCount);
        Assert.Equal(4, result.Items.Count);
        var teamApproval = Assert.Single(
            result.Items,
            x => x.Kind == ApprovalDashboardKinds.ResourceChange);
        Assert.True(teamApproval.CanDecide);
        Assert.Equal("Owner", teamApproval.AssignedTo);
        Assert.Contains(
            result.Items,
            x => x.Kind == ApprovalDashboardKinds.HiringWorkflow &&
                 x.Title.Contains("Web Game Developer"));
    }

    private sealed class StubResourceChangeService(
        IReadOnlyList<ResourceChangeRequestResponse> requests) : IResourceChangeService
    {
        public Task<IReadOnlyList<ResourceChangeRequestResponse>> ListForDashboardAsync(
            Guid organizationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(requests);

        public Task<ResourceChangeRequestResponse> ProposeAsync(
            Guid organizationId,
            Guid requesterInstallationId,
            ResourceChangeProposalRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ResourceChangeReadResponse> ReadForInstallationAsync(
            Guid organizationId,
            Guid installationId,
            ResourceChangeReadRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ResourceChangeRequestResponse> DecideForInstallationAsync(
            Guid organizationId,
            Guid managerInstallationId,
            ResourceChangeDecisionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ResourceChangeRequestResponse> DecideForUserAsync(
            Guid organizationId,
            Guid applicationUserId,
            ResourceChangeDecisionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
