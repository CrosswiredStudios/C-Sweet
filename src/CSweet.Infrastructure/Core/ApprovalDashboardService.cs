using System.Text.Json;
using CSweet.Application.Core;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed class ApprovalDashboardService(
    CSweetDbContext db,
    IResourceChangeService resourceChanges,
    IHiringService hiring, ProjectHealthService? projectHealth = null) : IApprovalDashboardService
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

        await EmployeeActionExpiration.ReconcileAsync(db, organizationId, cancellationToken);

        var people = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .ToListAsync(cancellationToken);
        var names = people.ToDictionary(x => x.Id, x => x.DisplayName);
        var installationNames = people
            .Where(x => x.AgentInstallationId.HasValue)
            .GroupBy(x => x.AgentInstallationId!.Value)
            .ToDictionary(x => x.Key, x => x.First().DisplayName);
        var installationEmployees = people.Where(x => x.AgentInstallationId.HasValue)
            .GroupBy(x => x.AgentInstallationId!.Value).Where(x => x.Count() == 1)
            .ToDictionary(x => x.Key, x => x.Single().Id);
        var ownerNames = people
            .Where(x => x.IsActive && x.PermissionLevel == OrganizationPermissionLevel.Owner)
            .Select(x => x.DisplayName)
            .ToList();
        var ownerLabel = ownerNames.Count == 0
            ? "Organization owner"
            : string.Join(", ", ownerNames);
        var managersByInstallation = people
            .Where(x => x.IsActive && x.AgentInstallationId.HasValue && x.ReportsToOrganizationUserId.HasValue)
            .GroupBy(x => x.AgentInstallationId!.Value)
            .ToDictionary(x => x.Key, x => x.First().ReportsToOrganizationUserId!.Value);

        var items = new List<ApprovalDashboardItemResponse>();
        var teamRequests = await resourceChanges.ListForDashboardAsync(
            organizationId,
            cancellationToken);
        var requestTeamIds = teamRequests.Where(x => x.TeamId.HasValue).Select(x => x.TeamId!.Value).Distinct().ToList();
        var requestTeamNames = await db.OrganizationTeams.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && requestTeamIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
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
            request)
        {
            RequesterOrganizationUserId = request.RequesterOrganizationUserId,
            RequestingTeam = request.TeamId.HasValue ? requestTeamNames.GetValueOrDefault(request.TeamId.Value) ?? request.TeamName : request.TeamName,
            ActualDecisionMaker = request.DecidedByOrganizationUserId.HasValue
                ? Name(names, request.DecidedByOrganizationUserId.Value, "Decision maker")
                : null,
            SourceResourceChangeRequestId = request.Id
        }));

        var agentActions = await db.ActionProposals.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(250)
            .ToListAsync(cancellationToken);
        var installationIds = agentActions.Select(x => x.AgentInstallationId).Distinct().ToList();
        var configurations = await db.AgentInstallationConfigurations.AsNoTracking()
            .Where(x => installationIds.Contains(x.AgentInstallationId))
            .ToDictionaryAsync(x => x.AgentInstallationId, x => x.SettingsJson, cancellationToken);
        items.AddRange(agentActions.Select(proposal =>
        {
            var managerId = managersByInstallation.GetValueOrDefault(proposal.AgentInstallationId);
            var configurationJson = configurations.GetValueOrDefault(proposal.AgentInstallationId);
            var connectorBinding = proposal.ActionType == ConnectorActionApprovalService.ActionType
                ? ConnectorActionApprovalService.Parse(proposal) : null;
            return new ApprovalDashboardItemResponse(
                proposal.Id,
                ApprovalDashboardKinds.AgentAction,
                Humanize(proposal.ActionType),
                proposal.Summary,
                proposal.Status.ToString(),
                Name(installationNames, proposal.AgentInstallationId, "Agent employee"),
                connectorBinding is not null ? Name(names, connectorBinding.ApproverOrganizationUserId, "Assigned approver") :
                    ManagedActionApprovalAuthority.RequiresManager(configurationJson) ? Name(names, managerId, "Assigned manager") : ownerLabel,
                proposal.CreatedAt,
                proposal.DecidedAt,
                $"/organizations/{organizationId:D}/approvals",
                proposal.Status == ProposalStatus.Pending &&
                (connectorBinding is not null ? actor.Id == connectorBinding.ApproverOrganizationUserId &&
                    connectorBinding.ExpiresAt > DateTimeOffset.UtcNow :
                    ManagedActionApprovalAuthority.CanDecide(actor, managerId, configurationJson)))
            {
                AgentAction = ReadManagedAction(proposal),
                CanManageStandingPolicy = (connectorBinding?.Effect == "write") && actor.EmployeeType == EmployeeType.Human &&
                    actor.ApplicationUserId is not null && actor.PermissionLevel == OrganizationPermissionLevel.Owner
            };
        }));

        var hiringCards = await hiring.ListApprovalCardsAsync(organizationId, cancellationToken: cancellationToken);
        var hiringTeamIds = hiringCards.Values.Where(x => x.TeamId.HasValue)
            .Select(x => x.TeamId!.Value).Distinct().ToList();
        var hiringTeamNames = await db.OrganizationTeams.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && hiringTeamIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
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
            HiringWorkflow: card)
            {
                RequesterOrganizationUserId = installationEmployees.TryGetValue(workflow.RequestingInstallationId, out var employeeId)
                    ? employeeId : null,
                RequestingTeam = card.TeamId.HasValue ? hiringTeamNames.GetValueOrDefault(card.TeamId.Value) : null
            };
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
                x.ApprovalStatus != ApprovalStatus.NotRequired &&
                x.DocumentType != ArtifactDocumentTypes.RecoveredQaEvidence)
            .OrderByDescending(x => x.UpdatedAt)
            .Take(250)
            .ToListAsync(cancellationToken);
        var artifactIds = artifacts.Select(x => x.Id).ToList();
        var submittedRevisionIds = artifacts.Where(x => x.SubmittedRevisionId.HasValue)
            .Select(x => x.SubmittedRevisionId!.Value).ToList();
        var artifactDecisions = (await db.CoreApprovals.AsNoTracking()
            .Where(x => artifactIds.Contains(x.ArtifactId) && x.DecidedAt != null)
            .GroupBy(x => x.ArtifactId)
            .Select(x => x.OrderByDescending(y => y.DecidedAt).ThenByDescending(y => y.CreatedAt).First())
            .ToListAsync(cancellationToken))
            .ToDictionary(x => x.ArtifactId);
        var reviewJobs = await db.ArtifactReviewJobs.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && artifactIds.Contains(x.ArtifactId) &&
                submittedRevisionIds.Contains(x.RevisionId))
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        items.AddRange(artifacts.Select(artifact =>
        {
            var lastDecision = artifact.ApprovalStatus != ApprovalStatus.Pending
                ? artifactDecisions.GetValueOrDefault(artifact.Id) : null;
            // Queue completion means the review was delivered, not that its assignment ended.
            // An old revision's reviewer must never take ownership of a new submission.
            var reviewerId = reviewJobs.FirstOrDefault(x => x.ArtifactId == artifact.Id &&
                x.RevisionId == artifact.SubmittedRevisionId)?.ReviewerOrganizationUserId ??
                artifact.StewardOrganizationUserId;
            var assignedTo = reviewerId.HasValue ? Name(names, reviewerId.Value, "Assigned reviewer") : ownerLabel;
            var canDecide = artifact.ApprovalStatus == ApprovalStatus.Pending &&
                (reviewerId.HasValue ? reviewerId.Value == actor.Id :
                    actor.PermissionLevel == OrganizationPermissionLevel.Owner);
            return new ApprovalDashboardItemResponse(
                artifact.Id,
                ApprovalDashboardKinds.Artifact,
                $"Artifact: {artifact.Title}",
                $"Approve the submitted version of {artifact.Title} for your team to use, or request changes from the author.",
                artifact.ApprovalStatus.ToString(),
                artifact.CreatedByOrganizationUserId.HasValue
                    ? Name(names, artifact.CreatedByOrganizationUserId.Value, artifact.CreatorDisplayName)
                    : artifact.CreatorDisplayName,
                assignedTo,
                artifact.CreatedAt,
                artifact.ApprovalStatus == ApprovalStatus.Pending ? null : lastDecision?.DecidedAt ?? artifact.UpdatedAt,
                $"/organizations/{organizationId:D}/documents?artifact={artifact.Id:D}",
                canDecide)
            {
                Artifact = new ArtifactApprovalCardResponse(
                    artifact.Id,
                    artifact.SubmittedRevisionId ?? lastDecision?.ArtifactRevisionId),
                ActualDecisionMaker = lastDecision?.DecidedByOrganizationUserId is { } decisionMaker
                    ? Name(names, decisionMaker, "Former employee") : lastDecision?.DecidedByAgentInstallationId is { } installation
                    ? Name(installationNames, installation, "Former agent") : null,
                RequesterOrganizationUserId = artifact.CreatedByOrganizationUserId,
                DecisionComment = lastDecision?.Comment,
                DecisionKind = lastDecision?.Status.ToString()
            };
        }));

        var accessRequests = await db.ArtifactAccessRequests.AsNoTracking()
            .Include(x => x.Artifact)
            .Where(x => x.OrganizationId == organizationId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(250)
            .ToListAsync(cancellationToken);
        items.AddRange(accessRequests.Select(request =>
        {
            var actions = JsonSerializer.Deserialize<string[]>(request.ActionsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
            var installationId = request.RequestingInstallationId ?? request.SubjectId;
            var requesterName = request.SubjectKind == GrantSubjectKind.OrganizationUser
                ? Name(names, request.SubjectId, request.SubjectId.ToString("D"))
                : Name(installationNames, installationId, installationId.ToString("D"));
            return new ApprovalDashboardItemResponse(
            request.Id,
            ApprovalDashboardKinds.ArtifactAccess,
            $"Document access: {request.Artifact!.Title}",
            $"{request.Justification} Requested actions: {string.Join(", ", actions)}.",
            request.Status.ToString(),
            requesterName,
            ownerLabel,
            request.CreatedAt,
            request.DecidedAt,
            $"/organizations/{organizationId:D}/documents?artifact={request.ArtifactId:D}",
            request.Status == ArtifactAccessRequestStatus.Pending)
        {
            ArtifactAccess = new ArtifactAccessRequestResponse(
                request.Id, request.ArtifactId, GrantSubjectKind.AgentInstallation.ToString(),
                installationId,
                requesterName,
                actions, request.Justification, request.Status.ToString(), request.CreatedAt,
                request.ExpiresAt, request.DecidedAt)
            };
        }));

        var projectReader = new ProjectApprovalReader(db);
        foreach (var proposal in agentActions.Where(x => x.ActionType == ProjectApprovalReader.ActionType))
        {
            var index = items.FindIndex(x => x.Id == proposal.Id);
            var governance = new ProjectApprovalGovernance(db);
            var approverId = await governance.ApproverAsync(proposal, cancellationToken);
            items[index] = await projectReader.EnrichAsync(items[index] with {
                CanDecide = proposal.Status == ProposalStatus.Pending && await governance.CanDecideAsync(proposal, actor, cancellationToken),
                AssignedTo = approverId.HasValue ? Name(names, approverId.Value, "Assigned manager") : ownerLabel
            }, proposal, cancellationToken);
        }
        var ordered = items
            .Where(x => !IsPending(x.Status) || x.CanDecide)
            .OrderBy(x => IsPending(x.Status) ? 0 : 1)
            .ThenByDescending(x => x.CreatedAt)
            .ToList();
        return new ApprovalDashboardResponse(
            actor.Id,
            ordered.Count(x => IsPending(x.Status) && x.CanDecide),
            ordered)
        {
            ProjectIncidents = await (projectHealth ?? new(db, new(db, TimeProvider.System), TimeProvider.System))
                .ReadAttentionAsync(organizationId, actor.Id, cancellationToken)
        };
    }

    internal static ManagedAgentActionApprovalResponse? ReadManagedAction(ActionProposal proposal)
    {
        try
        {
            using var payload = JsonDocument.Parse(proposal.PayloadJson);
            var root = payload.RootElement;
            if (!root.TryGetProperty("channelId", out var channel) ||
                !root.TryGetProperty("payloadHash", out var hash) ||
                !root.TryGetProperty("idempotencyKey", out var idempotency)) return null;
            var fiscalSummary = ReadInfrastructureFiscalSummary(root);
            var approvalRoute = ReadInfrastructureApprovalRoute(root);
            var expiresAt = root.TryGetProperty("change", out var change) &&
                change.TryGetProperty("expiresAt", out var expiry) && expiry.TryGetDateTimeOffset(out var parsedExpiry)
                    ? parsedExpiry : (DateTimeOffset?)null;
            if (proposal.ActionType == ConnectorActionApprovalService.ActionType &&
                root.TryGetProperty("expiresAt", out var connectorExpiry) && connectorExpiry.TryGetDateTimeOffset(out var planExpiry))
                expiresAt = planExpiry;
            return new(proposal.Id,
                root.TryGetProperty("actionType", out var action) ? action.GetString() ?? proposal.ActionType : proposal.ActionType,
                channel.GetString() ?? string.Empty,
                hash.GetString() ?? string.Empty,
                root.TryGetProperty("expectedRevision", out var revision) && revision.ValueKind == JsonValueKind.Number ? revision.GetInt64() : null,
                idempotency.GetString() ?? string.Empty,
                root.TryGetProperty("alwaysRequiresApproval", out var always) && always.GetBoolean(),
                root.TryGetProperty("resourceId", out var resource) && resource.ValueKind == JsonValueKind.String
                    ? resource.GetString() : null,
                fiscalSummary, approvalRoute, expiresAt,
                root.TryGetProperty("accountName", out var account) && account.ValueKind == JsonValueKind.String ? account.GetString() : null,
                root.TryGetProperty("reviewPayload", out var preview) && preview.ValueKind != JsonValueKind.Null
                    ? JsonSerializer.Serialize(preview, new JsonSerializerOptions { WriteIndented = true }) : null);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    private static string? ReadInfrastructureFiscalSummary(JsonElement root)
    {
        if (!root.TryGetProperty("change", out var change) ||
            !change.TryGetProperty("fiscalImpact", out var fiscal) ||
            !fiscal.TryGetProperty("hasFiscalImpact", out var hasFiscal) || !hasFiscal.GetBoolean()) return null;
        var currency = fiscal.TryGetProperty("currency", out var currencyNode) ? currencyNode.GetString() ?? "USD" : "USD";
        var maximum = fiscal.TryGetProperty("maximumAmount", out var maximumNode) && maximumNode.TryGetDecimal(out var amount)
            ? amount.ToString("0.00") : "unknown";
        var recurring = fiscal.TryGetProperty("recurring", out var recurringNode) && recurringNode.GetBoolean();
        var cadence = fiscal.TryGetProperty("renewalCadence", out var cadenceNode) ? cadenceNode.GetString() : null;
        var budget = fiscal.TryGetProperty("budgetStatus", out var budgetNode) ? budgetNode.GetString() : null;
        return $"Maximum initial charge: {currency} {maximum}; renewal: {(recurring ? cadence ?? "recurring" : "none")}; budget: {budget ?? "unknown"}.";
    }

    private static string? ReadInfrastructureApprovalRoute(JsonElement root)
    {
        if (!root.TryGetProperty("approvalRoute", out var route) || route.ValueKind != JsonValueKind.Array) return null;
        return string.Join(" → ", route.EnumerateArray().Select(stage =>
            stage.TryGetProperty("kind", out var kind) ? kind.GetString() : null).Where(x => !string.IsNullOrWhiteSpace(x))!);
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
