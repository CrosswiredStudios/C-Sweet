using CSweet.Application.WorkManagement;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

public sealed class EmployeeAssignedWorkQueryService(CSweetDbContext db, TimeProvider clock)
    : IEmployeeAssignedWorkQueryService
{
    public async Task<EmployeeAssignedWorkResponse> GetAsync(Guid organizationId, Guid employeeId,
        Guid viewerOrganizationUserId, CancellationToken cancellationToken = default)
    {
        var employee = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == employeeId && x.OrganizationId == organizationId && x.IsActive, cancellationToken)
            ?? throw new KeyNotFoundException("The employee was not found.");

        var items = await db.CoreWorkTasks.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Board != null &&
                x.Board.Kind == WorkBoardKind.Standard && x.ArchivedAt == null &&
                (x.AssignedEmployeeId == employeeId ||
                 x.AccountableOrganizationUserId == employeeId ||
                 x.StageAssignments.Any(a => a.OrganizationUserId == employeeId) ||
                 (employee.AgentInstallationId.HasValue &&
                  x.StageAssignments.Any(a => a.AgentInstallationId == employee.AgentInstallationId))))
            .Include(x => x.Board)
            .Include(x => x.BoardColumn)
            .Include(x => x.Sprint)
            .Include(x => x.StageAssignments)
            .OrderBy(x => x.DueDate).ThenBy(x => x.BoardRank)
            .ToListAsync(cancellationToken);

        var boardIds = items.Where(x => x.BoardId.HasValue).Select(x => x.BoardId!.Value).Distinct().ToArray();
        var now = clock.GetUtcNow();
        var readableBoardIds = await db.ScopedActionGrants.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId &&
                x.SubjectKind == GrantSubjectKind.OrganizationUser &&
                x.SubjectId == viewerOrganizationUserId &&
                x.ScopeKind == GrantScopeKind.Board && x.ScopeId.HasValue &&
                boardIds.Contains(x.ScopeId.Value) && x.RevokedAt == null &&
                (!x.ExpiresAt.HasValue || x.ExpiresAt > now) &&
                (x.Action == WorkBoardActions.Read || x.Action == WorkItemActions.Read))
            .Select(x => x.ScopeId!.Value).Distinct().ToListAsync(cancellationToken);
        var readable = readableBoardIds.ToHashSet();

        var response = items.Select(item =>
        {
            var relationships = new List<string>();
            if (item.AssignedEmployeeId == employeeId)
                relationships.Add(Wire.WorkAssignmentRelationships.DirectAssignee);
            if (item.AccountableOrganizationUserId == employeeId)
                relationships.Add(Wire.WorkAssignmentRelationships.AccountableOwner);
            if (item.StageAssignments.Any(x => x.OrganizationUserId == employeeId))
                relationships.Add(Wire.WorkAssignmentRelationships.StageAssignee);
            if (employee.AgentInstallationId.HasValue && item.StageAssignments.Any(x =>
                x.AgentInstallationId == employee.AgentInstallationId))
                relationships.Add(Wire.WorkAssignmentRelationships.StageAgent);

            var canonical = new Wire.WorkItemResponse(item.Id, item.OrganizationId,
                item.BoardId!.Value, item.BoardColumnId ?? Guid.Empty, item.Kind.ToString(), item.Title,
                item.Description, item.Status.ToString(), item.Priority.ToString(), item.BoardRank,
                item.Revision, item.DueDate, item.CreatedAt, item.UpdatedAt, item.ArchivedAt)
            {
                Identifier = item.Identifier,
                SprintId = item.SprintId,
                EstimatePoints = item.EstimatePoints,
                Provenance = new Wire.WorkItemProvenance(item.CreatedByOrganizationUserId,
                    item.SourceConversationId, item.SourceMessageId, item.CorrelationId,
                    item.CausationId, item.CreationIdempotencyKey),
                Assignment = new Wire.WorkAssignmentMetadata(item.AssignedEmployeeId,
                    item.AssignedAgentInstallationId, item.AccountableOrganizationUserId, relationships),
                Execution = new Wire.WorkItemExecutionMetadata(item.ResultSummary,
                    item.BlockReason, item.ClaimEventId, item.ClaimExpiresAt),
                Mentions = WorkItemMentionCodec.Deserialize(item.StructuredMentionsJson)
            };
            return new EmployeeAssignedWorkItemResponse(canonical, item.Board!.Name,
                item.BoardColumn?.Name ?? "Unmapped", item.Sprint?.Name, relationships,
                readable.Contains(item.BoardId.Value));
        }).ToList();
        return new EmployeeAssignedWorkResponse(employeeId, response);
    }
}
