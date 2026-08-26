using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class StaffingReplenishmentServiceTests
{
    [Fact]
    public async Task RepeatedVitalRoleGapCreatesExactlyOnePendingRequest()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var organizationId = Guid.NewGuid();
        var managerId = Guid.NewGuid();
        var managerInstallationId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();
        var requesterInstallationId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var baselineId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.CoreOrganizations.Add(new Organization
        {
            Id = organizationId, Name = "Example", Status = OrganizationStatus.Active,
            CreatedAt = now, UpdatedAt = now
        });
        db.CoreOrganizationUsers.AddRange(
            new OrganizationUser
            {
                Id = managerId, OrganizationId = organizationId,
                AgentInstallationId = managerInstallationId, DisplayName = "Manager",
                EmployeeType = EmployeeType.Agent, IsActive = true, CreatedAt = now
            },
            new OrganizationUser
            {
                Id = requesterId, OrganizationId = organizationId,
                AgentInstallationId = requesterInstallationId, ReportsToOrganizationUserId = managerId,
                DisplayName = "Product Manager", EmployeeType = EmployeeType.Agent,
                IsActive = true, CreatedAt = now
            });
        db.CoreConversations.Add(new Conversation
        {
            Id = conversationId, OrganizationId = organizationId,
            InitiatedByOrganizationUserId = managerId, AgentOrganizationUserId = requesterId,
            Kind = ConversationKind.DirectHumanAgent, CreatedAt = now, UpdatedAt = now,
            Participants =
            [
                new ConversationParticipant
                {
                    Id = Guid.NewGuid(), OrganizationUserId = managerId, JoinedAt = now
                },
                new ConversationParticipant
                {
                    Id = Guid.NewGuid(), OrganizationUserId = requesterId, JoinedAt = now
                }
            ]
        });
        db.ResourceChangeRequests.Add(new ResourceChangeRequestRecord
        {
            Id = baselineId, OrganizationId = organizationId,
            RequesterOrganizationUserId = requesterId, RequesterInstallationId = requesterInstallationId,
            ManagerOrganizationUserId = managerId, ConversationId = conversationId,
            ChatTurnId = Guid.NewGuid(), ConversationMessageId = Guid.NewGuid(),
            ProductGoal = "Ship safely", TeamId = teamId, TeamKey = "software-delivery",
            TeamName = "Software Delivery", Rationale = "Approved delivery team",
            ContextRevision = 1, IdempotencyKey = "approved-team-v1",
            Status = ResourceChangeRequestStatus.Approved, CreatedAt = now, UpdatedAt = now,
            Roles =
            [
                new ResourceChangeRoleRecord
                {
                    Id = Guid.NewGuid(), RoleKey = "software-qa", Team = "Software Delivery",
                    Title = "Software QA", Purpose = "Independent verification", Headcount = 1,
                    Priority = 1, Timing = "Now", IsDesired = true, TeamId = teamId
                }
            ]
        });
        await db.SaveChangesAsync();

        var request = new StaffingReplenishmentProposalRequest(
            baselineId, teamId, conversationId,
            [new StaffingReplenishmentGap("software-qa", "Software QA", 1, 0, 1, ["No eligible active QA principal"])],
            "No sprint may start without independent QA.",
            ["Hold sprint starts", "Preserve executing snapshots"],
            "qa-gap-fingerprint", "qa-gap-idempotency-key");
        var service = new StaffingReplenishmentService(db, new TestAuditEventWriter(), null!);

        var initialVacancy = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ProposeAsync(organizationId, requesterInstallationId, request));
        Assert.Contains("original approved hiring plan", initialVacancy.Message, StringComparison.OrdinalIgnoreCase);
        db.WorkforcePlans.Add(new WorkforcePlan
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId,
            RequestingInstallationId = requesterInstallationId, TeamId = teamId,
            SourceResourceChangeRequestId = baselineId, RoleKey = "software-qa",
            Title = "Software QA", Objective = "Independent verification",
            Headcount = 1, FulfilledHeadcount = 1, Status = ProposalStatus.Approved,
            IdempotencyKey = "initial-software-qa", CreatedAt = now, UpdatedAt = now
        });
        await db.SaveChangesAsync();

        var first = await service.ProposeAsync(organizationId, requesterInstallationId, request);
        var replay = await service.ProposeAsync(organizationId, requesterInstallationId, request);

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal("Pending", first.Status);
        Assert.Single(await db.StaffingReplenishmentRequests.ToListAsync());
        Assert.Single(await db.CoreConversationMessages.ToListAsync());
        Assert.Single(await db.AgentPlatformEventOutbox.ToListAsync());
    }
}
