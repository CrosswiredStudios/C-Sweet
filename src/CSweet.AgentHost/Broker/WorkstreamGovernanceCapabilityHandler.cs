using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.AgentHost.Broker;

/// <summary>
/// Implements the domain-neutral Workstream governance boundary. Profile payloads remain opaque
/// to this handler except for validation against the registered immutable JSON schema.
/// </summary>
public sealed class WorkstreamGovernanceCapabilityHandler(
    CSweetDbContext db,
    IAuditEventWriter audit,
    AgentEmployeeIdentityResolver identityResolver,
    TimeProvider clock) : IPlatformCapabilityHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlySet<string> Capabilities = new HashSet<string>(StringComparer.Ordinal)
    {
        W.WorkstreamCapabilityNames.ReadV1,
        W.WorkstreamCapabilityNames.PlanProposeV2,
        W.WorkstreamCapabilityNames.ChangeProposeV1,
        W.WorkstreamCapabilityNames.GateReadV1,
        W.WorkstreamCapabilityNames.GateSubmitV1,
        W.WorkstreamCapabilityNames.GateDecideV1,
        W.WorkstreamCapabilityNames.PortfolioReadV1,
        W.WorkstreamCapabilityNames.TeamRosterReadV2,
        W.DecisionCapabilityNames.RequestV1,
        W.DecisionCapabilityNames.ReadV1,
        W.DecisionCapabilityNames.DecideV1
    };

    public bool CanHandle(string capability) => Capabilities.Contains(capability);

    public async IAsyncEnumerable<CapabilityResult> HandleAsync(
        AgentSession session,
        RequestCapability request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return await HandleCoreAsync(session, request, cancellationToken);
    }

    private async Task<CapabilityResult> HandleCoreAsync(
        AgentSession session,
        RequestCapability request,
        CancellationToken token)
    {
        if (session.Grant.RequestedCapabilities?.Contains(request.Capability, StringComparer.Ordinal) != true)
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied, "The installation is not granted this governance capability.");
        if (!Guid.TryParse(session.BusinessId, out var organizationId) ||
            !Guid.TryParse(session.InstallationId, out var installationId))
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied, "The agent identity is invalid.");

        var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.AgentInstallationId == installationId &&
            x.IsActive && x.EmployeeType == EmployeeType.Agent, token);
        if (actor is null)
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied, "The agent is not an active employee.");

        try
        {
            object result = request.Capability switch
            {
                W.WorkstreamCapabilityNames.ReadV1 => await ReadWorkstreamAsync(
                    organizationId, actor.Id, Read<W.ReadWorkstreamRequest>(request), token),
                W.WorkstreamCapabilityNames.PlanProposeV2 => await ProposePlanAsync(
                    organizationId, installationId, actor.Id, Read<W.WorkstreamPlanProposalV2Request>(request), token),
                W.WorkstreamCapabilityNames.ChangeProposeV1 => await ProposeChangeAsync(
                    organizationId, installationId, actor.Id, Read<W.WorkstreamChangeProposalRequest>(request), token),
                W.WorkstreamCapabilityNames.GateReadV1 => await ReadGatesAsync(
                    organizationId, actor.Id, Read<W.ReadWorkstreamGatesRequest>(request), token),
                W.WorkstreamCapabilityNames.GateSubmitV1 => await SubmitGateAsync(
                    organizationId, actor.Id, Read<W.SubmitWorkstreamGateRequest>(request), token),
                W.WorkstreamCapabilityNames.GateDecideV1 => await DecideGateAsync(
                    organizationId, actor.Id, Read<W.DecideWorkstreamGateRequest>(request), token),
                W.WorkstreamCapabilityNames.PortfolioReadV1 => await ReadPortfolioAsync(
                    organizationId, actor.Id, Read<W.ReadPortfolioRequest>(request), token),
                W.WorkstreamCapabilityNames.TeamRosterReadV2 => await ReadRosterV2Async(
                    session, actor.Id, Read<W.TeamRosterV2Request>(request), token),
                W.DecisionCapabilityNames.RequestV1 => await RequestDecisionAsync(
                    organizationId, installationId, actor.Id, Read<W.DecisionRequest>(request), token),
                W.DecisionCapabilityNames.ReadV1 => await ReadDecisionsAsync(
                    organizationId, actor.Id, Read<W.ReadDecisionRequest>(request), token),
                W.DecisionCapabilityNames.DecideV1 => await DecideDecisionAsync(
                    organizationId, actor.Id, Read<W.DecideDecisionRequest>(request), token),
                _ => throw new KeyNotFoundException("The governance capability is not implemented.")
            };
            return Success(request.RequestId, result);
        }
        catch (JsonException exception) { return Failure(request.RequestId, PlatformCapabilityErrorCode.ValidationFailed, exception.Message); }
        catch (ArgumentException exception) { return Failure(request.RequestId, PlatformCapabilityErrorCode.ValidationFailed, exception.Message); }
        catch (UnauthorizedAccessException exception) { return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied, exception.Message); }
        catch (DbUpdateConcurrencyException exception) { return Failure(request.RequestId, PlatformCapabilityErrorCode.Conflict, exception.Message); }
        catch (InvalidOperationException exception) when (exception.Message.StartsWith("approval_required:", StringComparison.Ordinal))
        { return Failure(request.RequestId, PlatformCapabilityErrorCode.ApprovalRequired, exception.Message[18..]); }
        catch (InvalidOperationException exception) { return Failure(request.RequestId, PlatformCapabilityErrorCode.Conflict, exception.Message); }
        catch (KeyNotFoundException exception) { return Failure(request.RequestId, PlatformCapabilityErrorCode.NotFound, exception.Message); }
    }

    private async Task<W.WorkstreamDetail> ReadWorkstreamAsync(
        Guid organizationId, Guid actorId, W.ReadWorkstreamRequest request, CancellationToken token)
    {
        var workstream = await RequireVisibleWorkstreamAsync(organizationId, actorId, request.WorkstreamId, false, token);
        var staffing = await ReadStaffingRequirementsAsync(workstream, token);
        return Map(workstream, staffing);
    }

    private async Task<MutationResponse> ProposePlanAsync(
        Guid organizationId, Guid installationId, Guid actorId,
        W.WorkstreamPlanProposalV2Request request, CancellationToken token)
    {
        ValidateText(request.Name, 1, 240, "Name");
        ValidateText(request.Outcome, 1, 4000, "Outcome");
        ValidateText(request.IdempotencyKey, 1, 200, "Idempotency key");
        if (request.ProfileVersion < 1 || string.IsNullOrWhiteSpace(request.ProfileKey))
            throw new ArgumentException("A valid profile key and version are required.");
        if (request.InitialMilestones.GroupBy(x => x.Key, StringComparer.Ordinal).Any(x => x.Count() > 1))
            throw new ArgumentException("Milestone keys must be unique.");

        var definition = await db.WorkstreamProfileDefinitions.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Key == request.ProfileKey && x.Version == request.ProfileVersion && x.Status == W.WorkstreamProfileStatuses.Active, token)
            ?? throw new ArgumentException("The requested Workstream profile is not active.");
        using (var schema = JsonDocument.Parse(definition.MetadataSchemaJson))
            WorkstreamProfileDefinitionValidator.ValidateProfileData(schema.RootElement, request.ProfileData);
        var staffingRequirements = EvaluateStaffingRequirements(definition.DefinitionJson, request.ProfileData);
        var activeRoleKeys = staffingRequirements.Where(x => x.IsActive).Select(x => x.RoleKey).ToHashSet(StringComparer.Ordinal);
        var authorizedRoleKeys = request.AuthorityEnvelope.AuthorizedStaffingRoleKeys.ToHashSet(StringComparer.Ordinal);
        var unauthorizedRoles = activeRoleKeys.Where(role => !authorizedRoleKeys.Contains(role)).Order(StringComparer.Ordinal).ToArray();
        if (unauthorizedRoles.Length > 0)
            throw new ArgumentException($"The authority envelope must include every active profile staffing role: {string.Join(", ", unauthorizedRoles)}.");
        if (!await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x =>
                x.OrganizationId == organizationId && x.Id == request.AccountableManagerOrganizationUserId && x.IsActive,
                token))
            throw new ArgumentException("The accountable manager must be an active employee of this organization.");
        if (request.InitialTeamId.HasValue && !await db.OrganizationTeams.AsNoTracking().AnyAsync(x =>
                x.OrganizationId == organizationId && x.Id == request.InitialTeamId && x.ArchivedAt == null, token))
            throw new ArgumentException("The initial team must be active in this organization.");
        if (request.InitialSupervisors.GroupBy(x => new { x.SupervisorOrganizationUserId, x.RoleKey }).Any(x => x.Count() > 1) ||
            request.InitialSupervisors.Any(x => string.IsNullOrWhiteSpace(x.RoleKey) || x.RoleKey.Length > 160))
            throw new ArgumentException("Initial supervisor assignments must have unique employees and valid role keys.");
        var supervisorIds = request.InitialSupervisors.Select(x => x.SupervisorOrganizationUserId).Distinct().ToArray();
        if (supervisorIds.Length > 0 && await db.CoreOrganizationUsers.AsNoTracking().CountAsync(x =>
                x.OrganizationId == organizationId && supervisorIds.Contains(x.Id) && x.IsActive, token) != supervisorIds.Length)
            throw new ArgumentException("Every initial supervisor must be an active employee of this organization.");

        var existing = await db.ActionProposals.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.IdempotencyKey == request.IdempotencyKey, token);
        if (existing is not null)
            return new MutationResponse(existing.Status == ProposalStatus.Approved, 0, existing.Id,
                $"The Workstream proposal is {existing.Status}.");

        var proposal = new ActionProposal
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, AgentInstallationId = installationId,
            ActionType = "workstream.create.v2", Summary = $"Create Workstream: {request.Name.Trim()}",
            PayloadJson = BindProposalPayload(request, request.IdempotencyKey, null, null, definition.DefinitionDigest),
            RiskClass = "OrganizationalChange", IdempotencyKey = request.IdempotencyKey,
            Status = ProposalStatus.Pending, CreatedAt = clock.GetUtcNow()
        };
        db.ActionProposals.Add(proposal);
        await db.SaveChangesAsync(token);
        await audit.WriteAsync("workstream.plan.proposed", nameof(ActionProposal), proposal.Id,
            proposal.Summary, JsonSerializer.Serialize(new { organizationId, actorId, request.ProfileKey, request.ProfileVersion,
                definition.DefinitionDigest }, JsonOptions), token);
        return new MutationResponse(false, 0, proposal.Id, "The Workstream plan is awaiting approval.");
    }

    private async Task<MutationResponse> ProposeChangeAsync(
        Guid organizationId, Guid installationId, Guid actorId,
        W.WorkstreamChangeProposalRequest request, CancellationToken token)
    {
        var workstream = await RequireVisibleWorkstreamAsync(organizationId, actorId, request.WorkstreamId, true, token);
        if (workstream.Revision != request.ExpectedRevision)
            throw new DbUpdateConcurrencyException($"Expected revision {request.ExpectedRevision}; current revision is {workstream.Revision}.");
        ValidateText(request.Summary, 1, 1000, "Summary");
        ValidateText(request.Rationale, 1, 4000, "Rationale");
        ValidateText(request.IdempotencyKey, 1, 200, "Idempotency key");
        if (request.Changes.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Changes must be a JSON object.");

        var existing = await db.ActionProposals.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.IdempotencyKey == request.IdempotencyKey, token);
        if (existing is not null)
            return new MutationResponse(existing.Status == ProposalStatus.Approved, request.ExpectedRevision, existing.Id,
                $"The Workstream change proposal is {existing.Status}.");
        var proposal = new ActionProposal
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, AgentInstallationId = installationId,
            ActionType = "workstream.change.v1", Summary = request.Summary.Trim(),
            PayloadJson = BindProposalPayload(request, request.IdempotencyKey, request.WorkstreamId, request.ExpectedRevision, workstream.ProfileDefinitionDigest),
            RiskClass = "OrganizationalChange", IdempotencyKey = request.IdempotencyKey,
            Status = ProposalStatus.Pending, CreatedAt = clock.GetUtcNow()
        };
        db.ActionProposals.Add(proposal);
        await db.SaveChangesAsync(token);
        await audit.WriteAsync("workstream.change.proposed", nameof(ActionProposal), proposal.Id,
            proposal.Summary, JsonSerializer.Serialize(new { organizationId, actorId, request.WorkstreamId, request.ExpectedRevision }, JsonOptions), token);
        return new MutationResponse(false, request.ExpectedRevision, proposal.Id, "The Workstream change is awaiting approval.");
    }

    private async Task<IReadOnlyList<W.WorkstreamGateSummary>> ReadGatesAsync(
        Guid organizationId, Guid actorId, W.ReadWorkstreamGatesRequest request, CancellationToken token)
    {
        await RequireVisibleWorkstreamAsync(organizationId, actorId, request.WorkstreamId, false, token);
        return await db.WorkstreamGates.AsNoTracking().Where(x =>
                x.OrganizationId == organizationId && x.WorkstreamId == request.WorkstreamId &&
                (!request.GateId.HasValue || x.Id == request.GateId))
            .OrderBy(x => x.DueAt).ThenBy(x => x.Key)
            .Select(x => MapGate(x)).ToListAsync(token);
    }

    private async Task<W.WorkstreamGateSummary> SubmitGateAsync(
        Guid organizationId, Guid actorId, W.SubmitWorkstreamGateRequest request, CancellationToken token)
    {
        var workstream = await RequireVisibleWorkstreamAsync(organizationId, actorId, request.WorkstreamId, false, token);
        var gate = await db.WorkstreamGates.SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.WorkstreamId == request.WorkstreamId && x.Id == request.GateId, token) ?? throw new KeyNotFoundException("The gate was not found.");
        if (gate.Revision != request.ExpectedRevision)
            throw new DbUpdateConcurrencyException($"Expected revision {request.ExpectedRevision}; current revision is {gate.Revision}.");
        if (gate.Status is W.WorkstreamGateStatuses.Approved or W.WorkstreamGateStatuses.Rejected)
            throw new InvalidOperationException("A terminal gate cannot be resubmitted.");
        ValidateText(request.Summary, 1, 4000, "Submission summary");
        ValidateText(request.IdempotencyKey, 1, 200, "Idempotency key");
        ValidateEvidence(gate.RequiredEvidenceTypeKeysJson, request.Evidence);
        var now = clock.GetUtcNow();
        gate.Status = W.WorkstreamGateStatuses.Submitted;
        gate.EvidenceJson = JsonSerializer.Serialize(request.Evidence, JsonOptions);
        gate.SubmissionSummary = request.Summary.Trim();
        gate.SubmittedByOrganizationUserId = actorId;
        gate.SubmittedAt = now;
        gate.Revision++;
        AddEvent(organizationId, W.WorkstreamEventNames.GateRequestedV1, workstream, gate.Id, gate.Revision,
            gate.Key, "submitted", new { gate.Id, request.Summary });
        await db.SaveChangesAsync(token);
        await audit.WriteAsync("workstream.gate.submitted", nameof(WorkstreamGateRecord), gate.Id,
            request.Summary, JsonSerializer.Serialize(new { organizationId, actorId, request.WorkstreamId, gate.Revision }, JsonOptions), token);
        return MapGate(gate);
    }

    private async Task<W.WorkstreamGateSummary> DecideGateAsync(
        Guid organizationId, Guid actorId, W.DecideWorkstreamGateRequest request, CancellationToken token)
    {
        var workstream = await RequireVisibleWorkstreamAsync(organizationId, actorId, request.WorkstreamId, true, token);
        var gate = await db.WorkstreamGates.SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.WorkstreamId == request.WorkstreamId && x.Id == request.GateId, token) ?? throw new KeyNotFoundException("The gate was not found.");
        if (gate.Revision != request.ExpectedRevision)
            throw new DbUpdateConcurrencyException($"Expected revision {request.ExpectedRevision}; current revision is {gate.Revision}.");
        if (gate.Status != W.WorkstreamGateStatuses.Submitted)
            throw new InvalidOperationException("Only a submitted gate can be decided.");
        var decision = NormalizeGateDecision(request.Decision);
        ValidateText(request.Rationale, 1, 4000, "Decision rationale");
        ValidateText(request.IdempotencyKey, 1, 200, "Idempotency key");
        if (decision == W.WorkstreamGateStatuses.Approved && request.Findings.Any(x => x.Blocking))
            throw new ArgumentException("A gate with blocking findings cannot be approved.");
        var requiredReviewerRoles = ReadList<string>(gate.RequiredReviewerRoleKeysJson);
        if (requiredReviewerRoles.Contains("human-owner", StringComparer.Ordinal))
            throw new InvalidOperationException("approval_required:This gate requires an authenticated human owner decision.");
        await RequireAuthorityAsync(organizationId, actorId, workstream.Id, $"gate:{gate.Key}:decide", token);
        var now = clock.GetUtcNow();
        gate.Status = decision;
        gate.FindingsJson = JsonSerializer.Serialize(request.Findings, JsonOptions);
        gate.DecisionRationale = request.Rationale.Trim();
        gate.DecidedByOrganizationUserId = actorId;
        gate.DecidedAt = now;
        gate.Revision++;
        AddEvent(organizationId, W.WorkstreamEventNames.GateDecidedV1, workstream, gate.Id, gate.Revision,
            gate.Key, decision, new { gate.Id, decision, request.Rationale, request.Findings });
        await db.SaveChangesAsync(token);
        await audit.WriteAsync("workstream.gate.decided", nameof(WorkstreamGateRecord), gate.Id,
            $"{decision}: {request.Rationale}", JsonSerializer.Serialize(new { organizationId, actorId, request.WorkstreamId, gate.Revision }, JsonOptions), token);
        return MapGate(gate);
    }

    private async Task<W.PortfolioResponse> ReadPortfolioAsync(
        Guid organizationId, Guid actorId, W.ReadPortfolioRequest request, CancellationToken token)
    {
        var supervisedIds = await db.WorkstreamSupervisionAssignments.AsNoTracking().Where(x =>
            x.OrganizationId == organizationId && x.SupervisorOrganizationUserId == actorId && x.EndsAt == null)
            .Select(x => x.WorkstreamId).ToListAsync(token);
        var query = db.Workstreams.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
            (x.AccountableManagerOrganizationUserId == actorId || supervisedIds.Contains(x.Id)));
        if (request.WorkstreamIds is { Count: > 0 }) query = query.Where(x => request.WorkstreamIds.Contains(x.Id));
        if (!request.IncludeClosed) query = query.Where(x => x.Status != WorkstreamStatus.Completed && x.Status != WorkstreamStatus.Cancelled);
        var workstreams = await query.OrderBy(x => x.Name).ToListAsync(token);
        var ids = workstreams.Select(x => x.Id).ToList();
        var teams = await db.WorkstreamTeamAssignments.AsNoTracking().Where(x => ids.Contains(x.WorkstreamId) && x.EndsAt == null).ToListAsync(token);
        var gates = await db.WorkstreamGates.AsNoTracking().Where(x => ids.Contains(x.WorkstreamId)).ToListAsync(token);
        var decisions = await db.WorkstreamDecisions.AsNoTracking().Where(x => ids.Contains(x.WorkstreamId) && x.Status == W.DecisionStatuses.Pending).ToListAsync(token);
        var profileDigests = workstreams.Where(x => !string.IsNullOrWhiteSpace(x.ProfileDefinitionDigest))
            .Select(x => x.ProfileDefinitionDigest!).Distinct(StringComparer.Ordinal).ToArray();
        var definitions = await db.WorkstreamProfileDefinitions.AsNoTracking()
            .Where(x => profileDigests.Contains(x.DefinitionDigest)).ToDictionaryAsync(x => x.DefinitionDigest, token);
        return new W.PortfolioResponse(workstreams.Select(workstream => new W.PortfolioWorkstream(
            Map(workstream, definitions.TryGetValue(workstream.ProfileDefinitionDigest ?? string.Empty, out var definition)
                ? EvaluateStaffingRequirements(definition.DefinitionJson, ReadProfileData(workstream)) : []),
            teams.Where(x => x.WorkstreamId == workstream.Id).OrderByDescending(x => x.StartsAt).Select(MapTeam).FirstOrDefault(),
            gates.Where(x => x.WorkstreamId == workstream.Id).OrderBy(x => x.DueAt).Select(MapGate).ToList(),
            decisions.Where(x => x.WorkstreamId == workstream.Id).OrderBy(x => x.DueAt).Select(x => x.Summary).ToList())).ToList());
    }

    private async Task<TeamRosterV2Response> ReadRosterV2Async(
        AgentSession session, Guid actorId, W.TeamRosterV2Request request, CancellationToken token)
    {
        Guid? teamId = request.TeamId;
        if (request.WorkstreamId.HasValue)
        {
            await RequireVisibleWorkstreamAsync(Guid.Parse(session.BusinessId), actorId, request.WorkstreamId.Value, false, token);
            var assignment = await db.WorkstreamTeamAssignments.AsNoTracking().SingleOrDefaultAsync(x =>
                x.WorkstreamId == request.WorkstreamId && x.EndsAt == null && (!teamId.HasValue || x.TeamId == teamId), token)
                ?? throw new KeyNotFoundException("The Workstream has no matching active team assignment.");
            teamId = assignment.TeamId;
        }
        if (!teamId.HasValue)
        {
            var legacy = await identityResolver.ReadTeamRosterAsync(session, new TeamRosterRequest(request.Page, request.PageSize), token);
            return new TeamRosterV2Response(legacy.Team, request.WorkstreamId);
        }
        var businessId = Guid.Parse(session.BusinessId);
        var visibleBySupervision = request.WorkstreamId.HasValue;
        if (!visibleBySupervision)
        {
            visibleBySupervision = await db.TeamMemberships.AsNoTracking().AnyAsync(x =>
                x.OrganizationId == businessId && x.TeamId == teamId && x.OrganizationUserId == actorId && x.EndedAt == null, token);
        }
        if (!visibleBySupervision) throw new UnauthorizedAccessException("The requested team is outside this employee's scope.");
        return new TeamRosterV2Response(await BuildTeamContextAsync(businessId, actorId, teamId.Value, request.Page, request.PageSize, token), request.WorkstreamId);
    }

    private async Task<W.DecisionRecord> RequestDecisionAsync(
        Guid organizationId, Guid installationId, Guid actorId, W.DecisionRequest request, CancellationToken token)
    {
        var workstream = await RequireVisibleWorkstreamAsync(organizationId, actorId, request.WorkstreamId, false, token);
        ValidateText(request.TypeKey, 1, 200, "Decision type key");
        ValidateText(request.Summary, 1, 4000, "Decision summary");
        ValidateText(request.AuthorityRuleKey, 1, 200, "Authority rule key");
        ValidateText(request.BlockingImpact, 1, 4000, "Blocking impact");
        ValidateText(request.IdempotencyKey, 1, 200, "Idempotency key");
        if (request.TypeData is { } typeData)
        {
            if (typeData.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Decision type data must be a JSON object.");
            if (Encoding.UTF8.GetByteCount(typeData.GetRawText()) > 64 * 1024)
                throw new ArgumentException("Decision type data must not exceed 64 KB.");
        }
        if (request.Options.Count < 2 || request.Options.GroupBy(x => x.Id, StringComparer.Ordinal).Any(x => x.Count() > 1) ||
            request.Options.All(x => x.Id != request.RecommendedOptionId))
            throw new ArgumentException("A decision requires distinct options and a valid recommendation.");
        var existing = await db.WorkstreamDecisions.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.RequestedByInstallationId == installationId && x.IdempotencyKey == request.IdempotencyKey, token);
        if (existing is not null) return MapDecision(existing);
        if (request.SupersedesDecisionId.HasValue && !await db.WorkstreamDecisions.AsNoTracking().AnyAsync(x =>
            x.OrganizationId == organizationId && x.WorkstreamId == request.WorkstreamId && x.Id == request.SupersedesDecisionId, token))
            throw new ArgumentException("The superseded decision was not found in this Workstream.");
        var now = clock.GetUtcNow();
        var decision = new WorkstreamDecisionRecord
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, WorkstreamId = request.WorkstreamId,
            TypeKey = request.TypeKey.Trim(), Summary = request.Summary.Trim(), AuthorityRuleKey = request.AuthorityRuleKey.Trim(),
            OptionsJson = JsonSerializer.Serialize(request.Options, JsonOptions), RecommendedOptionId = request.RecommendedOptionId,
            EvidenceJson = JsonSerializer.Serialize(request.Evidence, JsonOptions), Status = W.DecisionStatuses.Pending,
            TypeDataJson = request.TypeData?.GetRawText(),
            BlockingImpact = request.BlockingImpact.Trim(), RequestedByOrganizationUserId = actorId,
            RequestedByInstallationId = installationId, SupersedesDecisionId = request.SupersedesDecisionId,
            DueAt = request.DueAt, IdempotencyKey = request.IdempotencyKey, CreatedAt = now, UpdatedAt = now
        };
        if (request.SupersedesDecisionId.HasValue)
        {
            var superseded = await db.WorkstreamDecisions.SingleAsync(x => x.Id == request.SupersedesDecisionId, token);
            superseded.Status = W.DecisionStatuses.Superseded; superseded.SupersededByDecisionId = decision.Id;
            superseded.Revision++; superseded.UpdatedAt = now;
        }
        db.WorkstreamDecisions.Add(decision);
        AddEvent(organizationId, W.WorkstreamEventNames.DecisionRequestedV1, workstream, decision.Id, decision.Revision,
            decision.TypeKey, "requested", new { decision.Id, decision.Summary, decision.AuthorityRuleKey, decision.DueAt });
        await db.SaveChangesAsync(token);
        await audit.WriteAsync("workstream.decision.requested", nameof(WorkstreamDecisionRecord), decision.Id,
            decision.Summary, JsonSerializer.Serialize(new { organizationId, actorId, request.WorkstreamId, request.AuthorityRuleKey }, JsonOptions), token);
        return MapDecision(decision);
    }

    private async Task<IReadOnlyList<W.DecisionRecord>> ReadDecisionsAsync(
        Guid organizationId, Guid actorId, W.ReadDecisionRequest request, CancellationToken token)
    {
        if (!request.DecisionId.HasValue && !request.WorkstreamId.HasValue)
            throw new ArgumentException("DecisionId or WorkstreamId is required.");
        var query = db.WorkstreamDecisions.AsNoTracking().Where(x => x.OrganizationId == organizationId);
        if (request.DecisionId.HasValue) query = query.Where(x => x.Id == request.DecisionId);
        if (request.WorkstreamId.HasValue) query = query.Where(x => x.WorkstreamId == request.WorkstreamId);
        if (request.PendingOnly) query = query.Where(x => x.Status == W.DecisionStatuses.Pending);
        var decisions = await query.OrderByDescending(x => x.CreatedAt).ToListAsync(token);
        foreach (var workstreamId in decisions.Select(x => x.WorkstreamId).Distinct())
            await RequireVisibleWorkstreamAsync(organizationId, actorId, workstreamId, false, token);
        return decisions.Select(MapDecision).ToList();
    }

    private async Task<W.DecisionRecord> DecideDecisionAsync(
        Guid organizationId, Guid actorId, W.DecideDecisionRequest request, CancellationToken token)
    {
        var decision = await db.WorkstreamDecisions.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.Id == request.DecisionId, token) ?? throw new KeyNotFoundException("The decision was not found.");
        var workstream = await RequireVisibleWorkstreamAsync(organizationId, actorId, decision.WorkstreamId, true, token);
        if (decision.Revision != request.ExpectedRevision)
            throw new DbUpdateConcurrencyException($"Expected revision {request.ExpectedRevision}; current revision is {decision.Revision}.");
        if (decision.Status != W.DecisionStatuses.Pending) throw new InvalidOperationException("Only a pending decision can be decided.");
        var options = ReadList<W.DecisionOption>(decision.OptionsJson);
        if (options.All(x => x.Id != request.SelectedOptionId)) throw new ArgumentException("The selected option is invalid.");
        ValidateText(request.Rationale, 1, 4000, "Decision rationale");
        ValidateText(request.IdempotencyKey, 1, 200, "Idempotency key");
        await RequireAuthorityAsync(organizationId, actorId, decision.WorkstreamId, decision.AuthorityRuleKey, token);
        var now = clock.GetUtcNow();
        decision.SelectedOptionId = request.SelectedOptionId; decision.Rationale = request.Rationale.Trim();
        decision.Status = W.DecisionStatuses.Decided; decision.DecidedByOrganizationUserId = actorId;
        decision.Revision++; decision.UpdatedAt = now;
        AddEvent(organizationId, W.WorkstreamEventNames.DecisionDecidedV1, workstream, decision.Id, decision.Revision,
            decision.TypeKey, "decided", new { decision.Id, decision.SelectedOptionId, decision.Rationale });
        await db.SaveChangesAsync(token);
        await audit.WriteAsync("workstream.decision.decided", nameof(WorkstreamDecisionRecord), decision.Id,
            request.Rationale, JsonSerializer.Serialize(new { organizationId, actorId, decision.WorkstreamId, request.SelectedOptionId }, JsonOptions), token);
        return MapDecision(decision);
    }

    private async Task<Workstream> RequireVisibleWorkstreamAsync(
        Guid organizationId, Guid actorId, Guid workstreamId, bool managementRequired, CancellationToken token)
    {
        var workstream = await db.Workstreams.SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == workstreamId, token)
            ?? throw new KeyNotFoundException("The Workstream was not found.");
        var manager = workstream.AccountableManagerOrganizationUserId == actorId;
        var supervisor = await db.WorkstreamSupervisionAssignments.AsNoTracking().AnyAsync(x =>
            x.OrganizationId == organizationId && x.WorkstreamId == workstreamId &&
            x.SupervisorOrganizationUserId == actorId && x.EndsAt == null, token);
        if (manager || supervisor) return workstream;
        if (managementRequired) throw new UnauthorizedAccessException("Workstream management authority is required.");
        var teamIds = await db.WorkstreamTeamAssignments.AsNoTracking().Where(x =>
            x.OrganizationId == organizationId && x.WorkstreamId == workstreamId && x.EndsAt == null).Select(x => x.TeamId).ToListAsync(token);
        var member = await db.TeamMemberships.AsNoTracking().AnyAsync(x => x.OrganizationId == organizationId &&
            teamIds.Contains(x.TeamId) && x.OrganizationUserId == actorId && x.EndedAt == null, token);
        if (!member) throw new UnauthorizedAccessException("The Workstream is outside this employee's assigned or supervised scope.");
        return workstream;
    }

    private async Task RequireAuthorityAsync(Guid organizationId, Guid actorId, Guid workstreamId, string actionKey, CancellationToken token)
    {
        var envelope = await db.WorkstreamAuthorityEnvelopes.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.WorkstreamId == workstreamId, token);
        if (envelope is null) throw new InvalidOperationException("approval_required:No authority envelope is configured for this Workstream.");
        if (envelope.ExpiresAt.HasValue && envelope.ExpiresAt <= clock.GetUtcNow())
            throw new InvalidOperationException("approval_required:The Workstream authority envelope has expired.");
        var humanRequired = ReadList<string>(envelope.HumanRequiredActionKeysJson);
        if (humanRequired.Contains(actionKey, StringComparer.Ordinal) || humanRequired.Contains("*", StringComparer.Ordinal))
            throw new InvalidOperationException("approval_required:This action is explicitly human-gated by the Workstream authority envelope.");
        var authorized = ReadList<string>(envelope.AgentAuthorizedActionKeysJson);
        if (!authorized.Contains(actionKey, StringComparer.Ordinal) && !authorized.Contains("*", StringComparer.Ordinal))
            throw new UnauthorizedAccessException($"The authority envelope does not authorize '{actionKey}'.");
    }

    private async Task<AgentTeamContext> BuildTeamContextAsync(
        Guid organizationId, Guid actorId, Guid teamId, int requestedPage, int requestedPageSize, CancellationToken token)
    {
        var page = Math.Clamp(requestedPage, 1, 10_000); var pageSize = Math.Clamp(requestedPageSize, 1, 100);
        var team = await db.OrganizationTeams.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == teamId && x.ArchivedAt == null, token)
            ?? throw new KeyNotFoundException("The team was not found.");
        var members = await db.TeamMemberships.AsNoTracking().Include(x => x.OrganizationUser).ThenInclude(x => x!.Role)
            .Include(x => x.TeamRole).Where(x => x.OrganizationId == organizationId && x.TeamId == teamId &&
                x.EndedAt == null && x.OrganizationUser != null && x.OrganizationUser.IsActive)
            .OrderBy(x => x.OrganizationUserId == team.LeadOrganizationUserId ? 0 : 1)
            .ThenBy(x => x.OrganizationUser!.DisplayName).ToListAsync(token);
        var lead = members.SingleOrDefault(x => x.OrganizationUserId == team.LeadOrganizationUserId)?.OrganizationUser
            ?? throw new InvalidOperationException("The team has no active lead.");
        var pageMembers = members.Skip((page - 1) * pageSize).Take(pageSize).Select(x => new AgentTeammate(
            x.OrganizationUserId.ToString("D"), x.OrganizationUser!.DisplayName, x.OrganizationUser.EmployeeType.ToString(),
            x.OrganizationUser.Role?.Name, x.TeamRole?.Name,
            x.OrganizationUserId == actorId ? "Self" : x.OrganizationUserId == team.LeadOrganizationUserId ? "TeamLead" : "TeamMember", "Active")
        {
            AgentInstallationId = x.OrganizationUser.AgentInstallationId,
            RuntimeEligibility = x.OrganizationUser.AgentInstallationId.HasValue ? "Eligible" : "NotApplicable",
            IsAvailable = true
        }).ToList();
        var coverage = members.GroupBy(x => x.TeamRole?.Name ?? x.OrganizationUser?.Role?.Name ?? "Unspecified")
            .Select(x => new TeamRoleCoverage(x.Key, x.Count())).OrderBy(x => x.Role).ToList();
        return new AgentTeamContext(team.Id.ToString("D"), team.TeamKey, team.Name, team.Revision,
            lead.Id.ToString("D"), lead.DisplayName, pageMembers, coverage, members.Count, page * pageSize < members.Count);
    }

    private void AddEvent(Guid organizationId, string eventType, Workstream workstream,
        Guid aggregateId, long revision, string typeKey, string action, object metadata)
    {
        var now = clock.GetUtcNow();
        var context = new W.AgentWorkContext(organizationId, workstream.Id, null, null, null, null, null,
            Guid.NewGuid(), null, workstream.ProfileKey);
        var data = new W.GenericResourceEvent(Guid.NewGuid(), now, context,
            eventType.Contains("decision", StringComparison.Ordinal) ? "Decision" : "WorkstreamGate",
            aggregateId, revision, typeKey, action, JsonSerializer.SerializeToElement(metadata, JsonOptions));
        db.AgentPlatformEventOutbox.Add(new AgentPlatformEventOutboxItem
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, EventType = eventType,
            DataJson = JsonSerializer.Serialize(data, JsonOptions),
            IdempotencyKey = $"{eventType}:{aggregateId:N}:{revision}",
            Status = AgentPlatformEventOutboxStatus.Pending, NextAttemptAt = now, OccurredAt = now
        });
    }

    private static string BindProposalPayload(object value, string idempotencyKey, Guid? resourceId, long? expectedRevision, string? profileDigest)
    {
        var payload = JsonSerializer.SerializeToElement(value, JsonOptions);
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        return JsonSerializer.Serialize(new
        {
            channelId = "platform-capability", actionType = "workstream-governance",
            payloadHash = Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant(),
            idempotencyKey, resourceId = resourceId?.ToString("D"), expectedRevision,
            alwaysRequiresApproval = true, profileDefinitionDigest = profileDigest, payload
        }, JsonOptions);
    }

    private static void ValidateEvidence(string requiredJson, IReadOnlyList<W.EvidenceReference> evidence)
    {
        var required = ReadList<string>(requiredJson);
        var provided = evidence.Where(x => !string.IsNullOrWhiteSpace(x.TypeKey)).Select(x => x.TypeKey).ToHashSet(StringComparer.Ordinal);
        var missing = required.Where(x => !provided.Contains(x)).ToList();
        if (missing.Count > 0) throw new ArgumentException($"Required evidence is missing: {string.Join(", ", missing)}.");
        if (evidence.Any(x => string.IsNullOrWhiteSpace(x.Kind) || x.ResourceId == Guid.Empty || string.IsNullOrWhiteSpace(x.TypeKey)))
            throw new ArgumentException("Every evidence reference requires kind, resource id, and type key.");
    }

    private static string NormalizeGateDecision(string value) => value.Trim().ToLowerInvariant() switch
    {
        "approve" or "approved" => W.WorkstreamGateStatuses.Approved,
        "changes-required" or "changesrequired" or "request-changes" => W.WorkstreamGateStatuses.ChangesRequired,
        "reject" or "rejected" => W.WorkstreamGateStatuses.Rejected,
        _ => throw new ArgumentException("The gate decision is invalid.")
    };

    private async Task<IReadOnlyList<W.WorkstreamStaffingRequirement>> ReadStaffingRequirementsAsync(
        Workstream workstream, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(workstream.ProfileDefinitionDigest)) return [];
        var definition = await db.WorkstreamProfileDefinitions.AsNoTracking().SingleOrDefaultAsync(x =>
            x.DefinitionDigest == workstream.ProfileDefinitionDigest, token);
        return definition is null ? [] : EvaluateStaffingRequirements(definition.DefinitionJson, ReadProfileData(workstream));
    }

    private static JsonElement ReadProfileData(Workstream workstream) =>
        string.IsNullOrWhiteSpace(workstream.ProfileDataJson)
            ? JsonSerializer.SerializeToElement(new { }, JsonOptions)
            : JsonSerializer.Deserialize<JsonElement>(workstream.ProfileDataJson);

    private static IReadOnlyList<W.WorkstreamStaffingRequirement> EvaluateStaffingRequirements(
        string definitionJson, JsonElement profileData)
    {
        using var definition = JsonDocument.Parse(definitionJson);
        if (!definition.RootElement.TryGetProperty("staffing", out var staffing) || staffing.ValueKind != JsonValueKind.Object)
            return [];
        var result = new List<W.WorkstreamStaffingRequirement>();
        if (staffing.TryGetProperty("requiredRoleKeys", out var requiredRoles) && requiredRoles.ValueKind == JsonValueKind.Array)
            result.AddRange(requiredRoles.EnumerateArray().Select(role =>
                new W.WorkstreamStaffingRequirement(role.GetString()!, false, true, null)));
        if (staffing.TryGetProperty("conditionalRoles", out var conditionalRoles) && conditionalRoles.ValueKind == JsonValueKind.Array)
        {
            foreach (var rule in conditionalRoles.EnumerateArray())
            {
                var predicate = rule.GetProperty("predicate");
                result.Add(new W.WorkstreamStaffingRequirement(
                    rule.GetProperty("roleKey").GetString()!,
                    true,
                    BoundedJsonPredicateEvaluator.Evaluate(profileData,
                        predicate.GetProperty("jsonPath").GetString()!,
                        predicate.GetProperty("operator").GetString()!,
                        predicate.GetProperty("value")),
                    rule.GetProperty("blockingDecisionTypeKey").GetString()));
            }
        }
        return result;
    }

    private static W.WorkstreamDetail Map(Workstream value, IReadOnlyList<W.WorkstreamStaffingRequirement>? staffingRequirements = null) => new(
        value.Id, value.Name, value.Outcome, ReadList<string>(value.SuccessCriteriaJson), value.LifecycleStage,
        value.Status.ToString(), value.AccountableManagerOrganizationUserId ?? Guid.Empty, value.TargetDate,
        value.BudgetAmount, value.BudgetCurrency, value.ProfileKey, value.ProfileVersion,
        string.IsNullOrWhiteSpace(value.ProfileDataJson) ? null : JsonSerializer.Deserialize<JsonElement>(value.ProfileDataJson),
        value.ProfileDefinitionDigest, value.Revision, staffingRequirements);

    private static W.WorkstreamGateSummary MapGate(WorkstreamGateRecord value) => new(
        value.Id, value.WorkstreamId, value.Key, value.Name, value.LifecycleStage, value.Status, value.Revision, value.DueAt);

    private static W.WorkstreamTeamAssignment MapTeam(WorkstreamTeamAssignmentRecord value) => new(
        value.Id, value.WorkstreamId, value.TeamId, value.StartsAt, value.EndsAt, value.Revision);

    private static W.DecisionRecord MapDecision(WorkstreamDecisionRecord value) => new(
        value.Id, value.WorkstreamId, value.TypeKey, value.Summary, value.AuthorityRuleKey,
        ReadList<W.DecisionOption>(value.OptionsJson), value.RecommendedOptionId, value.SelectedOptionId,
        value.Status, value.Rationale, ReadList<W.EvidenceReference>(value.EvidenceJson),
        value.SupersedesDecisionId, value.SupersededByDecisionId, value.DueAt, value.Revision, value.CreatedAt, value.UpdatedAt,
        string.IsNullOrWhiteSpace(value.TypeDataJson) ? null : JsonSerializer.Deserialize<JsonElement>(value.TypeDataJson));

    private static IReadOnlyList<T> ReadList<T>(string? json)
    {
        try { return JsonSerializer.Deserialize<IReadOnlyList<T>>(json ?? "[]", JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    private static T Read<T>(RequestCapability request) =>
        JsonSerializer.Deserialize<T>(request.Payload.Span, JsonOptions) ?? throw new JsonException("The capability payload is required.");

    private static void ValidateText(string? value, int minimum, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length < minimum || value.Trim().Length > maximum)
            throw new ArgumentException($"{name} must contain between {minimum} and {maximum} characters.");
    }

    private static CapabilityResult Success<T>(string requestId, T value) => new()
    {
        RequestId = requestId, Succeeded = true, ContentType = "application/json",
        Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions))
    };

    private static CapabilityResult Failure(string requestId, PlatformCapabilityErrorCode code, string message) => new()
    {
        RequestId = requestId, Succeeded = false, ContentType = "application/json", Error = message,
        Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new PlatformCapabilityError(code, message), JsonOptions))
    };
}
