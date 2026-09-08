using CSweet.Application.Core;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ApprovalDashboardServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectorReviewDisplaysExactChangesAndOnlyOffersDecisionToBoundApprover(bool assigned)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var organizationId = Guid.NewGuid(); var requester = Guid.NewGuid();
        var owner = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organizationId,
            ApplicationUserId = Guid.NewGuid(), DisplayName = "Owner", EmployeeType = EmployeeType.Human,
            PermissionLevel = OrganizationPermissionLevel.Owner };
        var manager = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organizationId,
            ApplicationUserId = Guid.NewGuid(), DisplayName = "Assigned manager", EmployeeType = EmployeeType.Human,
            PermissionLevel = OrganizationPermissionLevel.Manager };
        var binding = new ConnectorActionApprovalService.Binding(Guid.NewGuid(), new string('a', 64), "channel", "video",
            1, "action-key", "example.video.update.v1", true, manager.Id, "Manager Approval", "write",
            DateTimeOffset.UtcNow.AddHours(1), JsonSerializer.SerializeToElement(new { title = "<script>untrusted</script>" }), "Company channel");
        db.AddRange(owner, manager, new ActionProposal { Id = Guid.NewGuid(), OrganizationId = organizationId,
            AgentInstallationId = requester, ActionType = ConnectorActionApprovalService.ActionType,
            PayloadJson = JsonSerializer.Serialize(binding, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Summary = "Review changes", IdempotencyKey = "proposal", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var service = new ApprovalDashboardService(db, new StubResourceChangeService([]), new HiringService(db, null!, null!));
        var result = await service.GetAsync(organizationId, (assigned ? manager : owner).ApplicationUserId!.Value);
        var item = Assert.Single(result.Items);
        Assert.Equal(assigned, item.CanDecide); Assert.Equal(manager.DisplayName, item.AssignedTo);
        Assert.Equal(!assigned, item.CanManageStandingPolicy);
        Assert.Equal("Company channel", item.AgentAction!.AccountName);
        Assert.Equal(binding.ExpiresAt, item.AgentAction.ExpiresAt);
        Assert.Equal(binding.ReviewPayload!.Value.GetProperty("title").GetString(),
            JsonDocument.Parse(item.AgentAction.ReviewPayloadJson!).RootElement.GetProperty("title").GetString());
    }

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
        var candidate = new WorkforceCandidate
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, Source = "CSweetEmbeddedCatalog",
            ExternalCandidateId = Guid.NewGuid().ToString("D"), DisplayName = "Web Game Developer",
            CapabilitiesJson = "[]", ExplanationJson = "{}", Score = .9m, IsAvailable = true
        };
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
            candidate,
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
                CandidateId = $"candidate:{candidate.Id:N}",
                CandidateSource = candidate.Source,
                ActionType = "install-and-hire",
                PayloadJson = $$"""{"roleTitle":"Web Game Developer","employeeDisplayName":"Web Game Developer","reportsToOrganizationUserId":"{{owner.Id:D}}","requiredGrants":[],"embeddedAgent":null}""",
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                Status = ProposalStatus.Pending,
                CreatedAt = createdAt,
                SubmittedAt = createdAt
            },
            new StaffingActionProposal
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId,
                RequestingInstallationId = Guid.Empty,
                CandidateId = $"candidate:{candidate.Id:N}", CandidateSource = candidate.Source,
                ActionType = "marketplace-install-and-hire", PayloadJson = "{}",
                IdempotencyKey = Guid.NewGuid().ToString("N"), Status = ProposalStatus.Pending,
                CreatedAt = createdAt, SubmittedAt = null
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
            new StubResourceChangeService([resourceChange]),
            new HiringService(db, null!, null!));

        var result = await service.GetAsync(
            organizationId,
            applicationUserId);

        Assert.Equal(4, result.PendingCount);
        Assert.Equal(4, result.Items.Count);
        Assert.DoesNotContain(result.Items, item => item.Summary.Contains("draft", StringComparison.OrdinalIgnoreCase));
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
