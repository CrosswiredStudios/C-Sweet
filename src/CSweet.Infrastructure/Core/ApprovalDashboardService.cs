using System.Text.Json;
using CSweet.Application.Core;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed class ApprovalDashboardService(
    CSweetDbContext db,
    IResourceChangeService resourceChanges,
    IHiringService hiring) : IApprovalDashboardService
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
        var managersByInstallation = people
            .Where(x => x.AgentInstallationId.HasValue && x.ReportsToOrganizationUserId.HasValue)
            .GroupBy(x => x.AgentInstallationId!.Value)
            .ToDictionary(x => x.Key, x => x.First().ReportsToOrganizationUserId!.Value);

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
        items.AddRange(agentActions.Select(proposal =>
        {
            var managerId = managersByInstallation.GetValueOrDefault(proposal.AgentInstallationId);
            return new ApprovalDashboardItemResponse(
                proposal.Id,
                ApprovalDashboardKinds.AgentAction,
                Humanize(proposal.ActionType),
                proposal.Summary,
                proposal.Status.ToString(),
                Name(installationNames, proposal.AgentInstallationId, "Agent employee"),
                managerId == Guid.Empty ? ownerLabel : Name(names, managerId, ownerLabel),
                proposal.CreatedAt,
                proposal.DecidedAt,
                $"/organizations/{organizationId:D}/approvals",
                proposal.Status == ProposalStatus.Pending &&
                (actor.PermissionLevel == OrganizationPermissionLevel.Owner || actor.Id == managerId))
            {
                AgentAction = ReadManagedAction(proposal)
            };
        }));

        var hiringCards = await hiring.ListApprovalCardsAsync(organizationId, cancellationToken: cancellationToken);
        var hiringWorkflowIds = hiringCards.Keys.ToList();
        var hiringWorkflows = await db.StaffingActionProposals.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && hiringWorkflowIds.Contains(x.Id))
            .OrderByDescending(x => x.CreatedAt).Take(250).ToListAsync(cancellationToken);
        items.AddRange(hiringWorkflows.Select(workflow =>
        {
            var card = hiringCards[workflow.Id];
            return new ApprovalDashboardItemResponse(
            workflow.Id,
            ApprovalDashboardKinds.HiringWorkflow,
            $"Hiring workflow: {card.RoleTitle}",
            $"Hire {card.EmployeeDisplayName} from {card.CandidateSource}.",
            workflow.Status.ToString(),
            Name(installationNames, workflow.RequestingInstallationId, "Hiring workflow"),
            ownerLabel,
            workflow.CreatedAt,
            workflow.DecidedAt,
            workflow.ConversationId.HasValue
                ? $"/organizations/{organizationId:D}/communications/{workflow.ConversationId:D}"
                : $"/organizations/{organizationId:D}/approvals",
            actor.PermissionLevel == OrganizationPermissionLevel.Owner &&
            workflow.Status == ProposalStatus.Pending &&
            workflow.SubmittedAt.HasValue,
            HiringWorkflow: card);
        }));

        var sourceApprovals = await db.SourceControlApprovals.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(250)
            .ToListAsync(cancellationToken);
        var provisioningIds = sourceApprovals
            .Where(x => x.ProvisioningRequestId.HasValue)
            .Select(x => x.ProvisioningRequestId!.Value)
            .ToList();
        var provisioning = await db.RepositoryProvisioningRequests.AsNoTracking()
            .Include(x => x.Connection)
            .Include(x => x.Policy)
            .Include(x => x.Template)
            .Where(x => x.OrganizationId == organizationId && provisioningIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var teamIds = provisioning.Values
            .Where(x => x.TeamId.HasValue)
            .Select(x => x.TeamId!.Value)
            .Distinct()
            .ToList();
        var teamNames = await db.OrganizationTeams.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && teamIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        var mergeIds = sourceApprovals
            .Where(x => x.MergeJobId.HasValue)
            .Select(x => x.MergeJobId!.Value)
            .ToList();
        var mergeJobs = await db.SourceControlMergeJobs.AsNoTracking()
            .Include(x => x.Publication)!.ThenInclude(x => x!.Repository)
            .Where(x => x.OrganizationId == organizationId && mergeIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        foreach (var approval in sourceApprovals)
        {
            SourceControlApprovalCardResponse card;
            string title;
            string summary;
            string kind;
            if (approval.Kind == SourceControlApprovalKind.RepositoryProvisioning &&
                approval.ProvisioningRequestId.HasValue &&
                provisioning.TryGetValue(approval.ProvisioningRequestId.Value, out var request))
            {
                title = $"New private code project: {request.RepositoryName}";
                summary = $"Create a private code project in {request.Connection?.AccountLogin}.";
                kind = ApprovalDashboardKinds.RepositoryProvisioning;
                card = new SourceControlApprovalCardResponse(
                    approval.Id,
                    approval.Kind.ToString(),
                    request.Id,
                    null,
                    request.RepositoryName,
                    request.Connection?.AccountLogin ?? "GitHub organization",
                    true,
                    request.Template?.DisplayName,
                    request.TeamId.HasValue && teamNames.TryGetValue(request.TeamId.Value, out var teamName)
                        ? teamName
                        : null,
                    request.Policy?.MaximumRepositories,
                    approval.Status.ToString(),
                    approval.Revision);
            }
            else if (approval.MergeJobId.HasValue &&
                     mergeJobs.TryGetValue(approval.MergeJobId.Value, out var merge))
            {
                title = $"Code merge: {merge.Publication?.Repository?.Name ?? "code project"}";
                summary = $"Merge exact version {merge.ExpectedHeadSha[..Math.Min(12, merge.ExpectedHeadSha.Length)]}.";
                kind = ApprovalDashboardKinds.Merge;
                card = new SourceControlApprovalCardResponse(
                    approval.Id,
                    approval.Kind.ToString(),
                    null,
                    merge.Id,
                    merge.Publication?.Repository?.Name ?? "Code project",
                    merge.Publication?.Repository?.Owner ?? "GitHub",
                    merge.Publication?.Repository?.IsPrivate ?? true,
                    null,
                    null,
                    null,
                    approval.Status.ToString(),
                    approval.Revision);
            }
            else
            {
                continue;
            }

            items.Add(new ApprovalDashboardItemResponse(
                approval.Id,
                kind,
                title,
                summary,
                approval.Status.ToString(),
                Name(names, approval.RequestedByOrganizationUserId, "Software team"),
                ownerLabel,
                approval.CreatedAt,
                approval.DecidedAt,
                $"/organizations/{organizationId:D}/approvals",
                approval.Status == ApprovalStatus.Pending,
                SourceControl: card));
        }

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

    private static ManagedAgentActionApprovalResponse? ReadManagedAction(ActionProposal proposal)
    {
        try
        {
            using var payload = JsonDocument.Parse(proposal.PayloadJson);
            var root = payload.RootElement;
            if (!root.TryGetProperty("channelId", out var channel) ||
                !root.TryGetProperty("payloadHash", out var hash) ||
                !root.TryGetProperty("idempotencyKey", out var idempotency)) return null;
            return new(proposal.Id,
                root.TryGetProperty("actionType", out var action) ? action.GetString() ?? proposal.ActionType : proposal.ActionType,
                channel.GetString() ?? string.Empty,
                hash.GetString() ?? string.Empty,
                root.TryGetProperty("expectedRevision", out var revision) && revision.ValueKind == JsonValueKind.Number ? revision.GetInt64() : null,
                idempotency.GetString() ?? string.Empty,
                root.TryGetProperty("alwaysRequiresApproval", out var always) && always.GetBoolean(),
                root.TryGetProperty("resourceId", out var resource) && resource.ValueKind == JsonValueKind.String
                    ? resource.GetString() : null);
        }
        catch (JsonException) { return null; }
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
