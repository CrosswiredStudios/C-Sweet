using System.Text.Json;
using CSweet.Application.Core;
using CSweet.Application.Agents;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Core;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using AgentAvailabilityState = CSweet.Agent.SDK.AgentAvailabilityState;
using AgentCatalogSource = CSweet.Agent.SDK.AgentCatalogSource;

namespace CSweet.Infrastructure.Core;

public sealed class HiringService(
    CSweetDbContext db,
    IOrganizationUserService organizationUsers,
    IAuditEventWriter audit,
    IAgentImportPreviewService? importPreview = null,
    IAgentInstallationService? agentInstallations = null,
    IAgentCatalogService? agentCatalog = null,
    ILocalAgentSourceArchiveService? localAgentArchives = null,
    IPluginArchiveImportService? archiveImport = null,
    IResourceChangeService? resourceChanges = null,
    ITeamService? teams = null) : IHiringService, IAgentHireOrchestrator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public const string ApprovalMessageSource = "HiringWorkflowApproval";

    public async Task<HiringRecommendationResponse> UpsertRecommendationAsync(
        Guid organizationId,
        Guid requestingInstallationId,
        UpsertHiringRecommendationRequest request,
        CancellationToken cancellationToken = default)
    {
        var title = Required(request.Title, 256, nameof(request.Title));
        var objective = Required(request.Objective, 2048, nameof(request.Objective));
        var key = Required(request.IdempotencyKey, 160, nameof(request.IdempotencyKey));
        var references = (request.CandidateReferences ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (references.Count > 3) throw new ArgumentException("A recommendation may contain up to three ranked candidates.");
        if (request.Priority is < 1 or > 100) throw new ArgumentException("Priority must be between 1 and 100, where 1 is highest.");
        if (references.Count == 0 && !string.IsNullOrWhiteSpace(request.RecommendedCandidateReference))
            throw new ArgumentException("A recommendation without candidates cannot select a recommended candidate.");
        if (references.Count > 0 && (string.IsNullOrWhiteSpace(request.RecommendedCandidateReference) ||
            !references.Contains(request.RecommendedCandidateReference, StringComparer.Ordinal)))
            throw new ArgumentException("The recommended candidate must be in the ranked candidate list.");
        var candidates = await ResolveCandidatesAsync(organizationId, references, cancellationToken);
        if (candidates.Count != references.Count) throw new ArgumentException("One or more candidate references are invalid or expired.");
        if (request.WorkstreamId.HasValue && !await db.Workstreams.AsNoTracking().AnyAsync(x =>
                x.Id == request.WorkstreamId && x.OrganizationId == organizationId, cancellationToken))
            throw new ArgumentException("The workstream does not belong to this organization.");
        Guid? approvedTeamId = request.TeamId;
        if (request.SourceResourceChangeRequestId.HasValue)
        {
            var approvedChange = await db.ResourceChangeRequests.AsNoTracking()
                .Include(x => x.Roles)
                .SingleOrDefaultAsync(x =>
                    x.Id == request.SourceResourceChangeRequestId.Value &&
                    x.OrganizationId == organizationId &&
                    x.Status == ResourceChangeRequestStatus.Approved,
                    cancellationToken)
                ?? throw new ArgumentException("The linked approved resource-change request was not found.");
            approvedTeamId = approvedChange.TeamId;
            if (request.TeamId.HasValue && request.TeamId != approvedTeamId)
                throw new UnauthorizedAccessException("The requested team does not match the approved resource change.");
            if (!string.IsNullOrWhiteSpace(request.RoleKey) &&
                !approvedChange.Roles.Any(x => x.IsDesired && x.RoleKey == request.RoleKey && x.TeamId == approvedTeamId))
                throw new UnauthorizedAccessException("The requested role is not part of the approved team plan.");
        }
        if (approvedTeamId.HasValue && !await db.OrganizationTeams.AsNoTracking().AnyAsync(x =>
                x.Id == approvedTeamId && x.OrganizationId == organizationId && x.ArchivedAt == null,
                cancellationToken))
            throw new ArgumentException("The selected team is not active in this organization.");

        var existing = await db.WorkforcePlans.SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.RequestingInstallationId == requestingInstallationId && x.IdempotencyKey == key, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var orderedIds = references.Select(reference => ParseCandidateReference(reference)).ToList();
        var recommendedId = string.IsNullOrWhiteSpace(request.RecommendedCandidateReference)
            ? (Guid?)null
            : ParseCandidateReference(request.RecommendedCandidateReference);
        var plan = existing ?? new WorkforcePlan
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, RequestingInstallationId = requestingInstallationId,
            IdempotencyKey = key, CreatedAt = now, Status = ProposalStatus.Pending
        };
        plan.WorkstreamId = request.WorkstreamId;
        plan.TeamId = approvedTeamId;
        plan.Title = title;
        plan.Objective = objective;
        plan.Priority = request.Priority;
        plan.RoleKey = string.IsNullOrWhiteSpace(request.RoleKey)
            ? null
            : Required(request.RoleKey, 160, nameof(request.RoleKey));
        plan.Headcount = request.Headcount is >= 1 and <= 100
            ? request.Headcount
            : throw new ArgumentException("Headcount must be between 1 and 100.");
        plan.SourceResourceChangeRequestId = request.SourceResourceChangeRequestId;
        plan.RecommendedCandidateId = recommendedId;
        plan.AssignmentsJson = JsonSerializer.Serialize(orderedIds, JsonOptions);
        var recommendedCandidate = recommendedId.HasValue ? candidates.First(x => x.Id == recommendedId.Value) : null;
        plan.EstimatedMonthlyCost = recommendedCandidate?.EstimatedCost;
        plan.Currency = recommendedCandidate?.Currency;
        plan.UpdatedAt = now;
        foreach (var candidate in candidates) candidate.WorkforcePlanId = plan.Id;
        if (existing is null) db.WorkforcePlans.Add(plan);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("hiring.recommendation.upserted", nameof(WorkforcePlan), plan.Id,
            $"Ranked {candidates.Count} candidates for {title}.", cancellationToken: cancellationToken);
        return ToRecommendation(plan, candidates);
    }

    public async Task<HiringRecommendationResponse> ResolveRecommendationAsync(
        Guid organizationId,
        Guid requestingInstallationId,
        ResolveHiringRecommendationRequest request,
        CancellationToken cancellationToken = default)
    {
        _ = Required(request.IdempotencyKey, 160, nameof(request.IdempotencyKey));
        var plan = await db.WorkforcePlans.SingleOrDefaultAsync(x =>
            x.Id == request.RecommendationId &&
            x.OrganizationId == organizationId &&
            x.RequestingInstallationId == requestingInstallationId,
            cancellationToken) ?? throw new ArgumentException("The hiring recommendation was not found.");
        if (!await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x =>
                x.Id == request.ResultOrganizationUserId &&
                x.OrganizationId == organizationId &&
                x.IsActive,
                cancellationToken))
            throw new ArgumentException("The resulting employee was not found.");
        if (plan.Status == ProposalStatus.Pending)
        {
            if (plan.TeamId.HasValue)
            {
                var teamService = teams
                    ?? throw new InvalidOperationException("Team management is unavailable for this hire.");
                await teamService.AssignFromWorkflowAsync(
                    organizationId,
                    plan.TeamId.Value,
                    request.ResultOrganizationUserId,
                    teamRoleId: null,
                    "HiringRecommendation",
                    plan.Id,
                    cancellationToken);
            }
            plan.Status = ProposalStatus.Approved;
            plan.DecidedAt = DateTimeOffset.UtcNow;
            plan.UpdatedAt = plan.DecidedAt.Value;
            await db.SaveChangesAsync(cancellationToken);
            await audit.WriteAsync("hiring.recommendation.resolved", nameof(WorkforcePlan), plan.Id,
                $"Resolved {plan.Title} after employee {request.ResultOrganizationUserId:D} was hired.",
                cancellationToken: cancellationToken);
        }
        var candidates = await ResolveCandidatesAsync(organizationId, ReadIds(plan.AssignmentsJson)
            .Select(CandidateReference).ToList(), cancellationToken);
        return ToRecommendation(plan, candidates);
    }

    public async Task<HiringRecommendationResponse> WithdrawRecommendationAsync(
        Guid organizationId,
        Guid requestingInstallationId,
        WithdrawHiringRecommendationRequest request,
        CancellationToken cancellationToken = default)
    {
        _ = Required(request.Reason, 2048, nameof(request.Reason));
        _ = Required(request.IdempotencyKey, 160, nameof(request.IdempotencyKey));
        var plan = await db.WorkforcePlans.SingleOrDefaultAsync(x =>
            x.Id == request.RecommendationId &&
            x.OrganizationId == organizationId &&
            x.RequestingInstallationId == requestingInstallationId,
            cancellationToken) ?? throw new ArgumentException("The hiring recommendation was not found.");
        if (plan.Status == ProposalStatus.Pending)
        {
            plan.Status = ProposalStatus.Cancelled;
            plan.DecidedAt = DateTimeOffset.UtcNow;
            plan.UpdatedAt = plan.DecidedAt.Value;
            await db.SaveChangesAsync(cancellationToken);
            await audit.WriteAsync("hiring.recommendation.withdrawn", nameof(WorkforcePlan), plan.Id,
                request.Reason, cancellationToken: cancellationToken);
        }
        return ToRecommendation(plan, []);
    }

    public async Task<HiringWorkflowResponse> StageWorkflowAsync(
        Guid organizationId,
        Guid requestingInstallationId,
        StageHiringWorkflowRequest request,
        CancellationToken cancellationToken = default)
    {
        var roleTitle = Required(request.RoleTitle, 160, nameof(request.RoleTitle));
        var key = Required(request.IdempotencyKey, 160, nameof(request.IdempotencyKey));
        var existing = await db.StaffingActionProposals.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.RequestingInstallationId == requestingInstallationId &&
            x.IdempotencyKey == key, cancellationToken);
        if (existing is not null) return ToWorkflow(existing);

        var recommendation = await db.WorkforcePlans.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == request.RecommendationId && x.OrganizationId == organizationId, cancellationToken)
            ?? throw new ArgumentException("The hiring recommendation was not found.");
        var candidateId = ParseCandidateReference(request.CandidateReference);
        if (!ReadIds(recommendation.AssignmentsJson).Contains(candidateId))
            throw new ArgumentException("The selected candidate is not part of this recommendation.");
        var candidate = await db.WorkforceCandidates.AsNoTracking().SingleAsync(x =>
            x.Id == candidateId && x.OrganizationId == organizationId, cancellationToken);
        if (!candidate.IsAvailable) throw new InvalidOperationException("The candidate is no longer available.");
        if (!candidate.IsHuman && !request.ReportsToOrganizationUserId.HasValue)
            throw new ArgumentException("A managing employee is required when hiring an agent.");
        if (request.ReportsToOrganizationUserId.HasValue && !await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x =>
                x.Id == request.ReportsToOrganizationUserId &&
                x.OrganizationId == organizationId &&
                x.IsActive,
                cancellationToken))
            throw new ArgumentException("The proposed manager does not belong to this organization.");

        var requester = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId &&
            x.AgentInstallationId == requestingInstallationId &&
            x.IsActive,
            cancellationToken) ?? throw new UnauthorizedAccessException(
                "The requesting installation is not an active employee.");
        var owners = await db.CoreOrganizationUsers.AsNoTracking().Where(x =>
            x.OrganizationId == organizationId &&
            x.PermissionLevel == OrganizationPermissionLevel.Owner &&
            x.IsActive).ToListAsync(cancellationToken);
        if (owners.Count == 0)
            throw new InvalidOperationException(
                "The organization has no active owner available to approve this hire.");
        if (!request.ConversationId.HasValue || !request.ChatTurnId.HasValue)
            throw new ArgumentException("A submitted hiring workflow must originate from an owner conversation turn.");
        var conversation = await db.CoreConversations.Include(x => x.Participants).SingleOrDefaultAsync(x =>
            x.Id == request.ConversationId.Value &&
            x.OrganizationId == organizationId &&
            x.ArchivedAt == null,
            cancellationToken) ?? throw new ArgumentException("The hiring workflow conversation was not found.");
        var participantIds = conversation.Participants.Where(x => x.LeftAt == null)
            .Select(x => x.OrganizationUserId).ToHashSet();
        var owner = owners.SingleOrDefault(candidate => participantIds.Contains(candidate.Id))
            ?? throw new UnauthorizedAccessException(
                "The hiring workflow conversation does not include an active organization owner.");
        if (conversation.Kind != ConversationKind.DirectHumanAgent ||
            participantIds.Count != 2 ||
            !participantIds.Contains(requester.Id) ||
            !participantIds.Contains(owner.Id))
            throw new UnauthorizedAccessException(
                "A hiring workflow must be attached to the requesting agent's direct owner conversation.");
        var validTurn = await db.ChatTurns.AsNoTracking().Include(x => x.UserMessage).AnyAsync(x =>
            x.Id == request.ChatTurnId.Value &&
            x.OrganizationId == organizationId &&
            x.ConversationId == conversation.Id &&
            x.TargetAgentOrganizationUserId == requester.Id &&
            x.UserMessage != null &&
            x.UserMessage.SenderOrganizationUserId == owner.Id,
            cancellationToken);
        if (!validTurn)
            throw new UnauthorizedAccessException("The hiring workflow must originate from the current owner turn.");

        var snapshot = await BuildWorkflowSnapshotAsync(candidate, roleTitle, request.ReportsToOrganizationUserId,
            request.RequiredGrants ?? [], cancellationToken, teamId: recommendation.TeamId);
        var now = DateTimeOffset.UtcNow;
        var messageId = Guid.NewGuid();
        var workflow = new StaffingActionProposal
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, WorkforcePlanId = recommendation.Id,
            RequestingInstallationId = requestingInstallationId, IdempotencyKey = key,
            ActionType = "install-and-hire", CandidateSource = candidate.Source,
            CandidateId = request.CandidateReference, PayloadJson = JsonSerializer.Serialize(snapshot, JsonOptions),
            Status = ProposalStatus.Pending, CreatedAt = now, SubmittedAt = now,
            ConversationId = conversation.Id, ConversationMessageId = messageId, ChatTurnId = request.ChatTurnId
        };
        db.StaffingActionProposals.Add(workflow);
        db.CoreConversationMessages.Add(new ConversationMessage
        {
            Id = messageId,
            ConversationId = conversation.Id,
            Role = ConversationRole.Assistant,
            Content = $"Approval requested to hire {candidate.DisplayName} as {roleTitle}.",
            CreatedAt = now,
            ChatTurnId = request.ChatTurnId,
            SenderOrganizationUserId = requester.Id,
            CorrelationId = workflow.Id,
            CausationId = request.ChatTurnId,
            DeliveryIntent = CommunicationDeliveryIntent.RequestResponse,
            SourceProvider = ApprovalMessageSource,
            IdempotencyKey = $"hiring-workflow:{workflow.Id:N}"
        });
        conversation.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("hiring.workflow.staged", nameof(StaffingActionProposal), workflow.Id,
            $"Staged {roleTitle} hiring workflow for owner approval.", cancellationToken: cancellationToken);
        return ToWorkflow(workflow);
    }

    public async Task<IReadOnlyList<HiringRecommendationResponse>> ListRecommendationsAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        var plans = await db.WorkforcePlans.AsNoTracking().Where(x =>
                x.OrganizationId == organizationId && x.Status == ProposalStatus.Pending)
            .OrderBy(x => x.Priority).ThenBy(x => x.CreatedAt).ToListAsync(cancellationToken);
        var candidateIds = plans.SelectMany(x => ReadIds(x.AssignmentsJson)).Distinct().ToList();
        var candidates = await db.WorkforceCandidates.AsNoTracking().Where(x => candidateIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var installationIds = plans.Select(x => x.RequestingInstallationId).Distinct().ToList();
        var suggestedBy = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId &&
                        x.AgentInstallationId.HasValue &&
                        installationIds.Contains(x.AgentInstallationId.Value))
            .ToDictionaryAsync(x => x.AgentInstallationId!.Value, x => x.DisplayName, cancellationToken);
        return plans.Select(plan => ToRecommendation(plan, ReadIds(plan.AssignmentsJson)
            .Where(candidates.ContainsKey).Select(id => candidates[id]).ToList(),
            suggestedBy.GetValueOrDefault(plan.RequestingInstallationId))).ToList();
    }

    public async Task<IReadOnlyList<HiringRecommendationResponse>> ListRecommendationsForInstallationAsync(
        Guid organizationId,
        Guid requestingInstallationId,
        CancellationToken cancellationToken = default)
    {
        var plans = await db.WorkforcePlans.AsNoTracking().Where(x =>
                x.OrganizationId == organizationId &&
                x.RequestingInstallationId == requestingInstallationId &&
                x.Status == ProposalStatus.Pending)
            .OrderBy(x => x.Priority).ThenBy(x => x.CreatedAt).ToListAsync(cancellationToken);
        var candidateIds = plans.SelectMany(x => ReadIds(x.AssignmentsJson)).Distinct().ToList();
        var candidates = await db.WorkforceCandidates.AsNoTracking().Where(x => candidateIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var suggestedBy = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId &&
                        x.AgentInstallationId == requestingInstallationId)
            .Select(x => x.DisplayName)
            .FirstOrDefaultAsync(cancellationToken);
        return plans.Select(plan => ToRecommendation(plan, ReadIds(plan.AssignmentsJson)
            .Where(candidates.ContainsKey).Select(id => candidates[id]).ToList(), suggestedBy)).ToList();
    }

    public async Task<HiringDashboardResponse> GetDashboardAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var recommendations = await ListRecommendationsAsync(organizationId, cancellationToken);
        var workflows = await db.StaffingActionProposals.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => x)
            .ToListAsync(cancellationToken);
        return new(recommendations, workflows.Select(ToWorkflow).ToList())
        {
            ResourceChanges = resourceChanges is null
                ? []
                : await resourceChanges.ListForDashboardAsync(organizationId, cancellationToken)
        };
    }

    public async Task<MarketplaceHirePreviewResponse> PreviewMarketplaceHireAsync(
        Guid organizationId,
        Guid applicationUserId,
        PreviewMarketplaceHireRequest request,
        CancellationToken cancellationToken = default)
    {
        var owner = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId &&
            x.ApplicationUserId == applicationUserId &&
            x.IsActive,
            cancellationToken);
        if (owner?.PermissionLevel != OrganizationPermissionLevel.Owner)
            throw new UnauthorizedAccessException("Only an organization owner may review an agent hire.");
        var roleTitle = Required(request.RoleTitle, 160, nameof(request.RoleTitle));
        var employeeDisplayName = Required(
            request.EmployeeDisplayName,
            160,
            nameof(request.EmployeeDisplayName));
        var key = Required(request.IdempotencyKey, 160, nameof(request.IdempotencyKey));
        if (!request.ReportsToOrganizationUserId.HasValue)
            throw new ArgumentException("A managing employee is required when hiring an agent.");
        var existing = await db.StaffingActionProposals.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId &&
            x.RequestingInstallationId == Guid.Empty &&
            x.IdempotencyKey == key,
            cancellationToken);
        if (existing is not null)
        {
            var existingCandidateId = ParseCandidateReference(existing.CandidateId);
            var existingCandidate = await db.WorkforceCandidates.AsNoTracking()
                .SingleAsync(x => x.Id == existingCandidateId, cancellationToken);
            return ToMarketplacePreview(existing, existingCandidate);
        }

        var catalog = agentCatalog ?? throw new InvalidOperationException("The agent catalog service is unavailable.");
        var agent = await catalog.ResolveAsync(organizationId, request.AgentReference, cancellationToken)
            ?? throw new ArgumentException("The selected catalog agent could not be resolved.");
        if (agent.Availability is not AgentAvailabilityState.AvailableToInstall and
            not AgentAvailabilityState.InstalledEnabled)
            throw new InvalidOperationException("The selected agent is not currently available to hire.");
        if (agent.InstallationId.HasValue && await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x =>
                x.OrganizationId == organizationId &&
                x.AgentInstallationId == agent.InstallationId &&
                x.IsActive,
                cancellationToken))
            throw new InvalidOperationException("The selected agent installation already belongs to an active employee.");
        if (!await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x =>
                x.Id == request.ReportsToOrganizationUserId.Value &&
                x.OrganizationId == organizationId &&
                x.IsActive,
                cancellationToken))
            throw new ArgumentException("The proposed manager does not belong to this organization.");

        var source = agent.Source switch
        {
            AgentCatalogSource.Installed => "InstalledPlugin",
            AgentCatalogSource.LocalDirectory => "LocalDirectoryCatalog",
            AgentCatalogSource.FirstPartyCatalog => "CSweetEmbeddedCatalog",
            AgentCatalogSource.Marketplace => "CSweetMarketplace",
            _ => throw new InvalidOperationException("The selected catalog source is unsupported.")
        };
        var candidate = new WorkforceCandidate
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Source = source,
            ExternalCandidateId = agent.InstallationId?.ToString("D") ?? agent.AgentReference,
            DisplayName = agent.Name,
            CapabilitiesJson = JsonSerializer.Serialize(agent.Capabilities, JsonOptions),
            Score = agent.Score,
            EstimatedCost = agent.Price,
            Currency = agent.Currency,
            IsHuman = false,
            IsAvailable = true,
            ExplanationJson = JsonSerializer.Serialize(new
            {
                ResourceType = "Agent",
                Credentials = Array.Empty<string>(),
                Rationale = $"{agent.Trust}. Catalog source: {agent.Source}.",
                RequiredGrants = Array.Empty<string>(),
                agent.RepositoryUrl,
                CatalogReference = agent.AgentReference
            }, JsonOptions)
        };
        db.WorkforceCandidates.Add(candidate);
        var snapshot = await BuildWorkflowSnapshotAsync(
            candidate,
            roleTitle,
            request.ReportsToOrganizationUserId,
            [],
            cancellationToken,
            teamId: request.TeamId,
            objective: $"Hire {roleTitle} through Marketplace.",
            employeeDisplayName: employeeDisplayName);
        var now = DateTimeOffset.UtcNow;
        var workflow = new StaffingActionProposal
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            WorkforcePlanId = Guid.Empty,
            RequestingInstallationId = Guid.Empty,
            IdempotencyKey = key,
            ActionType = "marketplace-install-and-hire",
            CandidateSource = source,
            CandidateId = CandidateReference(candidate.Id),
            PayloadJson = JsonSerializer.Serialize(snapshot, JsonOptions),
            Status = ProposalStatus.Pending,
            CreatedAt = now
        };
        if (request.SupersedesWorkflowId.HasValue)
        {
            var superseded = await db.StaffingActionProposals.SingleOrDefaultAsync(x =>
                x.Id == request.SupersedesWorkflowId.Value &&
                x.OrganizationId == organizationId &&
                x.RequestingInstallationId == Guid.Empty &&
                x.SubmittedAt == null &&
                x.Status == ProposalStatus.Pending,
                cancellationToken);
            if (superseded is not null)
            {
                superseded.Status = ProposalStatus.Cancelled;
                superseded.DecidedByOrganizationUserId = owner.Id;
                superseded.DecisionComment = "Superseded by an updated Marketplace review.";
                superseded.DecidedAt = now;
            }
        }
        db.StaffingActionProposals.Add(workflow);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("hiring.marketplace.previewed", nameof(StaffingActionProposal), workflow.Id,
            $"Prepared an immutable Marketplace review for {employeeDisplayName} ({agent.Name}) as {roleTitle}.",
            cancellationToken: cancellationToken);
        return ToMarketplacePreview(workflow, candidate);
    }

    Task<MarketplaceHirePreviewResponse> IAgentHireOrchestrator.PreviewAsync(
        Guid organizationId,
        Guid applicationUserId,
        PreviewMarketplaceHireRequest request,
        CancellationToken cancellationToken) =>
        PreviewMarketplaceHireAsync(organizationId, applicationUserId, request, cancellationToken);

    Task<HiringWorkflowResponse?> IAgentHireOrchestrator.ConfirmAsync(
        Guid organizationId,
        Guid workflowId,
        Guid applicationUserId,
        ConfirmHiringWorkflowRequest request,
        CancellationToken cancellationToken) =>
        ConfirmWorkflowAsync(organizationId, workflowId, applicationUserId, request, cancellationToken);

    public Task<HiringWorkflowResponse?> ConfirmWorkflowAsync(
        Guid organizationId,
        Guid workflowId,
        Guid applicationUserId,
        ConfirmHiringWorkflowRequest request,
        CancellationToken cancellationToken = default) =>
        DecideWorkflowAsync(
            organizationId,
            workflowId,
            applicationUserId,
            new DecideHiringWorkflowRequest(
                HiringWorkflowDecisionKinds.Approve,
                null,
                request.IdempotencyKey)
            {
                ConfigurationSettings = request.ConfigurationSettings
            },
            cancellationToken);

    public async Task<HiringWorkflowResponse?> DecideWorkflowAsync(
        Guid organizationId,
        Guid workflowId,
        Guid applicationUserId,
        DecideHiringWorkflowRequest request,
        CancellationToken cancellationToken = default)
    {
        _ = Required(request.IdempotencyKey, 160, nameof(request.IdempotencyKey));
        var decision = Required(request.Decision, 16, nameof(request.Decision));
        if (decision is not HiringWorkflowDecisionKinds.Approve and not HiringWorkflowDecisionKinds.Reject)
            throw new ArgumentException("A hiring workflow decision must be Approve or Reject.");

        DateTimeOffset? relationalClaimedAt = null;
        Guid? claimingOwnerId = null;
        try
        {
            if (db.Database.IsRelational())
            {
                var owner = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.OrganizationId == organizationId &&
                    x.ApplicationUserId == applicationUserId &&
                    x.IsActive,
                    cancellationToken);
                if (owner?.PermissionLevel != OrganizationPermissionLevel.Owner)
                    throw new UnauthorizedAccessException("Only an organization owner may decide a hiring workflow.");
                var claimedAt = DateTimeOffset.UtcNow;
                var claimed = await db.StaffingActionProposals
                    .Where(x => x.Id == workflowId &&
                                x.OrganizationId == organizationId &&
                                x.Status == ProposalStatus.Pending &&
                                x.DecidedAt == null)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.DecidedByOrganizationUserId, owner.Id)
                        .SetProperty(x => x.DecidedAt, claimedAt), cancellationToken);
                if (claimed == 0)
                {
                    var current = await db.StaffingActionProposals.AsNoTracking().SingleOrDefaultAsync(x =>
                        x.Id == workflowId && x.OrganizationId == organizationId, cancellationToken);
                    if (current is null) return null;
                    if ((decision == HiringWorkflowDecisionKinds.Approve && current.Status == ProposalStatus.Approved) ||
                        (decision == HiringWorkflowDecisionKinds.Reject && current.Status == ProposalStatus.Rejected))
                        return ToWorkflow(current);
                    throw new InvalidOperationException(
                        current.Status == ProposalStatus.Pending
                            ? "The hiring workflow is already being decided."
                            : "The hiring workflow is no longer pending.");
                }
                relationalClaimedAt = claimedAt;
                claimingOwnerId = owner.Id;
            }
            if (decision == HiringWorkflowDecisionKinds.Reject)
                return await RejectWorkflowCoreAsync(
                    organizationId, workflowId, applicationUserId, request.Comment, cancellationToken);
            return await ConfirmWorkflowCoreAsync(
                organizationId,
                workflowId,
                applicationUserId,
                new ConfirmHiringWorkflowRequest(request.IdempotencyKey)
                {
                    ConfigurationSettings = request.ConfigurationSettings
                },
                cancellationToken);
        }
        catch
        {
            if (relationalClaimedAt.HasValue && claimingOwnerId.HasValue)
            {
                try
                {
                    await db.StaffingActionProposals
                        .Where(x => x.Id == workflowId &&
                                    x.OrganizationId == organizationId &&
                                    x.Status == ProposalStatus.Pending &&
                                    x.DecidedByOrganizationUserId == claimingOwnerId &&
                                    x.DecidedAt == relationalClaimedAt)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(x => x.DecidedByOrganizationUserId, (Guid?)null)
                            .SetProperty(x => x.DecidedAt, (DateTimeOffset?)null), CancellationToken.None);
                }
                catch
                {
                    // Preserve the actionable decision failure if the best-effort claim release also fails.
                }
            }
            throw;
        }
    }

    private async Task<HiringWorkflowResponse?> ConfirmWorkflowCoreAsync(
        Guid organizationId,
        Guid workflowId,
        Guid applicationUserId,
        ConfirmHiringWorkflowRequest request,
        CancellationToken cancellationToken)
    {
        _ = Required(request.IdempotencyKey, 160, nameof(request.IdempotencyKey));
        var owner = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.ApplicationUserId == applicationUserId && x.IsActive,
            cancellationToken);
        if (owner?.PermissionLevel != OrganizationPermissionLevel.Owner)
            throw new UnauthorizedAccessException("Only an organization owner may approve a hiring workflow.");
        var workflow = await db.StaffingActionProposals.SingleOrDefaultAsync(x =>
            x.Id == workflowId && x.OrganizationId == organizationId, cancellationToken);
        if (workflow is null) return null;
        if (workflow.Status == ProposalStatus.Approved) return ToWorkflow(workflow);
        if (workflow.Status != ProposalStatus.Pending)
            throw new InvalidOperationException("The hiring workflow is no longer pending.");

        var snapshot = JsonSerializer.Deserialize<WorkflowSnapshot>(workflow.PayloadJson, JsonOptions)
            ?? throw new InvalidOperationException("The hiring workflow snapshot is invalid.");
        var candidateId = ParseCandidateReference(workflow.CandidateId);
        var candidate = await db.WorkforceCandidates.SingleAsync(x => x.Id == candidateId &&
            x.OrganizationId == organizationId, cancellationToken);
        await RevalidateAsync(organizationId, candidate, snapshot, workflowId, cancellationToken);

        var role = await db.CoreRoles.SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.Name == snapshot.RoleTitle, cancellationToken);
        if (role is null)
        {
            var now = DateTimeOffset.UtcNow;
            role = new Role { Id = Guid.NewGuid(), OrganizationId = organizationId, Name = snapshot.RoleTitle,
                Description = $"Approved through hiring workflow {workflow.Id}.", AuthorityLevel = AuthorityLevel.ExecutionWithApproval,
                CreatedAt = now, UpdatedAt = now };
            db.CoreRoles.Add(role);
            await db.SaveChangesAsync(cancellationToken);
        }

        Guid resultUserId;
        if (candidate.Source == "CurrentStaff")
        {
            var workerId = Guid.Parse(candidate.ExternalCandidateId);
            var employee = await db.CoreOrganizationUsers.SingleAsync(x => x.OrganizationId == organizationId &&
                x.WorkerId == workerId && x.IsActive, cancellationToken);
            employee.RoleId = role.Id;
            if (snapshot.WorkstreamId.HasValue && !await db.Responsibilities.AnyAsync(x =>
                    x.OrganizationUserId == employee.Id && x.WorkstreamId == snapshot.WorkstreamId && x.Status == "Active",
                    cancellationToken))
                db.Responsibilities.Add(new Responsibility { Id = Guid.NewGuid(), OrganizationId = organizationId,
                    OrganizationUserId = employee.Id, WorkstreamId = snapshot.WorkstreamId, Title = snapshot.RoleTitle,
                    Outcome = snapshot.Objective, Status = "Active" });
            await db.SaveChangesAsync(cancellationToken);
            resultUserId = employee.Id;
        }
        else if (candidate.Source == "InstalledPlugin")
        {
            var installationId = Guid.Parse(candidate.ExternalCandidateId);
            var existingEmployee = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
                x.OrganizationId == organizationId && x.AgentInstallationId == installationId && x.IsActive,
                cancellationToken);
            if (existingEmployee is not null)
            {
                resultUserId = existingEmployee.Id;
                goto CompleteWorkflow;
            }
            var result = await organizationUsers.CreateAsync(organizationId, new CreateOrganizationUserRequest(
                ResolveEmployeeDisplayName(snapshot, candidate.DisplayName),
                null,
                (int)OrganizationPermissionLevel.Contributor,
                (int)EmployeeType.Agent,
                role.Id, null, snapshot.ReportsToOrganizationUserId, AgentInstallationId: installationId),
                cancellationToken, applicationUserId,
                workflow.ActionType == "marketplace-install-and-hire" ? "Marketplace" : "HiringWorkflow");
            if (!result.Succeeded || result.OrganizationUser is null)
                throw new InvalidOperationException(result.Message);
            resultUserId = result.OrganizationUser.Id;
        }
        else if (IsInstallableAgentCatalogSource(candidate.Source))
        {
            var embedded = snapshot.EmbeddedAgent
                ?? throw new InvalidOperationException("The catalog agent installation snapshot is missing.");
            var installationService = agentInstallations
                ?? throw new InvalidOperationException("The agent installation service is unavailable.");
            AgentInstallationResponse installation;
            if (embedded.InstallationId.HasValue)
            {
                installation = await installationService.GetAsync(embedded.InstallationId.Value, cancellationToken)
                    ?? throw new InvalidOperationException("The approved catalog agent installation no longer exists.");
            }
            else
            {
                installation = await installationService.InstallAsync(
                    embedded.ImportId,
                    new InstallAgentRequest(
                        organizationId.ToString("D"),
                        embedded.ActivationMode,
                        300,
                        "Skip",
                        embedded.ProvidedCapabilities,
                        embedded.Subscriptions,
                        embedded.Publications,
                        [],
                        embedded.NetworkAccess,
                        86_400,
                        512,
                        100)
                    {
                        GrantedRequestedCapabilities = embedded.RequestedCapabilities,
                        PluginScope = "Organization",
                        ConfigurationSettings = request.ConfigurationSettings
                    },
                    cancellationToken);
                embedded = embedded with { InstallationId = installation.Id };
                snapshot = snapshot with { EmbeddedAgent = embedded };
                workflow.PayloadJson = JsonSerializer.Serialize(snapshot, JsonOptions);
                await db.SaveChangesAsync(cancellationToken);
            }
            var existingEmployee = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
                x.OrganizationId == organizationId && x.AgentInstallationId == installation.Id && x.IsActive,
                cancellationToken);
            if (existingEmployee is not null)
            {
                resultUserId = existingEmployee.Id;
                goto CompleteWorkflow;
            }
            var result = await organizationUsers.CreateAsync(organizationId, new CreateOrganizationUserRequest(
                    ResolveEmployeeDisplayName(snapshot, installation.AgentName),
                    null,
                    (int)OrganizationPermissionLevel.Contributor,
                    (int)EmployeeType.Agent,
                    role.Id,
                    null,
                    snapshot.ReportsToOrganizationUserId,
                    AgentInstallationId: installation.Id),
                cancellationToken,
                applicationUserId,
                workflow.ActionType == "marketplace-install-and-hire" ? "Marketplace" : "HiringWorkflow");
            if (!result.Succeeded || result.OrganizationUser is null)
                throw new InvalidOperationException(result.Message);
            resultUserId = result.OrganizationUser.Id;
        }
        else
        {
            throw new InvalidOperationException("This candidate source cannot be hired until its installation or provider engagement succeeds.");
        }

CompleteWorkflow:
        if (snapshot.TeamId.HasValue)
        {
            var teamService = teams
                ?? throw new InvalidOperationException("Team management is unavailable for this hire.");
            await teamService.AssignFromWorkflowAsync(
                organizationId,
                snapshot.TeamId.Value,
                resultUserId,
                role.Id,
                "HiringWorkflow",
                workflow.Id,
                cancellationToken);
        }
        workflow.Status = ProposalStatus.Approved;
        workflow.ApprovedByOrganizationUserId = owner.Id;
        workflow.DecidedByOrganizationUserId = owner.Id;
        workflow.ResultOrganizationUserId = resultUserId;
        workflow.DecidedAt = DateTimeOffset.UtcNow;
        if (workflow.WorkforcePlanId != Guid.Empty)
        {
            var plan = await db.WorkforcePlans.SingleAsync(x => x.Id == workflow.WorkforcePlanId, cancellationToken);
            plan.Status = ProposalStatus.Approved;
            plan.DecidedAt = workflow.DecidedAt;
            plan.UpdatedAt = workflow.DecidedAt.Value;
        }
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("hiring.workflow.approved", nameof(StaffingActionProposal), workflow.Id,
            $"Owner approved and completed the {snapshot.RoleTitle} workflow.", cancellationToken: cancellationToken);
        return ToWorkflow(workflow);
    }

    private async Task<HiringWorkflowResponse?> RejectWorkflowCoreAsync(
        Guid organizationId,
        Guid workflowId,
        Guid applicationUserId,
        string? comment,
        CancellationToken cancellationToken)
    {
        var owner = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.ApplicationUserId == applicationUserId && x.IsActive,
            cancellationToken);
        if (owner?.PermissionLevel != OrganizationPermissionLevel.Owner)
            throw new UnauthorizedAccessException("Only an organization owner may reject a hiring workflow.");
        var workflow = await db.StaffingActionProposals.SingleOrDefaultAsync(x =>
            x.Id == workflowId && x.OrganizationId == organizationId, cancellationToken);
        if (workflow is null) return null;
        if (workflow.Status == ProposalStatus.Rejected) return ToWorkflow(workflow);
        if (workflow.Status != ProposalStatus.Pending)
            throw new InvalidOperationException("The hiring workflow is no longer pending.");

        workflow.Status = ProposalStatus.Rejected;
        workflow.DecidedByOrganizationUserId = owner.Id;
        workflow.DecisionComment = string.IsNullOrWhiteSpace(comment) ? null : Required(comment, 2048, nameof(comment));
        workflow.DecidedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("hiring.workflow.rejected", nameof(StaffingActionProposal), workflow.Id,
            workflow.DecisionComment ?? "Owner rejected the hiring workflow.", cancellationToken: cancellationToken);
        return ToWorkflow(workflow);
    }

    public async Task<HiringWorkflowResponse?> CancelMarketplacePreviewAsync(
        Guid organizationId,
        Guid workflowId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default)
    {
        var owner = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.ApplicationUserId == applicationUserId && x.IsActive,
            cancellationToken);
        if (owner?.PermissionLevel != OrganizationPermissionLevel.Owner)
            throw new UnauthorizedAccessException("Only an organization owner may cancel a Marketplace review.");
        var workflow = await db.StaffingActionProposals.SingleOrDefaultAsync(x =>
            x.Id == workflowId && x.OrganizationId == organizationId &&
            x.RequestingInstallationId == Guid.Empty && x.SubmittedAt == null,
            cancellationToken);
        if (workflow is null) return null;
        if (workflow.Status == ProposalStatus.Pending)
        {
            workflow.Status = ProposalStatus.Cancelled;
            workflow.DecidedByOrganizationUserId = owner.Id;
            workflow.DecisionComment = "Marketplace review cancelled.";
            workflow.DecidedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        return ToWorkflow(workflow);
    }

    public async Task<IReadOnlyDictionary<Guid, HiringWorkflowApprovalResponse>> ListApprovalCardsAsync(
        Guid organizationId,
        Guid? conversationId = null,
        CancellationToken cancellationToken = default)
    {
        var query = db.StaffingActionProposals.AsNoTracking().Where(x =>
            x.OrganizationId == organizationId &&
            (x.SubmittedAt != null || x.Status != ProposalStatus.Pending));
        if (conversationId.HasValue)
            query = query.Where(x => x.ConversationId == conversationId);
        var workflows = await query.OrderByDescending(x => x.CreatedAt).Take(250).ToListAsync(cancellationToken);
        var candidateIds = workflows.Select(x => ParseCandidateReference(x.CandidateId)).Distinct().ToList();
        var candidates = await db.WorkforceCandidates.AsNoTracking()
            .Where(x => candidateIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var managerIds = workflows.Select(x =>
                JsonSerializer.Deserialize<WorkflowSnapshot>(x.PayloadJson, JsonOptions)?.ReportsToOrganizationUserId)
            .Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
        var managerNames = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => managerIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DisplayName, cancellationToken);
        return workflows.Where(x => candidates.ContainsKey(ParseCandidateReference(x.CandidateId)))
            .ToDictionary(
                x => x.Id,
                x => ToApprovalCard(
                    x,
                    candidates[ParseCandidateReference(x.CandidateId)],
                    managerNames));
    }

    private async Task<WorkflowSnapshot> BuildWorkflowSnapshotAsync(
        WorkforceCandidate candidate,
        string roleTitle,
        Guid? reportsTo,
        IReadOnlyList<string> requiredGrants,
        CancellationToken token,
        Guid? workstreamId = null,
        Guid? teamId = null,
        string? objective = null,
        string? employeeDisplayName = null)
    {
        string? digest = null;
        IReadOnlyList<string> currentGrants = [];
        IReadOnlyList<string> approvedRequiredGrants = requiredGrants
            .Distinct(StringComparer.Ordinal)
            .ToList();
        EmbeddedAgentInstallSnapshot? embeddedAgent = null;
        if (candidate.Source == "InstalledPlugin" && Guid.TryParse(candidate.ExternalCandidateId, out var installationId))
        {
            var installation = await db.AgentInstallations.AsNoTracking().Include(x => x.PackageVersion).Include(x => x.Grant)
                .SingleAsync(x => x.Id == installationId, token);
            digest = installation.PackageVersion?.PackageDigest ?? installation.PackageVersion?.ManifestDigest;
            currentGrants = ReadStrings(installation.Grant?.RequiredCapabilitiesJson);
            if (requiredGrants.Except(currentGrants, StringComparer.Ordinal).Any())
                throw new InvalidOperationException("The installed agent does not currently have all required grants.");
        }
        else if (IsInstallableAgentCatalogSource(candidate.Source))
        {
            var metadata = ReadMetadata(candidate.ExplanationJson);
            AgentImportPreviewResponse preview;
            var isLocalArchive = candidate.Source == "LocalDirectoryCatalog";
            if (isLocalArchive)
            {
                if (string.IsNullOrWhiteSpace(metadata.CatalogReference))
                    throw new InvalidOperationException("The local agent candidate has no resolvable catalog reference.");
                var archiveService = localAgentArchives
                    ?? throw new InvalidOperationException("The local agent archive service is unavailable.");
                var importer = archiveImport
                    ?? throw new InvalidOperationException("The source archive import service is unavailable.");
                var archive = await archiveService.CreateArchiveAsync(metadata.CatalogReference, token);
                await using var stream = new MemoryStream(archive.Content, writable: false);
                preview = await importer.PreviewSourceArchiveAsync(stream, archive.FileName, token);
            }
            else
            {
                var repositoryUrl = metadata.RepositoryUrl;
                if (!string.IsNullOrWhiteSpace(metadata.CatalogReference) && agentCatalog is not null)
                {
                    var resolved = await agentCatalog.ResolveAsync(
                        candidate.OrganizationId,
                        metadata.CatalogReference,
                        token);
                    repositoryUrl = resolved?.RepositoryUrl;
                }
                if (string.IsNullOrWhiteSpace(repositoryUrl))
                    throw new InvalidOperationException("The catalog agent candidate has no installable repository URL.");
                var previewService = importPreview
                    ?? throw new InvalidOperationException("The agent import preview service is unavailable.");
                preview = await previewService.PreviewAsync(new PreviewAgentImportRequest(repositoryUrl), token);
            }
            ValidateCatalogPreview(preview, requiredGrants);

            digest = preview.ManifestDigest;
            currentGrants = preview.RequestedCapabilities
                .Distinct(StringComparer.Ordinal)
                .ToList();
            approvedRequiredGrants = currentGrants;
            embeddedAgent = new EmbeddedAgentInstallSnapshot(
                preview.ImportId,
                preview.RepositoryUrl,
                preview.CommitSha,
                preview.ManifestDigest,
                preview.AgentId,
                string.IsNullOrWhiteSpace(preview.DefaultActivationMode)
                    ? "AlwaysOn"
                    : preview.DefaultActivationMode,
                preview.Capabilities.Distinct(StringComparer.Ordinal).ToList(),
                currentGrants,
                preview.RequestedSubscriptions.Distinct(StringComparer.Ordinal).ToList(),
                preview.RequestedPublications.Distinct(StringComparer.Ordinal).ToList(),
                preview.RequestedNetworkAccess.Distinct(StringComparer.Ordinal).ToList(),
                preview.ConfigurationFields.Where(field => !field.Secret).ToList(),
                isLocalArchive);
        }
        if (candidate.WorkforcePlanId is { } planId)
        {
            var plan = await db.WorkforcePlans.AsNoTracking().SingleAsync(x => x.Id == planId, token);
            workstreamId = plan.WorkstreamId;
            teamId = plan.TeamId;
            objective = plan.Objective;
        }
        if (string.IsNullOrWhiteSpace(objective))
            throw new InvalidOperationException("The hiring workflow objective is missing.");
        return new(roleTitle, reportsTo, workstreamId, teamId, objective, candidate.EstimatedCost, candidate.Currency,
            digest, approvedRequiredGrants, currentGrants, embeddedAgent, employeeDisplayName);
    }

    private async Task RevalidateAsync(Guid organizationId, WorkforceCandidate candidate, WorkflowSnapshot snapshot,
        Guid workflowId, CancellationToken token)
    {
        if (!candidate.IsAvailable) throw new InvalidOperationException("The candidate is no longer available.");
        var profile = await db.FinancialOperatingProfiles.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId, token);
        if (profile?.MaximumConcurrentHires is { } cap)
        {
            var pending = await db.StaffingActionProposals.CountAsync(x => x.OrganizationId == organizationId &&
                x.Status == ProposalStatus.Pending && x.Id != workflowId, token);
            if (pending >= cap) throw new InvalidOperationException("The organization's concurrent hiring cap has been reached.");
        }
        if (snapshot.Price is > 0)
        {
            if (profile?.MaximumMonthlyWorkforceSpend is { } max && snapshot.Price > max)
                throw new InvalidOperationException("The candidate price exceeds the workforce spending control.");
            var now = DateTimeOffset.UtcNow;
            var budget = await db.Budgets.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.IsActive &&
                x.ScopeType == BudgetScopeType.Organization && x.PeriodStart <= now && x.PeriodEnd > now &&
                x.Currency == snapshot.Currency).OrderBy(x => x.LimitAmount).FirstOrDefaultAsync(token);
            if (budget is null || snapshot.Price > budget.LimitAmount)
                throw new InvalidOperationException("The hiring workflow no longer fits an active organization budget.");
        }
        if (candidate.Source == "InstalledPlugin")
        {
            var installationId = Guid.Parse(candidate.ExternalCandidateId);
            var current = await db.AgentInstallations.AsNoTracking().Include(x => x.PackageVersion).Include(x => x.Grant)
                .SingleOrDefaultAsync(x => x.Id == installationId && x.IsEnabled &&
                    x.BusinessId == organizationId.ToString(), token)
                ?? throw new InvalidOperationException("The installed agent is no longer available.");
            var digest = current.PackageVersion?.PackageDigest ?? current.PackageVersion?.ManifestDigest;
            if (!string.Equals(digest, snapshot.PackageDigest, StringComparison.Ordinal))
                throw new InvalidOperationException("The agent package digest changed; create a new approval.");
            var grants = ReadStrings(current.Grant?.RequiredCapabilitiesJson);
            if (snapshot.RequiredGrants.Except(grants, StringComparer.Ordinal).Any())
                throw new InvalidOperationException("The approved grants changed; create a new approval.");
        }
        else if (IsInstallableAgentCatalogSource(candidate.Source))
        {
            var embedded = snapshot.EmbeddedAgent
                ?? throw new InvalidOperationException("The catalog agent installation snapshot is missing.");
            if (embedded.IsLocalArchive)
            {
                var current = await db.AgentPackageVersions.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.Id == embedded.ImportId &&
                    x.ManifestDigest == embedded.ManifestDigest &&
                    x.AgentId == embedded.AgentId,
                    token);
                if (current is null)
                    throw new InvalidOperationException(
                        "The approved local agent snapshot changed or was removed; create a new approval.");
            }
            else
            {
                var previewService = importPreview
                    ?? throw new InvalidOperationException("The agent import preview service is unavailable.");
                var current = await previewService.PreviewAsync(
                    new PreviewAgentImportRequest(embedded.RepositoryUrl, embedded.CommitSha),
                    token);
                if (!string.Equals(current.CommitSha, embedded.CommitSha, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(current.ManifestDigest, embedded.ManifestDigest, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(current.AgentId, embedded.AgentId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The catalog agent source changed after approval was staged; create a new approval.");
                }
            }
        }
        else if (candidate.Source == "CurrentStaff")
        {
            var workerId = Guid.Parse(candidate.ExternalCandidateId);
            var current = await db.CoreWorkers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == workerId && x.IsEnabled &&
                (x.OrganizationId == organizationId || x.OrganizationId == null), token)
                ?? throw new InvalidOperationException("The recommended staff resource is no longer available.");
            if (!await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.OrganizationId == organizationId &&
                    x.WorkerId == workerId && x.IsActive, token))
                throw new InvalidOperationException("The recommended employee is no longer on current staff.");
            var currentPrice = ReadCost(current.CostModelJson);
            if (currentPrice != snapshot.Price)
                throw new InvalidOperationException("The candidate price changed; create a new approval.");
        }
    }

    private async Task<List<WorkforceCandidate>> ResolveCandidatesAsync(Guid organizationId, IReadOnlyList<string> references,
        CancellationToken token)
    {
        var ids = references.Select(ParseCandidateReference).ToList();
        return await db.WorkforceCandidates.Where(x => x.OrganizationId == organizationId && ids.Contains(x.Id))
            .ToListAsync(token);
    }

    private static HiringRecommendationResponse ToRecommendation(
        WorkforcePlan plan,
        IReadOnlyList<WorkforceCandidate> candidates,
        string? suggestedBy = null)
    {
        var byId = candidates.ToDictionary(x => x.Id);
        var ordered = ReadIds(plan.AssignmentsJson).Where(byId.ContainsKey).Select(id => ToCandidate(byId[id])).ToList();
        return new(plan.Id, plan.WorkstreamId, plan.Title, plan.Objective, plan.Status.ToString(),
            plan.RecommendedCandidateId.HasValue ? CandidateReference(plan.RecommendedCandidateId.Value) : null,
            ordered, plan.CreatedAt, plan.UpdatedAt)
        {
            Priority = plan.Priority,
            HiringUrl = $"/organizations/{plan.OrganizationId:D}/marketplace?role={Uri.EscapeDataString(plan.Title)}",
            SuggestedBy = suggestedBy,
            RoleKey = plan.RoleKey,
            Headcount = plan.Headcount,
            SourceResourceChangeRequestId = plan.SourceResourceChangeRequestId,
            TeamId = plan.TeamId
        };
    }

    private static HiringCandidateResponse ToCandidate(WorkforceCandidate candidate)
    {
        var metadata = ReadMetadata(candidate.ExplanationJson);
        return new(CandidateReference(candidate.Id), candidate.Source, candidate.DisplayName,
            metadata.ResourceType ?? (candidate.IsHuman ? "Human" : "Agent"), ReadStrings(candidate.CapabilitiesJson),
            metadata.Credentials, candidate.Score, candidate.EstimatedCost, candidate.Currency,
            candidate.Source is "CurrentStaff" or "InstalledPlugin" or "CSweetEmbeddedCatalog"
                ? "Platform verified"
                : "Provider supplied",
            candidate.IsAvailable,
            candidate.Source == "InstalledPlugin"
                ? "Installed"
                : candidate.Source == "CurrentStaff"
                    ? "On staff"
                    : candidate.Source == "CSweetEmbeddedCatalog"
                        ? "Embedded source available"
                        : "Not installed",
            metadata.RequiredGrants, metadata.Rationale ?? string.Empty);
    }

    private static HiringWorkflowResponse ToWorkflow(StaffingActionProposal workflow)
    {
        var snapshot = JsonSerializer.Deserialize<WorkflowSnapshot>(workflow.PayloadJson, JsonOptions);
        return new(workflow.Id, workflow.WorkforcePlanId, workflow.CandidateId, snapshot?.RoleTitle ?? "Role",
            workflow.Status.ToString(), workflow.Status switch
            {
                ProposalStatus.Pending when workflow.SubmittedAt.HasValue => "Awaiting owner approval.",
                ProposalStatus.Pending => "Marketplace review draft.",
                ProposalStatus.Rejected => "Workflow rejected.",
                ProposalStatus.Cancelled => "Workflow cancelled.",
                _ => "Workflow completed."
            },
            workflow.CreatedAt, workflow.ResultOrganizationUserId);
    }

    private static HiringWorkflowApprovalResponse ToApprovalCard(
        StaffingActionProposal workflow,
        WorkforceCandidate candidate,
        IReadOnlyDictionary<Guid, string> managerNames)
    {
        var snapshot = JsonSerializer.Deserialize<WorkflowSnapshot>(workflow.PayloadJson, JsonOptions)
            ?? throw new InvalidOperationException("The hiring workflow snapshot is invalid.");
        var embedded = snapshot.EmbeddedAgent;
        return new HiringWorkflowApprovalResponse(
            workflow.Id,
            snapshot.RoleTitle,
            workflow.CandidateId,
            candidate.DisplayName,
            candidate.Source,
            ResolveEmployeeDisplayName(snapshot, candidate.DisplayName),
            snapshot.ReportsToOrganizationUserId,
            snapshot.ReportsToOrganizationUserId.HasValue
                ? managerNames.GetValueOrDefault(snapshot.ReportsToOrganizationUserId.Value)
                : null,
            workflow.Status.ToString(),
            candidate.Source == "InstalledPlugin"
                ? "Use the existing enabled installation and create the employee."
                : candidate.Source == "CurrentStaff"
                    ? "Assign the approved role to the existing employee."
                    : "Import the reviewed source snapshot, install it with the approved access, and create the employee.",
            embedded?.RequestedCapabilities ?? snapshot.RequiredGrants,
            embedded?.Subscriptions ?? [],
            embedded?.NetworkAccess ?? [],
            embedded?.ConfigurationFields ?? [],
            workflow.CreatedAt,
            workflow.SubmittedAt,
            workflow.DecidedAt,
            workflow.DecisionComment);
    }

    private static MarketplaceHirePreviewResponse ToMarketplacePreview(
        StaffingActionProposal workflow,
        WorkforceCandidate candidate)
    {
        var snapshot = JsonSerializer.Deserialize<WorkflowSnapshot>(workflow.PayloadJson, JsonOptions)
            ?? throw new InvalidOperationException("The Marketplace hire snapshot is invalid.");
        var metadata = ReadMetadata(candidate.ExplanationJson);
        var embedded = snapshot.EmbeddedAgent;
        return new(
            workflow.Id,
            metadata.CatalogReference ?? candidate.ExternalCandidateId,
            candidate.DisplayName,
            ResolveEmployeeDisplayName(snapshot, candidate.DisplayName),
            snapshot.RoleTitle,
            snapshot.ReportsToOrganizationUserId,
            candidate.Source,
            candidate.Source is "CurrentStaff" or "InstalledPlugin" or "CSweetEmbeddedCatalog"
                ? "Platform verified"
                : "Provider supplied",
            candidate.EstimatedCost,
            candidate.Currency,
            ReadStrings(candidate.CapabilitiesJson),
            embedded?.RequestedCapabilities ?? snapshot.RequiredGrants,
            embedded?.Subscriptions ?? [],
            embedded?.NetworkAccess ?? [],
            candidate.Source == "InstalledPlugin"
                ? "Use the existing enabled installation."
                : "Import an immutable source snapshot, install it with the reviewed grants, then create the employee.",
            workflow.Status.ToString())
        {
            ConfigurationFields = embedded?.ConfigurationFields ?? [],
            TeamId = snapshot.TeamId
        };
    }

    private static string CandidateReference(Guid id) => $"candidate:{id:N}";
    private static Guid ParseCandidateReference(string reference) =>
        reference.StartsWith("candidate:", StringComparison.Ordinal) && Guid.TryParseExact(reference[10..], "N", out var id)
            ? id : throw new ArgumentException("The candidate reference is invalid.");
    private static List<Guid> ReadIds(string json) => JsonSerializer.Deserialize<List<Guid>>(json, JsonOptions) ?? [];
    private static IReadOnlyList<string> ReadStrings(string? json)
    {
        try { return string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<IReadOnlyList<string>>(json, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }
    private static decimal? ReadCost(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("amount", out var value) && value.TryGetDecimal(out var amount)
                ? amount : null;
        }
        catch (JsonException) { return null; }
    }
    private static CandidateMetadata ReadMetadata(string json)
    {
        try { return JsonSerializer.Deserialize<CandidateMetadata>(json, JsonOptions) ?? new(); }
        catch (JsonException) { return new(); }
    }
    private static bool IsInstallableAgentCatalogSource(string source) =>
        source is "CSweetEmbeddedCatalog" or "CSweetMarketplace" or "LocalDirectoryCatalog";

    private static void ValidateCatalogPreview(
        AgentImportPreviewResponse preview,
        IReadOnlyList<string> requiredGrants)
    {
        if (!preview.PluginKind.Equals("Agent", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The catalog source does not contain an agent plugin.");
        if (preview.RequestedPermissions.Count > 0)
            throw new InvalidOperationException("Catalog agents using legacy permissions cannot be installed through hiring.");
        if (preview.RequestedNetworkAccess.Contains("all-public", StringComparer.Ordinal))
            throw new InvalidOperationException("All-public web access requires a separate installation review.");
        if (requiredGrants.Except(preview.RequestedCapabilities, StringComparer.Ordinal).Any())
            throw new InvalidOperationException("The requested grant list contains capabilities not declared by the catalog agent.");
    }
    private static string Required(string? value, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.");
        var result = value.Trim();
        if (result.Length > maximum) throw new ArgumentException($"{name} exceeds {maximum} characters.");
        return result;
    }

    private static string ResolveEmployeeDisplayName(WorkflowSnapshot snapshot, string fallback) =>
        string.IsNullOrWhiteSpace(snapshot.EmployeeDisplayName)
            ? fallback
            : snapshot.EmployeeDisplayName.Trim();

    private sealed record WorkflowSnapshot(string RoleTitle, Guid? ReportsToOrganizationUserId, Guid? WorkstreamId,
        Guid? TeamId,
        string Objective, decimal? Price, string? Currency, string? PackageDigest,
        IReadOnlyList<string> RequiredGrants, IReadOnlyList<string> ApprovedGrants,
        EmbeddedAgentInstallSnapshot? EmbeddedAgent = null,
        string? EmployeeDisplayName = null);
    private sealed record EmbeddedAgentInstallSnapshot(
        Guid ImportId,
        string RepositoryUrl,
        string CommitSha,
        string ManifestDigest,
        string AgentId,
        string ActivationMode,
        IReadOnlyList<string> ProvidedCapabilities,
        IReadOnlyList<string> RequestedCapabilities,
        IReadOnlyList<string> Subscriptions,
        IReadOnlyList<string> Publications,
        IReadOnlyList<string> NetworkAccess,
        IReadOnlyList<PluginConfigurationField> ConfigurationFields,
        bool IsLocalArchive = false,
        Guid? InstallationId = null);
    private sealed record CandidateMetadata
    {
        public string? ResourceType { get; init; }
        public IReadOnlyList<string> Credentials { get; init; } = [];
        public string? Rationale { get; init; }
        public IReadOnlyList<string> RequiredGrants { get; init; } = [];
        public string? RepositoryUrl { get; init; }
        public string? CatalogReference { get; init; }
    }
}
