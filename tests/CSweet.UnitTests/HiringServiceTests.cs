using System.Text.Json;
using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Core;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class HiringServiceTests
{
    [Fact]
    public async Task SourceLinkedRecommendation_RequiresTheApprovedPlansRawRoleKey()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var organizationId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        const string roleKey = "gameplay-engineer";
        db.CoreOrganizations.Add(new Organization
        {
            Id = organizationId,
            Name = "Example",
            CreatedAt = now,
            UpdatedAt = now
        });
        db.ResourceChangeRequests.Add(new ResourceChangeRequestRecord
        {
            Id = requestId,
            OrganizationId = organizationId,
            RequesterOrganizationUserId = Guid.NewGuid(),
            RequesterInstallationId = Guid.NewGuid(),
            ManagerOrganizationUserId = Guid.NewGuid(),
            ConversationId = Guid.NewGuid(),
            ChatTurnId = Guid.NewGuid(),
            ConversationMessageId = Guid.NewGuid(),
            ProductGoal = "Validate gameplay",
            Rationale = "Add the smallest delivery team.",
            IdempotencyKey = "approved-plan",
            Status = ResourceChangeRequestStatus.Approved,
            DeliveryStatus = "Delivered",
            CreatedAt = now,
            UpdatedAt = now,
            DecidedAt = now,
            Roles =
            [
                new ResourceChangeRoleRecord
                {
                    Id = Guid.NewGuid(),
                    ResourceChangeRequestId = requestId,
                    RoleKey = roleKey,
                    Team = "Product",
                    Title = "Gameplay Engineer",
                    Purpose = "Build the validation prototype.",
                    Headcount = 1,
                    Priority = 1,
                    Timing = "Now",
                    ChangeKind = "Add",
                    IsDesired = true
                }
            ]
        });
        await db.SaveChangesAsync();
        var service = new HiringService(
            db,
            new OrganizationUserService(db, new TestAuditEventWriter()),
            new TestAuditEventWriter());
        var chiefInstallationId = Guid.NewGuid();

        var recommendation = await service.UpsertRecommendationAsync(
            organizationId,
            chiefInstallationId,
            new UpsertHiringRecommendationRequest(
                "Gameplay Engineer", "Build the validation prototype.", null, [], null, "source-linked")
            {
                RoleKey = roleKey,
                SourceResourceChangeRequestId = requestId
            });

        Assert.Equal(roleKey, recommendation.RoleKey);
        Assert.Equal(requestId, recommendation.SourceResourceChangeRequestId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpsertRecommendationAsync(
            organizationId,
            chiefInstallationId,
            new UpsertHiringRecommendationRequest(
                "Gameplay Engineer", "Build the validation prototype.", null, [], null, "prefixed-role")
            {
                RoleKey = $"{Guid.NewGuid():N}:{roleKey}",
                SourceResourceChangeRequestId = requestId
            }));
    }

    [Fact]
    public async Task MultiSeatRecommendation_EmitsFulfillmentOnlyAfterEveryUniqueHire()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var organizationId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var sourceRequestId = Guid.NewGuid();
        var first = new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, DisplayName = "First QA",
            EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Contributor,
            IsActive = true, CreatedAt = now
        };
        var second = new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, DisplayName = "Second QA",
            EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Contributor,
            IsActive = true, CreatedAt = now
        };
        db.CoreOrganizations.Add(new Organization
        {
            Id = organizationId, Name = "Example", CreatedAt = now, UpdatedAt = now
        });
        db.CoreOrganizationUsers.AddRange(first, second);
        await db.SaveChangesAsync();
        var service = new HiringService(
            db,
            new OrganizationUserService(db, new TestAuditEventWriter()),
            new TestAuditEventWriter());
        var recommendation = await service.UpsertRecommendationAsync(
            organizationId,
            installationId,
            new UpsertHiringRecommendationRequest("QA Engineer", "Own release quality", null, [], null, "qa-two")
            {
                Headcount = 2,
                RoleKey = "product:qa"
            });
        var plan = await db.WorkforcePlans.SingleAsync(x => x.Id == recommendation.Id);
        plan.SourceResourceChangeRequestId = sourceRequestId;
        await db.SaveChangesAsync();

        var partial = await service.ResolveRecommendationAsync(
            organizationId,
            installationId,
            new ResolveHiringRecommendationRequest(recommendation.Id, first.Id, "first-seat"));
        var duplicate = await service.ResolveRecommendationAsync(
            organizationId,
            installationId,
            new ResolveHiringRecommendationRequest(recommendation.Id, first.Id, "duplicate-employee"));

        Assert.Equal("Pending", partial.Status);
        Assert.Equal(1, partial.FulfilledHeadcount);
        Assert.Equal(1, partial.RemainingHeadcount);
        Assert.Equal(1, duplicate.FulfilledHeadcount);
        Assert.Empty(await db.AgentPlatformEventOutbox.Where(x =>
            x.EventType == HiringEvents.RecommendationFulfilled).ToListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveRecommendationAsync(
            organizationId,
            installationId,
            new ResolveHiringRecommendationRequest(recommendation.Id, second.Id, "first-seat")));

        var fulfilled = await service.ResolveRecommendationAsync(
            organizationId,
            installationId,
            new ResolveHiringRecommendationRequest(recommendation.Id, second.Id, "second-seat"));
        var replay = await service.ResolveRecommendationAsync(
            organizationId,
            installationId,
            new ResolveHiringRecommendationRequest(recommendation.Id, second.Id, "second-seat"));

        Assert.Equal("Approved", fulfilled.Status);
        Assert.Equal(2, fulfilled.FulfilledHeadcount);
        Assert.Equal(0, fulfilled.RemainingHeadcount);
        Assert.Equal(2, replay.FulfilledHeadcount);
        Assert.Equal(2, await db.HiringRecommendationFulfillments.CountAsync());
        var outbox = Assert.Single(await db.AgentPlatformEventOutbox.Where(x =>
            x.EventType == HiringEvents.RecommendationFulfilled).ToListAsync());
        var payload = JsonSerializer.Deserialize<HiringRecommendationFulfilledEvent>(
            outbox.DataJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(payload);
        Assert.Equal(sourceRequestId, payload.SourceResourceChangeRequestId);
        Assert.Equal(new[] { first.Id, second.Id }.Order().ToArray(), payload.ResultOrganizationUserIds.Order().ToArray());
    }

    [Fact]
    public async Task LinkedRecommendation_CarriesTeamSnapshotAndAssignsCompletedHire()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var organizationId = Guid.NewGuid();
        var applicationUserId = Guid.NewGuid();
        var manager = new OrganizationUser
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ApplicationUserId = applicationUserId,
            DisplayName = "Manager",
            EmployeeType = EmployeeType.Human,
            PermissionLevel = OrganizationPermissionLevel.Manager,
            IsActive = true,
            CreatedAt = now
        };
        var hired = new OrganizationUser
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            DisplayName = "Contractor",
            EmployeeType = EmployeeType.Human,
            PermissionLevel = OrganizationPermissionLevel.Contributor,
            IsActive = true,
            CreatedAt = now
        };
        db.CoreOrganizations.Add(new Organization
        {
            Id = organizationId,
            Name = "Example",
            CreatedAt = now,
            UpdatedAt = now
        });
        db.CoreOrganizationUsers.AddRange(manager, hired);
        await db.SaveChangesAsync();
        var teamService = new TeamService(db, new TestAuditEventWriter(), TimeProvider.System);
        var team = (await teamService.CreateAsync(
            organizationId,
            applicationUserId,
            new CreateTeamRequest("Delivery", null, manager.Id))).Team;
        var service = new HiringService(
            db,
            new OrganizationUserService(db, new TestAuditEventWriter()),
            new TestAuditEventWriter(),
            teams: teamService);

        var recommendation = await service.UpsertRecommendationAsync(
            organizationId,
            Guid.NewGuid(),
            new UpsertHiringRecommendationRequest(
                "QA contractor",
                "Own release evidence",
                null,
                [],
                null,
                "team-hire")
            {
                TeamId = team.Id
            });
        await service.ResolveRecommendationAsync(
            organizationId,
            (await db.WorkforcePlans.SingleAsync()).RequestingInstallationId,
            new ResolveHiringRecommendationRequest(recommendation.Id, hired.Id, "completed"));

        Assert.Equal(team.Id, recommendation.TeamId);
        Assert.Contains(await db.TeamMemberships.ToListAsync(), membership =>
            membership.TeamId == team.Id &&
            membership.OrganizationUserId == hired.Id &&
            membership.EndedAt is null);
    }

    [Fact]
    public async Task RoleBacklog_IsPrioritizedAndScopedToRequestingInstallation()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var organizationId = Guid.NewGuid();
        var firstInstallationId = Guid.NewGuid();
        var secondInstallationId = Guid.NewGuid();
        db.CoreOrganizations.Add(new Organization
        {
            Id = organizationId, Name = "Example", CreatedAt = now, UpdatedAt = now
        });
        await db.SaveChangesAsync();
        var service = new HiringService(db, new OrganizationUserService(db, new TestAuditEventWriter()), new TestAuditEventWriter());

        await service.UpsertRecommendationAsync(organizationId, firstInstallationId,
            new("QA Engineer", "Own release quality", null, [], null, "qa-role") { Priority = 20 });
        await service.UpsertRecommendationAsync(organizationId, firstInstallationId,
            new("Product Manager", "Own product definition", null, [], null, "pm-role") { Priority = 1 });
        await service.UpsertRecommendationAsync(organizationId, secondInstallationId,
            new("Sales Lead", "Own revenue", null, [], null, "sales-role") { Priority = 1 });

        var backlog = await service.ListRecommendationsForInstallationAsync(organizationId, firstInstallationId);

        Assert.Collection(backlog,
            item =>
            {
                Assert.Equal("Product Manager", item.Title);
                Assert.Equal(1, item.Priority);
                Assert.Empty(item.Candidates);
                Assert.Equal(
                    $"/organizations/{organizationId:D}/marketplace?role=Product%20Manager&recommendationId={item.Id:D}",
                    item.HiringUrl);
            },
            item => { Assert.Equal("QA Engineer", item.Title); Assert.Equal(20, item.Priority); Assert.Empty(item.Candidates); });

        var hired = new OrganizationUser
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            DisplayName = "Product Manager",
            EmployeeType = EmployeeType.Agent,
            PermissionLevel = OrganizationPermissionLevel.Contributor,
            CreatedAt = now
        };
        db.CoreOrganizationUsers.Add(hired);
        await db.SaveChangesAsync();
        var productManager = backlog[0];
        await Assert.ThrowsAsync<ArgumentException>(() => service.ResolveRecommendationAsync(
            organizationId,
            secondInstallationId,
            new(productManager.Id, hired.Id, "wrong-owner")));
        await service.ResolveRecommendationAsync(
            organizationId,
            firstInstallationId,
            new(productManager.Id, hired.Id, "employee-hired"));
        Assert.DoesNotContain(
            await service.ListRecommendationsForInstallationAsync(organizationId, firstInstallationId),
            x => x.Id == productManager.Id);
    }

    [Fact]
    public async Task CurrentStaffWorkflow_RequiresOwnerAndAssignsApprovedRole()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow; var organizationId = Guid.NewGuid(); var applicationUserId = Guid.NewGuid();
        var installationId = Guid.NewGuid(); var workerId = Guid.NewGuid();
        var owner = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organizationId, ApplicationUserId = applicationUserId,
            DisplayName = "Owner", EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner, CreatedAt = now };
        var employee = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organizationId, WorkerId = workerId,
            DisplayName = "Alex", EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Contributor, CreatedAt = now };
        db.CoreOrganizations.Add(new Organization { Id = organizationId, Name = "Example", CreatedAt = now, UpdatedAt = now });
        db.CoreOrganizationUsers.AddRange(owner, employee);
        db.CoreWorkers.Add(new Worker { Id = workerId, OrganizationId = organizationId, Name = "Alex", WorkerType = WorkerType.Human,
            CapabilitiesJson = "[\"operations\"]", IsEnabled = true, CreatedAt = now, UpdatedAt = now });
        var candidate = new WorkforceCandidate { Id = Guid.NewGuid(), OrganizationId = organizationId, Source = "CurrentStaff",
            ExternalCandidateId = workerId.ToString(), DisplayName = "Alex", CapabilitiesJson = "[\"operations\"]",
            Score = .95m, IsHuman = true, IsAvailable = true, ExplanationJson = "{}" };
        db.WorkforceCandidates.Add(candidate);
        await db.SaveChangesAsync();
        var service = new HiringService(db, new OrganizationUserService(db, new TestAuditEventWriter()), new TestAuditEventWriter());
        var recommendation = await service.UpsertRecommendationAsync(organizationId, installationId,
            new("Operations lead", "Own reliable delivery", null, [$"candidate:{candidate.Id:N}"], $"candidate:{candidate.Id:N}", "rec-1"));
        var origin = await AddHiringOriginAsync(db, organizationId, owner, installationId);
        var workflow = await service.StageWorkflowAsync(organizationId, installationId,
            new(recommendation.Id, recommendation.RecommendedCandidateReference!, "Operations Lead", null, [], "workflow-1")
            {
                ConversationId = origin.ConversationId,
                ChatTurnId = origin.ChatTurnId
            });

        var rejected = await service.DecideWorkflowAsync(
            organizationId,
            workflow.Id,
            applicationUserId,
            new(HiringWorkflowDecisionKinds.Reject, "Use the existing reporting structure.", "reject-workflow-1"));
        Assert.Equal("Rejected", rejected?.Status);
        Assert.Equal("Use the existing reporting structure.",
            (await db.StaffingActionProposals.SingleAsync(item => item.Id == workflow.Id)).DecisionComment);
        Assert.Null((await db.CoreOrganizationUsers.SingleAsync(x => x.Id == employee.Id)).RoleId);

        workflow = await service.StageWorkflowAsync(organizationId, installationId,
            new(recommendation.Id, recommendation.RecommendedCandidateReference!, "Operations Lead", null, [], "workflow-2")
            {
                ConversationId = origin.ConversationId,
                ChatTurnId = origin.ChatTurnId
            });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ConfirmWorkflowAsync(organizationId, workflow.Id,
            Guid.NewGuid(), new("not-owner")));
        var approved = await service.ConfirmWorkflowAsync(organizationId, workflow.Id, applicationUserId, new("owner-approval"));

        Assert.Equal("Approved", approved?.Status);
        var updated = await db.CoreOrganizationUsers.SingleAsync(x => x.Id == employee.Id);
        Assert.Equal("Operations Lead", (await db.CoreRoles.SingleAsync(x => x.Id == updated.RoleId)).Name);
    }

    [Fact]
    public async Task EmbeddedAgentWorkflow_PreviewsPinsInstallsAndHiresConfiguredRepository()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var organizationId = Guid.NewGuid();
        var applicationUserId = Guid.NewGuid();
        var chiefInstallationId = Guid.NewGuid();
        var repositoryUrl = "https://github.com/CrosswiredStudios/CSweet.Agent.ProductManager";
        db.CoreOrganizations.Add(new Organization
        {
            Id = organizationId,
            Name = "Product Company",
            CreatedAt = now,
            UpdatedAt = now
        });
        db.CoreOrganizationUsers.Add(new OrganizationUser
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ApplicationUserId = applicationUserId,
            DisplayName = "Owner",
            EmployeeType = EmployeeType.Human,
            PermissionLevel = OrganizationPermissionLevel.Owner,
            CreatedAt = now
        });
        var candidate = new WorkforceCandidate
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Source = "CSweetEmbeddedCatalog",
            ExternalCandidateId = Guid.NewGuid().ToString("D"),
            DisplayName = "Product Manager",
            CapabilitiesJson = "[\"product.strategy\",\"product.discovery\",\"product.roadmap\"]",
            Score = .95m,
            IsAvailable = true,
            ExplanationJson = JsonSerializer.Serialize(new { repositoryUrl })
        };
        db.WorkforceCandidates.Add(candidate);
        await db.SaveChangesAsync();
        var ownerId = await db.CoreOrganizationUsers
            .Where(user => user.OrganizationId == organizationId)
            .Select(user => user.Id)
            .SingleAsync();

        var preview = new RecordingImportPreview(repositoryUrl);
        var installations = new RecordingInstallationService(organizationId);
        var organizationUsers = new RecordingOrganizationUserService();
        var service = new HiringService(
            db,
            organizationUsers,
            new TestAuditEventWriter(),
            preview,
            installations);
        var recommendation = await service.UpsertRecommendationAsync(
            organizationId,
            chiefInstallationId,
            new(
                "Product Manager",
                "Own customer discovery and product outcomes",
                null,
                [$"candidate:{candidate.Id:N}"],
                $"candidate:{candidate.Id:N}",
                "product-manager-first-hire")
            {
                Priority = 1
            });
        var owner = await db.CoreOrganizationUsers.SingleAsync(user => user.Id == ownerId);
        var origin = await AddHiringOriginAsync(db, organizationId, owner, chiefInstallationId);
        await Assert.ThrowsAsync<ArgumentException>(() => service.StageWorkflowAsync(
            organizationId,
            chiefInstallationId,
            new(
                recommendation.Id,
                recommendation.RecommendedCandidateReference!,
                "Product Manager",
                null,
                [],
                "install-product-manager-without-manager")));
        var workflow = await service.StageWorkflowAsync(
            organizationId,
            chiefInstallationId,
            new(
                recommendation.Id,
                recommendation.RecommendedCandidateReference!,
                "Product Manager",
                ownerId,
                [],
                "install-product-manager")
            {
                ConversationId = origin.ConversationId,
                ChatTurnId = origin.ChatTurnId
            });

        var approved = await service.ConfirmWorkflowAsync(
            organizationId,
            workflow.Id,
            applicationUserId,
            new("approve-product-manager"));
        var duplicate = await service.ConfirmWorkflowAsync(
            organizationId,
            workflow.Id,
            applicationUserId,
            new("approve-product-manager-retry"));

        Assert.Equal("Approved", approved?.Status);
        Assert.Equal(approved, duplicate);
        Assert.Equal(2, preview.Requests.Count);
        Assert.Equal(repositoryUrl, preview.Requests[0].RepositoryUrl);
        Assert.Equal(preview.CommitSha, preview.Requests[1].Ref);
        Assert.NotNull(installations.Request);
        Assert.Equal(1, installations.InstallCount);
        Assert.Equal(organizationId.ToString("D"), installations.Request!.BusinessId);
        Assert.Equal(preview.RequestedCapabilities, installations.Request.GrantedRequestedCapabilities);
        Assert.Equal(preview.ProvidedCapabilities, installations.Request.GrantedCapabilities);
        Assert.Equal(installations.InstallationId, organizationUsers.CreatedRequest?.AgentInstallationId);
        Assert.Equal("C-Sweet Product Manager", organizationUsers.CreatedRequest?.DisplayName);
    }

    [Fact]
    public async Task MarketplacePreview_LinksAndValidatesPendingRecommendation()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var organizationId = Guid.NewGuid();
        var foreignOrganizationId = Guid.NewGuid();
        var applicationUserId = Guid.NewGuid();
        var owner = new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, ApplicationUserId = applicationUserId,
            DisplayName = "Owner", EmployeeType = EmployeeType.Human,
            PermissionLevel = OrganizationPermissionLevel.Owner, IsActive = true, CreatedAt = now
        };
        var productManager = new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId,
            DisplayName = "Product manager", EmployeeType = EmployeeType.Agent,
            PermissionLevel = OrganizationPermissionLevel.Manager, IsActive = true, CreatedAt = now
        };
        var approvedRequestId = Guid.NewGuid();
        const string approvedRoleKey = "product-manager-hire";
        db.CoreOrganizations.Add(new Organization
        {
            Id = organizationId, Name = "Example", CreatedAt = now, UpdatedAt = now
        });
        db.CoreOrganizations.Add(new Organization
        {
            Id = foreignOrganizationId, Name = "Other Example", CreatedAt = now, UpdatedAt = now
        });
        db.CoreOrganizationUsers.AddRange(owner, productManager);
        db.ResourceChangeRequests.Add(new ResourceChangeRequestRecord
        {
            Id = approvedRequestId,
            OrganizationId = organizationId,
            RequesterOrganizationUserId = productManager.Id,
            RequesterInstallationId = Guid.NewGuid(),
            ManagerOrganizationUserId = owner.Id,
            ConversationId = Guid.NewGuid(),
            ChatTurnId = Guid.NewGuid(),
            ConversationMessageId = Guid.NewGuid(),
            ProductGoal = "Expand the product team",
            Rationale = "Add the approved product role.",
            IdempotencyKey = "approved-product-hire",
            Status = ResourceChangeRequestStatus.Approved,
            DeliveryStatus = "Delivered",
            CreatedAt = now,
            UpdatedAt = now,
            DecidedAt = now,
            Roles =
            [
                new ResourceChangeRoleRecord
                {
                    Id = Guid.NewGuid(),
                    ResourceChangeRequestId = approvedRequestId,
                    RoleKey = approvedRoleKey,
                    Team = "Product",
                    Title = "Product Manager",
                    Purpose = "Own product outcomes.",
                    Headcount = 1,
                    Priority = 1,
                    Timing = "Now",
                    ReportsToOrganizationUserId = productManager.Id,
                    ChangeKind = "Add",
                    IsDesired = true
                }
            ]
        });
        await db.SaveChangesAsync();
        var repositoryUrl = "https://github.com/CrosswiredStudios/CSweet.Agent.ProductManager";
        var available = new CSweet.Agent.SDK.AvailableAgent(
            "first-party:product-manager", "com.csweet.product-manager",
            CSweet.Agent.SDK.AgentCatalogSource.FirstPartyCatalog, [],
            CSweet.Agent.SDK.AgentAvailabilityState.AvailableToInstall, null,
            "C-Sweet Product Manager", "Own product outcomes.", "C-Sweet", "Product",
            ["Product Manager"], ["product"], ["product.strategy"], null, null, null, 0,
            null, repositoryUrl, .99m, "First-party verified");
        var service = new HiringService(
            db,
            new RecordingOrganizationUserService(),
            new TestAuditEventWriter(),
            new RecordingImportPreview(repositoryUrl),
            new RecordingInstallationService(organizationId),
            new RecordingAgentCatalog(available));
        var recommendation = await service.UpsertRecommendationAsync(
            organizationId,
            Guid.NewGuid(),
            new UpsertHiringRecommendationRequest(
                "Product Manager", "Own product outcomes", null, [], null, "linked-marketplace")
            {
                RoleKey = approvedRoleKey,
                SourceResourceChangeRequestId = approvedRequestId
            });
        var foreignRecommendation = await service.UpsertRecommendationAsync(
            foreignOrganizationId,
            Guid.NewGuid(),
            new UpsertHiringRecommendationRequest(
                "Product Manager", "Own product outcomes", null, [], null, "foreign-marketplace"));

        await Assert.ThrowsAsync<ArgumentException>(() => service.PreviewMarketplaceHireAsync(
            organizationId,
            applicationUserId,
            new PreviewMarketplaceHireRequest(
                available.AgentReference, "Product Manager", "Avery", owner.Id, "invalid-recommendation")
            {
                RecommendationId = Guid.NewGuid()
            }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.PreviewMarketplaceHireAsync(
            organizationId,
            applicationUserId,
            new PreviewMarketplaceHireRequest(
                available.AgentReference, "Product Manager", "Avery", owner.Id, "foreign-recommendation")
            {
                RecommendationId = foreignRecommendation.Id
            }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PreviewMarketplaceHireAsync(
            organizationId,
            applicationUserId,
            new PreviewMarketplaceHireRequest(
                available.AgentReference, "Product Manager", "Avery", owner.Id, "wrong-team")
            {
                RecommendationId = recommendation.Id,
                TeamId = Guid.NewGuid()
            }));
        var overriddenPreview = await service.PreviewMarketplaceHireAsync(
            organizationId,
            applicationUserId,
            new PreviewMarketplaceHireRequest(
                available.AgentReference, "Project Manager", "Avery", owner.Id, "overridden-role")
            {
                RecommendationId = recommendation.Id
            });
        Assert.Equal("Project Manager", overriddenPreview.RoleTitle);
        var overriddenWorkflow = await db.StaffingActionProposals
            .SingleAsync(x => x.Id == overriddenPreview.WorkflowId);
        Assert.Equal(recommendation.Id, overriddenWorkflow.WorkforcePlanId);
        Assert.Equal("Product Manager", (await db.WorkforcePlans.SingleAsync(x => x.Id == recommendation.Id)).Title);
        var preview = await service.PreviewMarketplaceHireAsync(
            organizationId,
            applicationUserId,
            new PreviewMarketplaceHireRequest(
                available.AgentReference, "Product Manager", "Avery", owner.Id, "linked-preview")
            {
                RecommendationId = recommendation.Id
            });

        Assert.Equal(productManager.Id, preview.ReportsToOrganizationUserId);

        var workflow = await db.StaffingActionProposals.SingleAsync(x => x.Id == preview.WorkflowId);
        var linkedCandidateId = Guid.Parse(workflow.CandidateId[10..]);
        var candidate = await db.WorkforceCandidates.SingleAsync(x => x.Id == linkedCandidateId);
        Assert.Equal(recommendation.Id, workflow.WorkforcePlanId);
        Assert.Equal(recommendation.Id, candidate.WorkforcePlanId);
        var anotherRecommendation = await service.UpsertRecommendationAsync(
            organizationId,
            Guid.NewGuid(),
            new UpsertHiringRecommendationRequest(
                "Product Manager", "Own another product outcome", null, [], null, "another-marketplace"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewMarketplaceHireAsync(
            organizationId,
            applicationUserId,
            new PreviewMarketplaceHireRequest(
                available.AgentReference, "Product Manager", "Avery", owner.Id, "linked-preview")
            {
                RecommendationId = anotherRecommendation.Id
            }));

        var completedRecommendation = await db.WorkforcePlans.SingleAsync(x => x.Id == recommendation.Id);
        completedRecommendation.Status = ProposalStatus.Approved;
        completedRecommendation.FulfilledHeadcount = completedRecommendation.Headcount;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => service.PreviewMarketplaceHireAsync(
            organizationId,
            applicationUserId,
            new PreviewMarketplaceHireRequest(
                available.AgentReference, "Product Manager", "Avery", owner.Id, "completed-recommendation")
            {
                RecommendationId = recommendation.Id
            }));
    }

    [Fact]
    public async Task MarketplacePreview_UsesCatalogImportAndSharedConfirmationOrchestrator()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var organizationId = Guid.NewGuid();
        var applicationUserId = Guid.NewGuid();
        db.CoreOrganizations.Add(new Organization
        {
            Id = organizationId, Name = "Product Company", CreatedAt = now, UpdatedAt = now
        });
        db.CoreOrganizationUsers.Add(new OrganizationUser
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ApplicationUserId = applicationUserId,
            DisplayName = "Owner",
            EmployeeType = EmployeeType.Human,
            PermissionLevel = OrganizationPermissionLevel.Owner,
            CreatedAt = now
        });
        await db.SaveChangesAsync();
        var ownerId = await db.CoreOrganizationUsers
            .Where(user => user.OrganizationId == organizationId)
            .Select(user => user.Id)
            .SingleAsync();
        var repositoryUrl = "https://github.com/CrosswiredStudios/CSweet.Agent.ProductManager";
        var agent = new CSweet.Agent.SDK.AvailableAgent(
            "first-party:product-manager",
            "com.csweet.product-manager",
            CSweet.Agent.SDK.AgentCatalogSource.FirstPartyCatalog,
            [],
            CSweet.Agent.SDK.AgentAvailabilityState.AvailableToInstall,
            null,
            "C-Sweet Product Manager",
            "Own product outcomes.",
            "C-Sweet",
            "Product",
            ["Product Manager"],
            ["product"],
            ["product.strategy"],
            null,
            null,
            null,
            0,
            null,
            repositoryUrl,
            .99m,
            "First-party verified");
        var import = new RecordingImportPreview(repositoryUrl);
        var installations = new RecordingInstallationService(organizationId);
        var organizationUsers = new RecordingOrganizationUserService();
        var service = new HiringService(
            db,
            organizationUsers,
            new TestAuditEventWriter(),
            import,
            installations,
            new RecordingAgentCatalog(agent));
        IAgentHireOrchestrator orchestrator = service;

        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.PreviewAsync(
            organizationId,
            applicationUserId,
            new("first-party:product-manager", "Product Manager", "   ", ownerId, "marketplace-preview-blank-name")));
        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.PreviewAsync(
            organizationId,
            applicationUserId,
            new("first-party:product-manager", "Product Manager", new string('a', 161), ownerId, "marketplace-preview-long-name")));
        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.PreviewAsync(
            organizationId,
            applicationUserId,
            new("first-party:product-manager", "Product Manager", "Avery", null, "marketplace-preview-without-manager")));
        var preview = await orchestrator.PreviewAsync(
            organizationId,
            applicationUserId,
            new("first-party:product-manager", "Product Manager", "  Avery  ", ownerId, "marketplace-preview"));
        var confirmed = await orchestrator.ConfirmAsync(
            organizationId,
            preview.WorkflowId,
            applicationUserId,
            new("marketplace-confirm")
            {
                ConfigurationSettings = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString("D")),
                    ["llmModel"] = JsonSerializer.SerializeToElement("test-model"),
                    ["responseTone"] = JsonSerializer.SerializeToElement("concise")
                }
            });
        var duplicate = await orchestrator.ConfirmAsync(
            organizationId,
            preview.WorkflowId,
            applicationUserId,
            new("marketplace-confirm-retry"));

        Assert.Equal("Pending", preview.Status);
        Assert.Equal("Approved", confirmed?.Status);
        Assert.Equal(confirmed, duplicate);
        Assert.Equal(Guid.Empty, confirmed?.RecommendationId);
        Assert.False(confirmed?.ResultAgentRequiresSetup);
        Assert.Equal(2, import.Requests.Count);
        Assert.Equal(3, preview.ConfigurationFields.Count);
        var tone = preview.ConfigurationFields.Single(field => field.Key == "responseTone");
        Assert.Equal("Controls response detail.", tone.Description);
        Assert.Equal(["concise", "balanced", "detailed"], tone.Options!.Select(option => option.Value).ToArray());
        Assert.Equal("Avery", preview.EmployeeDisplayName);
        Assert.Equal(1, installations.InstallCount);
        Assert.Equal(1024, installations.Request!.MemoryMb);
        Assert.Equal("test-model", installations.Request!.ConfigurationSettings["llmModel"].GetString());
        Assert.Equal("Avery", organizationUsers.CreatedRequest?.DisplayName);
        Assert.Equal(installations.InstallationId, organizationUsers.CreatedRequest?.AgentInstallationId);
        Assert.Equal(1, organizationUsers.CreateCount);
    }

    [Fact]
    public async Task MarketplacePreview_UsesAliasForAlreadyInstalledAgent()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
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
            CreatedAt = now
        };
        var package = new AgentPackageVersion
        {
            Id = Guid.NewGuid(),
            AgentId = "com.csweet.product-manager",
            AgentName = "C-Sweet Product Manager",
            Version = "1.4.0",
            PublisherId = "com.csweet",
            PublisherName = "C-Sweet",
            ManifestDigest = new string('b', 64),
            RuntimeType = "dotnet-project",
            ImportedAt = now
        };
        var installationId = Guid.NewGuid();
        db.CoreOrganizations.Add(new Organization
        {
            Id = organizationId, Name = "Product Company", CreatedAt = now, UpdatedAt = now
        });
        db.CoreOrganizationUsers.Add(owner);
        db.AgentInstallations.Add(new AgentInstallation
        {
            Id = installationId,
            InstallationKey = Guid.NewGuid(),
            PackageVersionId = package.Id,
            PackageVersion = package,
            BusinessId = organizationId.ToString("D"),
            CreatedAt = now,
            UpdatedAt = now,
            Grant = new AgentInstallationGrant
            {
                Id = Guid.NewGuid(),
                AgentInstallationId = installationId,
                ApprovedAt = now
            }
        });
        await db.SaveChangesAsync();
        var agent = new CSweet.Agent.SDK.AvailableAgent(
            $"installed:{installationId:D}",
            package.AgentId,
            CSweet.Agent.SDK.AgentCatalogSource.Installed,
            [],
            CSweet.Agent.SDK.AgentAvailabilityState.InstalledEnabled,
            installationId,
            package.AgentName,
            "Own product outcomes.",
            package.PublisherName,
            "Product",
            ["Product Manager"],
            ["product"],
            ["product.strategy"],
            null,
            null,
            null,
            0,
            null,
            null,
            1m,
            "Installed");
        var organizationUsers = new RecordingOrganizationUserService();
        IAgentHireOrchestrator orchestrator = new HiringService(
            db,
            organizationUsers,
            new TestAuditEventWriter(),
            agentCatalog: new RecordingAgentCatalog(agent));

        var preview = await orchestrator.PreviewAsync(
            organizationId,
            applicationUserId,
            new(agent.AgentReference, "Product Manager", "Riley", owner.Id, "marketplace-installed-preview"));
        var confirmed = await orchestrator.ConfirmAsync(
            organizationId,
            preview.WorkflowId,
            applicationUserId,
            new("marketplace-installed-confirm"));
        var duplicate = await orchestrator.ConfirmAsync(
            organizationId,
            preview.WorkflowId,
            applicationUserId,
            new("marketplace-installed-confirm-retry"));

        Assert.Equal("C-Sweet Product Manager", preview.AgentName);
        Assert.Equal("Riley", preview.EmployeeDisplayName);
        Assert.Equal("Approved", confirmed?.Status);
        Assert.Equal(confirmed, duplicate);
        Assert.Equal("Riley", organizationUsers.CreatedRequest?.DisplayName);
        Assert.Equal(installationId, organizationUsers.CreatedRequest?.AgentInstallationId);
        Assert.Equal(1, organizationUsers.CreateCount);
    }

    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(Guid ConversationId, Guid ChatTurnId)> AddHiringOriginAsync(
        CSweetDbContext db,
        Guid organizationId,
        OrganizationUser owner,
        Guid installationId)
    {
        var now = DateTimeOffset.UtcNow;
        var requester = new OrganizationUser
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            AgentInstallationId = installationId,
            ReportsToOrganizationUserId = owner.Id,
            DisplayName = "Hiring agent",
            EmployeeType = EmployeeType.Agent,
            PermissionLevel = OrganizationPermissionLevel.Contributor,
            IsActive = true,
            CreatedAt = now
        };
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            AgentOrganizationUserId = requester.Id,
            InitiatedByOrganizationUserId = owner.Id,
            Kind = ConversationKind.DirectHumanAgent,
            CreatedAt = now,
            UpdatedAt = now,
            Participants =
            [
                new ConversationParticipant { Id = Guid.NewGuid(), OrganizationUserId = owner.Id, JoinedAt = now },
                new ConversationParticipant { Id = Guid.NewGuid(), OrganizationUserId = requester.Id, JoinedAt = now }
            ]
        };
        var message = new ConversationMessage
        {
            Id = Guid.NewGuid(), ConversationId = conversation.Id, Sequence = 1,
            Role = ConversationRole.User, Content = "Submit the hiring request.",
            SenderOrganizationUserId = owner.Id, CreatedAt = now
        };
        var turn = new ChatTurn
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, ConversationId = conversation.Id,
            TargetAgentOrganizationUserId = requester.Id, UserMessageId = message.Id, UserMessage = message,
            Status = ChatTurnStatus.Running, CreatedAt = now, UpdatedAt = now
        };
        db.AddRange(requester, conversation, message, turn);
        await db.SaveChangesAsync();
        return (conversation.Id, turn.Id);
    }

    private sealed class RecordingImportPreview(string repositoryUrl) : IAgentImportPreviewService
    {
        public string CommitSha { get; } = new('a', 40);
        public IReadOnlyList<string> ProvidedCapabilities { get; } =
            ["assistant.converse.v1", "product-management.plan.v1"];
        public IReadOnlyList<string> RequestedCapabilities { get; } =
            ["platform.llm.chat-stream.v1", "platform.business-profile.read.v1"];
        public List<PreviewAgentImportRequest> Requests { get; } = [];

        public Task<AgentImportPreviewResponse> PreviewAsync(
            PreviewAgentImportRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new AgentImportPreviewResponse(
                Guid.Parse("7547f772-e46b-4918-a290-f4fba1f04457"),
                repositoryUrl,
                CommitSha,
                new string('b', 64),
                "com.csweet.product-manager",
                "C-Sweet Product Manager",
                "1.0.0",
                "com.csweet",
                "C-Sweet",
                "dotnet-project",
                "src/CSweet.Agents.ProductManager/CSweet.Agents.ProductManager.csproj",
                "net10.0",
                "AlwaysOn",
                ProvidedCapabilities,
                ["com.csweet.agent.onboarded.v1"],
                ["com.csweet.assistant.response.created.v1"],
                [],
                [],
                [],
                "Previewed")
            {
                RequestedCapabilities = RequestedCapabilities,
                ConfigurationFields =
                [
                    new PluginConfigurationField
                    {
                        Key = "llmProviderId", Type = "provider", Label = "LLM provider", Required = true
                    },
                    new PluginConfigurationField
                    {
                        Key = "llmModel", Type = "model", Label = "Model", Required = true
                    },
                    new PluginConfigurationField
                    {
                        Key = "responseTone",
                        Type = "select",
                        Label = "Response tone",
                        Required = true,
                        Description = "Controls response detail.",
                        Options =
                        [
                            new("concise", "Concise"),
                            new("balanced", "Balanced"),
                            new("detailed", "Detailed")
                        ]
                    }
                ]
            });
        }
    }

    private sealed class RecordingAgentCatalog(CSweet.Agent.SDK.AvailableAgent agent)
        : CSweet.Application.Agents.IAgentCatalogService
    {
        public Task<CSweet.Agent.SDK.AvailableAgentSearchResult> GetAvailableAgentsAsync(
            Guid? organizationId,
            CSweet.Agent.SDK.AvailableAgentSearchQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CSweet.Agent.SDK.AvailableAgentSearchResult([agent], []));

        public Task<CSweet.Agent.SDK.AvailableAgent?> ResolveAsync(
            Guid? organizationId,
            string agentReference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<CSweet.Agent.SDK.AvailableAgent?>(
                string.Equals(agent.AgentReference, agentReference, StringComparison.Ordinal) ? agent : null);
    }

    private sealed class RecordingInstallationService(Guid organizationId) : IAgentInstallationService
    {
        public Guid InstallationId { get; } = Guid.NewGuid();
        public InstallAgentRequest? Request { get; private set; }
        public int InstallCount { get; private set; }
        private AgentInstallationResponse? _installed;

        public Task<AgentInstallationResponse> InstallAsync(
            Guid importId,
            InstallAgentRequest request,
            CancellationToken cancellationToken = default)
        {
            InstallCount++;
            Request = request;
            var now = DateTimeOffset.UtcNow;
            _installed = new AgentInstallationResponse(
                InstallationId,
                importId,
                organizationId.ToString("D"),
                "com.csweet.product-manager",
                "C-Sweet Product Manager",
                "1.0.0",
                "C-Sweet",
                new string('a', 40),
                true,
                request.GrantedCapabilities,
                request.GrantedSubscriptions,
                request.GrantedPublications,
                request.GrantedPermissions,
                request.GrantedNetworkAccess,
                request.MemoryMb,
                request.CpuPercent,
                new AgentScheduleResponse(
                    Guid.NewGuid(),
                    request.ActivationMode,
                    request.TickFrequencySeconds,
                    null,
                    null,
                    null,
                    null,
                    request.MaxRuntimeSeconds,
                    0,
                    0,
                    null,
                    request.OverlapPolicy,
                    true),
                now,
                now);
            return Task.FromResult(_installed);
        }

        public Task<IReadOnlyList<AgentInstallationResponse>> ListAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<AgentInstallationResponse?> GetAsync(Guid installationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(installationId == InstallationId ? _installed : null);
        public Task<AgentInstallationResponse> UpdateScheduleAsync(Guid installationId, UpdateAgentScheduleRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<AgentInstallationResponse> RunNowAsync(Guid installationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<AgentInstallationResponse> RetryBuildAsync(Guid installationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<AgentInstallationResponse> RetryStartupAsync(Guid installationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<AgentInstallationResponse> DisableAsync(Guid installationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<AgentInstallationResponse> UpdateAsync(Guid installationId, UpdateAgentInstallationRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<AgentInstallationResponse> ApproveUpdateAsync(Guid stagedRevisionId, InstallAgentRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<AgentInstallationResponse> EnableAsync(Guid installationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<RemoveAgentInstallationResponse> RemoveAsync(Guid installationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<AgentRuntimeRunResponse>> ListRunsAsync(Guid installationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<AgentBuildLogResponse?> GetBuildLogAsync(Guid installationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingOrganizationUserService : IOrganizationUserService
    {
        public CreateOrganizationUserRequest? CreatedRequest { get; private set; }
        public int CreateCount { get; private set; }

        public Task<CoreActionResponse> CreateAsync(
            Guid organizationId,
            CreateOrganizationUserRequest request,
            CancellationToken cancellationToken = default,
            Guid? hiringApplicationUserId = null,
            string hiringSource = "Manual")
        {
            CreateCount++;
            CreatedRequest = request;
            return Task.FromResult(new CoreActionResponse(
                true,
                null,
                "Created",
                OrganizationUser: new OrganizationUserResponse(
                    Guid.NewGuid(),
                    organizationId,
                    request.ReportsToOrganizationUserId,
                    request.RoleId,
                    null,
                    request.DisplayName,
                    null,
                    request.EmployeeType,
                    request.PermissionLevel,
                    DateTimeOffset.UtcNow)
                {
                    AgentInstallationId = request.AgentInstallationId
                }));
        }

        public Task<IReadOnlyList<OrganizationUserResponse>> ListByOrganizationAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<OrganizationUserResponse?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<CoreActionResponse> UpdateRoleAsync(Guid organizationId, Guid id, UpdateOrganizationUserRoleRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<CoreActionResponse> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
