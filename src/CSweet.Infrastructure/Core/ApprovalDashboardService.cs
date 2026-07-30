using System.Text.Json;
using CSweet.Application.Core;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed class ApprovalDashboardService(
    CSweetDbContext db,
    IResourceChangeService resourceChanges) : IApprovalDashboardService
{
    public async Task<ApprovalDashboardResponse> GetAsync(
        Guid organizationId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default)
    {
        var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId &&
            x.ApplicationUserId == applicationUserId &&
            x.IsActive,
            cancellationToken) ?? throw new UnauthorizedAccessException(
                "The signed-in user is not an active employee of this organization.");
        if (actor.PermissionLevel < OrganizationPermissionLevel.Manager)
            throw new UnauthorizedAccessException(
                "Only organization owners and managers may view the approvals inbox.");

        var people = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .ToListAsync(cancellationToken);
        var names = people.ToDictionary(x => x.Id, x => x.DisplayName);
        var installationNames = people
            .Where(x => x.AgentInstallationId.HasValue)
            .GroupBy(x => x.AgentInstallationId!.Value)
            .ToDictionary(x => x.Key, x => x.First().DisplayName);
        var ownerNames = people
            .Where(x => x.IsActive && x.PermissionLevel == OrganizationPermissionLevel.Owner)
            .Select(x => x.DisplayName)
            .ToList();
        var ownerLabel = ownerNames.Count == 0
            ? "Organization owner"
            : string.Join(", ", ownerNames);

        var items = new List<ApprovalDashboardItemResponse>();
        var teamRequests = await resourceChanges.ListForDashboardAsync(
            organizationId,
            cancellationToken);
        items.AddRange(teamRequests.Select(request => new ApprovalDashboardItemResponse(
            request.Id,
            ApprovalDashboardKinds.ResourceChange,
            $"Team design: {request.ProductGoal}",
            request.Rationale,
            request.Status,
            Name(names, request.RequesterOrganizationUserId, "Agent employee"),
            Name(names, request.ManagerOrganizationUserId, "Assigned manager"),
            request.CreatedAt,
            request.DecidedAt,
            $"/organizations/{organizationId:D}/communications/{request.ConversationId:D}",
            request.Status == ResourceChangeRequestStatus.Pending.ToString() &&
            request.ManagerOrganizationUserId == actor.Id,
            request)));

        var agentActions = await db.ActionProposals.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(250)
            .ToListAsync(cancellationToken);
        items.AddRange(agentActions.Select(proposal => new ApprovalDashboardItemResponse(
            proposal.Id,
            ApprovalDashboardKinds.AgentAction,
            Humanize(proposal.ActionType),
            proposal.Summary,
            proposal.Status.ToString(),
            Name(installationNames, proposal.AgentInstallationId, "Agent employee"),
            ownerLabel,
            proposal.CreatedAt,
            proposal.DecidedAt,
            $"/organizations/{organizationId:D}/command-center",
            false)));

        var hiringWorkflows = await db.StaffingActionProposals.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(250)
            .ToListAsync(cancellationToken);
        items.AddRange(hiringWorkflows.Select(workflow => new ApprovalDashboardItemResponse(
            workflow.Id,
            ApprovalDashboardKinds.HiringWorkflow,
            $"Hiring workflow: {ReadRoleTitle(workflow.PayloadJson)}",
            $"Candidate {workflow.CandidateId} via {workflow.CandidateSource}.",
            workflow.Status.ToString(),
            Name(installationNames, workflow.RequestingInstallationId, "Hiring workflow"),
            ownerLabel,
            workflow.CreatedAt,
            workflow.DecidedAt,
            $"/organizations/{organizationId:D}/employees?tab=hiring",
            false)));

        var artifacts = await db.CoreArtifacts.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId &&
                x.ApprovalStatus != ApprovalStatus.NotRequired)
            .OrderByDescending(x => x.UpdatedAt)
            .Take(250)
            .ToListAsync(cancellationToken);
        items.AddRange(artifacts.Select(artifact => new ApprovalDashboardItemResponse(
            artifact.Id,
            ApprovalDashboardKinds.Artifact,
            $"Artifact: {artifact.Title}",
            $"Version {artifact.Version} {artifact.Type}.",
            artifact.ApprovalStatus.ToString(),
            "System workflow",
            ownerLabel,
            artifact.CreatedAt,
            artifact.ApprovalStatus == ApprovalStatus.Pending ? null : artifact.UpdatedAt,
            $"/organizations/{organizationId:D}/command-center",
            false)));

        var ordered = items
            .OrderBy(x => IsPending(x.Status) ? 0 : 1)
            .ThenByDescending(x => x.CreatedAt)
            .ToList();
        return new ApprovalDashboardResponse(
            actor.Id,
            ordered.Count(x => IsPending(x.Status)),
            ordered);
    }

    private static string Name(
        IReadOnlyDictionary<Guid, string> names,
        Guid id,
        string fallback) =>
        names.TryGetValue(id, out var name) ? name : fallback;

    private static bool IsPending(string status) =>
        status.Equals("Pending", StringComparison.OrdinalIgnoreCase);

    private static string Humanize(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? "Agent action"
            : string.Join(" ", value
                .Replace('-', ' ')
                .Replace('_', ' ')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select((word, index) => index == 0
                    ? char.ToUpperInvariant(word[0]) + word[1..]
                    : word));

    private static string ReadRoleTitle(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty("roleTitle", out var roleTitle) &&
                   roleTitle.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(roleTitle.GetString())
                ? roleTitle.GetString()!
                : "Staffing action";
        }
        catch (JsonException)
        {
            return "Staffing action";
        }
    }
}
