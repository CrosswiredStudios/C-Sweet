using CSweet.Agent.SDK;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CSweet.Infrastructure.Core;

public sealed partial class ProjectSetupService
{
    // The capability handler holds the organization lock and the transaction, including the outbox.
    public async Task<PreparedProjectDelivery> PrepareDeliveryAsync(Guid org, Guid installation,
        PrepareProjectDeliveryRequest request, CancellationToken ct)
    {
        if (request.ParticipantIds.Count is < 1 or > 100 || string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200)
            throw new ArgumentException("Select the approved participants and supply a stable setup key.");
        var manager = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.OrganizationId == org &&
            x.AgentInstallationId == installation && x.IsActive && x.ArchivedAt == null, ct)
            ?? throw new UnauthorizedAccessException("The project manager is no longer active.");
        var project = await db.Workstreams.SingleOrDefaultAsync(x => x.OrganizationId == org && x.Id == request.ProjectId, ct)
            ?? throw new KeyNotFoundException("The approved project was not found.");
        if (project.AccountableManagerOrganizationUserId != manager.Id || project.Status is not (WorkstreamStatus.Approved or WorkstreamStatus.Active))
            throw new UnauthorizedAccessException("Only the accountable manager of an approved active project can prepare delivery.");
        var authority = await db.WorkstreamAuthorityEnvelopes.SingleOrDefaultAsync(x => x.OrganizationId == org && x.WorkstreamId == project.Id, ct);
        if (authority is null || authority.ExpiresAt <= clock.GetUtcNow() ||
            !(JsonSerializer.Deserialize<string[]>(authority.AgentAuthorizedActionKeysJson) ?? []).Contains("routine-staffing") ||
            (JsonSerializer.Deserialize<string[]>(authority.HumanRequiredActionKeysJson) ?? []).Contains("routine-staffing"))
            throw new UnauthorizedAccessException("The approved project does not authorize routine staffing setup.");
        var staffing = await db.ResourceChangeRequests.SingleOrDefaultAsync(x => x.OrganizationId == org && x.Id == request.StaffingRequestId &&
            x.RequesterInstallationId == installation && x.Status == ResourceChangeRequestStatus.Approved, ct)
            ?? throw new UnauthorizedAccessException("A currently approved staffing request from this manager is required.");
        if (!staffing.TeamId.HasValue || staffing.WorkstreamId.HasValue && staffing.WorkstreamId != project.Id)
            throw new UnauthorizedAccessException("The staffing approval is not available for this project.");
        var team = await db.OrganizationTeams.SingleOrDefaultAsync(x => x.OrganizationId == org && x.Id == staffing.TeamId && x.ArchivedAt == null, ct);
        if (team is null || team.LeadOrganizationUserId != manager.Id)
            throw new UnauthorizedAccessException("The project manager must lead the approved team.");
        var ids = request.ParticipantIds.Append(manager.Id).Distinct().ToArray();
        var people = await db.CoreOrganizationUsers.Where(x => x.OrganizationId == org && ids.Contains(x.Id) && x.IsActive && x.ArchivedAt == null).ToListAsync(ct);
        var members = await db.TeamMemberships.Where(x => x.OrganizationId == org && x.TeamId == team.Id && x.EndedAt == null && ids.Contains(x.OrganizationUserId)).Select(x => x.OrganizationUserId).ToListAsync(ct);
        if (people.Count != ids.Length || members.Count != ids.Length)
            throw new UnauthorizedAccessException("Every participant must already be an active member of the approved team. No employees were hired or moved.");
        if (await db.ProjectParticipants.AnyAsync(x => x.WorkstreamId == project.Id && ids.Contains(x.OrganizationUserId) && x.RemovedAt != null, ct))
            throw new UnauthorizedAccessException("A removed project participant needs explicit human restoration.");
        if (await db.ProjectParticipants.AnyAsync(x => x.OrganizationId == org && x.WorkstreamId != project.Id && ids.Contains(x.OrganizationUserId) &&
            x.RemovedAt == null && db.Workstreams.Any(p => p.Id == x.WorkstreamId && p.Status != WorkstreamStatus.Completed && p.Status != WorkstreamStatus.Cancelled), ct))
            throw new InvalidOperationException("A participant is already assigned to another active project. Finish or explicitly change that assignment first.");
        var latestProfile = await db.WorkstreamProfileDefinitions.AsNoTracking().Where(x => x.Key == project.ProfileKey && x.Status == "Active")
            .OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct);
        var available = latestProfile is null ? null : new CSweet.WorkManagement.Contracts.WorkstreamProfileReference(latestProfile.Key, latestProfile.Version, latestProfile.DefinitionDigest);
        var binding = await db.ProjectDeliveryBindings.SingleOrDefaultAsync(x => x.OrganizationId == org && x.WorkstreamId == project.Id, ct);
        if (binding is not null)
        {
            if (binding.TeamId != team.Id || binding.CreationKey != request.IdempotencyKey ||
                await db.ProjectParticipants.CountAsync(x => x.WorkstreamId == project.Id && x.RemovedAt == null, ct) != ids.Length ||
                await db.ProjectParticipants.CountAsync(x => x.WorkstreamId == project.Id && x.RemovedAt == null && ids.Contains(x.OrganizationUserId), ct) != ids.Length)
                throw new InvalidOperationException("Project setup already exists. Use project membership management to change it.");
            return new(project.Id, binding.TeamId, binding.BoardId, project.Revision) { AvailableProfile = available };
        }
        if (project.Revision != request.ExpectedProjectRevision)
            throw new DbUpdateConcurrencyException("The project changed. Read its current state before preparing delivery.");
        var assignments = await db.WorkstreamTeamAssignments.Where(x => x.OrganizationId == org && x.WorkstreamId == project.Id && x.EndsAt == null).ToListAsync(ct);
        if (assignments.Any(x => x.TeamId != team.Id) || await db.WorkBoards.AnyAsync(x => x.OrganizationId == org && x.WorkstreamId == project.Id && x.ArchivedAt == null, ct))
            throw new InvalidOperationException("This project already has a delivery board or a different team; explicit reconciliation is required.");
        if (assignments.Count == 0) db.WorkstreamTeamAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = org, WorkstreamId = project.Id, TeamId = team.Id, StartsAt = clock.GetUtcNow() });
        var board = CreateBoard(project, team.Id, manager.Id);
        db.ProjectDeliveryBindings.Add(new() { WorkstreamId = project.Id, OrganizationId = org, TeamId = team.Id, BoardId = board.Id, CreationKey = request.IdempotencyKey });
        var added = await ApplyParticipantsAsync(project, board, people, manager, ct);
        // Setup confers project-scoped operating authority only; the installation still needs each capability.
        var actions = new[] { WorkBoardActions.ConfigureColumns, WorkFlowMetricActions.Read,
            WorkSprintActions.Read, WorkSprintActions.Create, WorkSprintActions.ManageScope,
            WorkSprintActions.ManageCapacity, WorkSprintActions.CarryOver, WorkSprintActions.ReadReports,
            WorkOrchestrationActions.ConfigureProfile, WorkOrchestrationActions.Read, WorkOrchestrationActions.Preflight,
            WorkOrchestrationActions.Start, WorkOrchestrationActions.DecideApproval };
        foreach (var action in actions) db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = org,
            SubjectKind = GrantSubjectKind.AgentInstallation, SubjectId = installation, Action = action,
            ScopeKind = GrantScopeKind.Board, ScopeId = board.Id, GrantedBySubjectKind = GrantSubjectKind.OrganizationUser,
            GrantedBySubjectId = manager.Id, GrantedAt = clock.GetUtcNow() });
        project.Revision++; project.UpdatedAt = clock.GetUtcNow();
        QueueProject(project);
        QueueProjectAssignmentChanges(project, board.Id, team.Id, added, []);
        await db.SaveChangesAsync(ct);
        return new(project.Id, team.Id, board.Id, project.Revision) { AvailableProfile = available };
    }
}
