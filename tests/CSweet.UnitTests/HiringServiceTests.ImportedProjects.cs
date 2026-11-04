using System.Text.Json;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class HiringServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewedImportedManagerHireAssignsCanonicalProjectAndRejectsChangedCategoryBeforeMutation(bool changedCategory)
    {
        await using var db = CreateDb(); var org = Guid.NewGuid(); var appUser = Guid.NewGuid(); var chiefInstallation = Guid.NewGuid();
        var owner = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org, ApplicationUserId = appUser, DisplayName = "CEO", EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner };
        var worker = new Worker { Id = Guid.NewGuid(), OrganizationId = org, Name = "PM", WorkerType = WorkerType.LocalAgent, CapabilitiesJson = "[\"product\"]", IsEnabled = true };
        var package = new AgentPackageVersion { Id = Guid.NewGuid(), AgentId = "test.pm", Version = "1.0.0", ManifestJson = "{\"rolePolicy\":{\"declaredRoleKeys\":[\"software-product-manager\"]}}" };
        var install = new AgentInstallation { Id = Guid.NewGuid(), BusinessId = org.ToString(), PackageVersionId = package.Id, PackageVersion = package, IsEnabled = true, RevisionStatus = PluginRevisionStatus.Active };
        var manager = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org, DisplayName = "PM", WorkerId = worker.Id, AgentInstallationId = install.Id, EmployeeType = EmployeeType.Agent, ReportsToOrganizationUserId = owner.Id };
        var project = new Workstream { Id = Guid.NewGuid(), OrganizationId = org, Name = "Imported app", Status = WorkstreamStatus.Proposed };
        var candidate = new WorkforceCandidate { Id = Guid.NewGuid(), OrganizationId = org, Source = "CurrentStaff", ExternalCandidateId = worker.Id.ToString(), DisplayName = "PM", IsAvailable = true, IsHuman = false, CapabilitiesJson = "[\"product\"]", ExplanationJson = "{}" };
        db.AddRange(new Organization { Id = org, Name = "Business" }, owner, manager, worker, install, candidate, project,
            new ProjectRepositoryAssociation { ProjectId = project.Id, OrganizationId = org, RepositoryId = Guid.NewGuid(), ImportSourceId = Guid.NewGuid() },
            new ImportedProjectProfile { ProjectId = project.Id, OrganizationId = org, Status = "Accepted", AcceptedJson = "{\"category\":\"Software\"}" });
        await db.SaveChangesAsync();
        var origin = await AddHiringOriginAsync(db, org, owner, chiefInstallation);
        var chief = await db.CoreOrganizationUsers.SingleAsync(x => x.AgentInstallationId == chiefInstallation);
        db.LeadershipAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = org, OrganizationUserId = chief.Id, PositionKey = "chief-of-staff", StartsAt = DateTimeOffset.UtcNow.AddSeconds(-1) });
        await db.SaveChangesAsync();
        var service = new HiringService(db, new OrganizationUserService(db, new TestAuditEventWriter()), new TestAuditEventWriter());
        var recommendation = await service.UpsertRecommendationAsync(org, chiefInstallation,
            new("Dedicated PM", "Manage existing app", project.Id, [$"candidate:{candidate.Id:N}"], $"candidate:{candidate.Id:N}", "import-manager") { RoleKey = "software-product-manager", AssignAsProjectManager = true });
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveRecommendationAsync(org, chiefInstallation, new(recommendation.Id, manager.Id, "unreviewed")));
        var workflow = await service.StageWorkflowAsync(org, chiefInstallation,
            new(recommendation.Id, recommendation.RecommendedCandidateReference!, "Product Manager", owner.Id, [], "manager-review") { ConversationId = origin.ConversationId, ChatTurnId = origin.ChatTurnId });
        Assert.Null(project.AccountableManagerOrganizationUserId);
        if (changedCategory)
        {
            (await db.ImportedProjectProfiles.SingleAsync()).AcceptedJson = "{\"category\":\"VideoGame\"}"; await db.SaveChangesAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfirmWorkflowAsync(org, workflow.Id, appUser, new("approve")));
            Assert.Null(manager.RoleId); Assert.Null(project.AccountableManagerOrganizationUserId); Assert.Empty(db.HiringRecommendationFulfillments);
            return;
        }
        await service.ConfirmWorkflowAsync(org, workflow.Id, appUser, new("approve"));
        Assert.Equal(manager.Id, project.AccountableManagerOrganizationUserId); Assert.Single(db.Workstreams);
        Assert.Equal(project.Id, (await db.ProjectManagerReservations.SingleAsync()).WorkstreamId);
        Assert.Empty(db.OrganizationTeams); Assert.Empty(db.ProjectDeliveryBindings); Assert.Equal(WorkstreamStatus.Proposed, project.Status);
    }
}
