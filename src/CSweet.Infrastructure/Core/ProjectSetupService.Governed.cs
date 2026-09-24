using System.Text.Json;
using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;
namespace CSweet.Infrastructure.Core;

public sealed partial class ProjectSetupService
{
    /// <summary>Completes setup only after the existing managed-action approval path authorizes the exact plan.</summary>
    public async Task ProvisionApprovedAsync(Workstream project, OrganizationUser approver, Guid? teamId, JsonElement data, CancellationToken ct)
    {
        await policy.LockAsync(project.OrganizationId, ct);
        ProjectIntake? intake = null;
        if (data.TryGetProperty("intakeId", out var id))
        {
            intake = await db.ProjectIntakes.SingleOrDefaultAsync(x => x.Id == id.GetGuid() && x.OrganizationId == project.OrganizationId &&
                x.ManagerId == project.AccountableManagerOrganizationUserId && x.Status == "AwaitingManagerAssistance", ct)
                ?? throw new InvalidOperationException("This project setup request was cancelled, changed, or assigned to another manager.");
        }
        if (intake is not null && (!data.TryGetProperty("setupChoiceMessageId", out var choice) ||
            choice.ValueKind != JsonValueKind.String || !choice.TryGetGuid(out var choiceId) || choiceId != intake.LastChoiceMessageId))
            throw new InvalidOperationException("This project proposal belongs to an obsolete setup choice. Ask the manager to submit the current request.");
        await ReserveManagerAsync(project.OrganizationId, project.AccountableManagerOrganizationUserId!.Value, project.Id, intake?.Id, ct);
        if (!data.TryGetProperty("participantIds", out var selected)) return; // Older governed plans require explicit human membership setup before delivery.
        var ids = selected.EnumerateArray().Select(x => x.GetGuid()).Append(project.AccountableManagerOrganizationUserId.Value).Distinct().ToArray();
        if (ids.Length is < 2 or > 100) throw new ArgumentException("Choose the project's explicit participants.");
        var people = await ValidatePeopleAsync(project.OrganizationId, ids, project.AccountableManagerOrganizationUserId.Value, ct);
        var manager = people.Single(x => x.Id == project.AccountableManagerOrganizationUserId);
        if (manager.EmployeeType == EmployeeType.Agent && !await db.ResourceChangeRequests.AnyAsync(x => x.OrganizationId == project.OrganizationId &&
            x.RequesterInstallationId == manager.AgentInstallationId && x.TeamId == teamId && x.Status == ResourceChangeRequestStatus.Approved, ct))
            throw new InvalidOperationException("The project manager must have an approved team staffing plan before its project board is created.");
        if (manager.EmployeeType == EmployeeType.Agent)
            foreach (var role in new[] { "software-architect", "software-developer", "software-qa" })
            {
                var present = false;
                foreach (var person in people) present |= await HasRoleAsync(person, role, ct);
                if (!present) throw new InvalidOperationException($"The managed software project needs an active {role} in its approved staffing plan.");
            }
        var team = await ResolveTeamAsync(project.OrganizationId, teamId, people, approver, project.Name, ct);
        if (!teamId.HasValue) db.WorkstreamTeamAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = project.OrganizationId, WorkstreamId = project.Id, TeamId = team.Id, StartsAt = clock.GetUtcNow() });
        var board = CreateBoard(project, team.Id, manager.Id);
        db.ProjectDeliveryBindings.Add(new() { WorkstreamId = project.Id, OrganizationId = project.OrganizationId, BoardId = board.Id, TeamId = team.Id, CreationKey = $"approved:{project.SourceProposalId:N}" });
        var added = await ApplyParticipantsAsync(project, board, people, approver, ct);
        if (intake is not null)
        {
            intake.ProjectId = project.Id; intake.BoardId = board.Id; intake.TeamId = team.Id;
            intake.Status = ids.Contains(intake.DeveloperId) ? "Ready" : "AwaitingAssignment";
            intake.Issue = ids.Contains(intake.DeveloperId) ? null : "The developer was not included in the approved participants. An authorized human must assign it before work starts.";
            intake.Revision++; intake.UpdatedAt = clock.GetUtcNow(); QueueIntake(intake);
        }
        QueueProject(project);
        QueueProjectAssignmentChanges(project, board.Id, team.Id, added, []);
    }
}
