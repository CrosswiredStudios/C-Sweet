using System.Text.Json;
using CSweet.Application.Core;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.Core;

/// <summary>Materializes approval-bound Workstream commands against an immutable profile digest.</summary>
public sealed class WorkstreamManagedActionExecutor(CSweetDbContext db, TimeProvider clock) : IManagedActionExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool CanExecute(string actionType) => actionType is "workstream.create.v2" or "workstream.change.v1";

    public async Task<ManagedActionExecutionResult> ExecuteAsync(
        ActionProposal proposal, OrganizationUser approvingActor, CancellationToken cancellationToken = default)
    {
        if (!CanExecute(proposal.ActionType)) throw new InvalidOperationException("The managed action type is not supported by this executor.");
        using var binding = JsonDocument.Parse(proposal.PayloadJson);
        var root = binding.RootElement;
        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The approval-bound command payload is missing.");
        return proposal.ActionType == "workstream.create.v2"
            ? await CreateAsync(proposal, approvingActor, root, payload, cancellationToken)
            : await ChangeAsync(proposal, approvingActor, root, payload, cancellationToken);
    }

    private async Task<ManagedActionExecutionResult> CreateAsync(
        ActionProposal proposal, OrganizationUser actor, JsonElement binding, JsonElement payload, CancellationToken token)
    {
        var existing = await db.Workstreams.SingleOrDefaultAsync(x => x.SourceProposalId == proposal.Id, token);
        if (existing is not null) return new(existing.Id, existing.Revision, $"Workstream '{existing.Name}' already exists.");
        var request = payload.Deserialize<W.WorkstreamPlanProposalV2Request>(JsonOptions)
            ?? throw new InvalidOperationException("The Workstream command is invalid.");
        if (actor.OrganizationId != proposal.OrganizationId)
            throw new UnauthorizedAccessException("The approver is outside the proposal organization.");
        var manager = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == proposal.OrganizationId && x.Id == request.AccountableManagerOrganizationUserId && x.IsActive, token)
            ?? throw new InvalidOperationException("The selected accountable manager is no longer active.");
        OrganizationTeam? initialTeam = null;
        if (request.InitialTeamId.HasValue)
            initialTeam = await db.OrganizationTeams.AsNoTracking().SingleOrDefaultAsync(x =>
                x.OrganizationId == proposal.OrganizationId && x.Id == request.InitialTeamId.Value && x.ArchivedAt == null, token)
                ?? throw new InvalidOperationException("The selected initial team is no longer active.");
        if (request.InitialSupervisors.GroupBy(x => new { x.SupervisorOrganizationUserId, RoleKey = x.RoleKey.Trim() }).Any(x => x.Count() > 1))
            throw new InvalidOperationException("Initial supervisor assignments must be unique.");
        if (request.InitialSupervisors.Any(x => string.IsNullOrWhiteSpace(x.RoleKey) || x.RoleKey.Length > 160))
            throw new InvalidOperationException("Every initial supervisor requires a valid role key.");
        var supervisorIds = request.InitialSupervisors.Select(x => x.SupervisorOrganizationUserId).Distinct().ToArray();
        var activeSupervisorIds = supervisorIds.Length == 0
            ? []
            : await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == proposal.OrganizationId &&
                    supervisorIds.Contains(x.Id) && x.IsActive)
                .Select(x => x.Id).ToArrayAsync(token);
        if (activeSupervisorIds.Length != supervisorIds.Length)
            throw new InvalidOperationException("One or more initial supervisors are no longer active.");
        var profile = await db.WorkstreamProfileDefinitions.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Key == request.ProfileKey && x.Version == request.ProfileVersion && x.Status == W.WorkstreamProfileStatuses.Active, token)
            ?? throw new InvalidOperationException("The selected Workstream profile is no longer active.");
        var boundDigest = binding.TryGetProperty("profileDefinitionDigest", out var digest) ? digest.GetString() : null;
        if (!string.Equals(profile.DefinitionDigest, boundDigest, StringComparison.Ordinal))
            throw new InvalidOperationException("The profile definition no longer matches the approval-bound digest.");
        using (var schema = JsonDocument.Parse(profile.MetadataSchemaJson))
            WorkstreamProfileDefinitionValidator.ValidateProfileData(schema.RootElement, request.ProfileData);
        ValidateLifecycleStage(profile.DefinitionJson, request.LifecycleStage);
        if (request.InitialMilestones.GroupBy(x => x.Key, StringComparer.Ordinal).Any(x => x.Count() > 1))
            throw new InvalidOperationException("Milestone keys must be unique.");
        if (request.InitialEvidence.GroupBy(x => new { x.Kind, x.ResourceId, x.RevisionId }).Any(x => x.Count() > 1))
            throw new InvalidOperationException("Initial evidence references must be unique.");
        var initialArtifacts = new List<Artifact>();
        foreach (var evidence in request.InitialEvidence.Where(x => x.Kind.Equals("artifact", StringComparison.OrdinalIgnoreCase)))
        {
            var artifact = await db.CoreArtifacts.Include(x => x.Revisions).SingleOrDefaultAsync(x =>
                x.OrganizationId == proposal.OrganizationId && x.Id == evidence.ResourceId, token)
                ?? throw new InvalidOperationException("An initial artifact evidence reference is unavailable.");
            var revision = artifact.Revisions.SingleOrDefault(x => x.Id == evidence.RevisionId)
                ?? throw new InvalidOperationException("An initial artifact evidence revision is unavailable.");
            if (!string.Equals(revision.ContentSha256, evidence.Digest, StringComparison.OrdinalIgnoreCase) ||
                revision.Status != ArtifactRevisionStatus.Accepted)
                throw new InvalidOperationException("Initial artifact evidence must bind an exact accepted revision and digest.");
            if (artifact.WorkstreamId.HasValue)
                throw new InvalidOperationException("Initial artifact evidence is already bound to another Workstream.");
            initialArtifacts.Add(artifact);
        }

        var now = clock.GetUtcNow();
        var workstream = new Workstream
        {
            Id = Guid.NewGuid(), SourceProposalId = proposal.Id, OrganizationId = proposal.OrganizationId,
            StrategicObjectiveId = request.StrategicObjectiveId,
            AccountableManagerOrganizationUserId = manager.Id,
            Name = request.Name.Trim(), Outcome = request.Outcome.Trim(),
            SuccessCriteriaJson = JsonSerializer.Serialize(request.SuccessCriteria, JsonOptions),
            LifecycleStage = request.LifecycleStage, ManagerTitle = manager.Role?.Name ?? "Accountable Manager",
            RequiredCapabilitiesJson = JsonSerializer.Serialize(request.RequiredCapabilities, JsonOptions),
            Status = WorkstreamStatus.Approved, TargetDate = request.TargetDate,
            BudgetAmount = request.ProposedBudgetAmount, BudgetCurrency = request.ProposedBudgetCurrency,
            ProfileKey = profile.Key, ProfileVersion = profile.Version,
            ProfileDataJson = request.ProfileData.GetRawText(), ProfileDefinitionDigest = profile.DefinitionDigest,
            CreatedAt = now, UpdatedAt = now
        };
        db.Workstreams.Add(workstream);
        foreach (var artifact in initialArtifacts)
        {
            artifact.WorkstreamId = workstream.Id;
            artifact.TeamId = initialTeam?.Id;
            artifact.UpdatedAt = now;
        }
        db.WorkstreamAuthorityEnvelopes.Add(new WorkstreamAuthorityEnvelopeRecord
        {
            Id = Guid.NewGuid(), OrganizationId = proposal.OrganizationId, WorkstreamId = workstream.Id,
            MaximumBudgetVariance = request.AuthorityEnvelope.MaximumBudgetVariance,
            MaximumScheduleVarianceDays = request.AuthorityEnvelope.MaximumScheduleVarianceDays,
            AuthorizedStaffingRoleKeysJson = JsonSerializer.Serialize(request.AuthorityEnvelope.AuthorizedStaffingRoleKeys, JsonOptions),
            HumanRequiredActionKeysJson = JsonSerializer.Serialize(request.AuthorityEnvelope.HumanRequiredActionKeys, JsonOptions),
            AgentAuthorizedActionKeysJson = JsonSerializer.Serialize(request.AuthorityEnvelope.AgentAuthorizedActionKeys, JsonOptions),
            ExpiresAt = request.AuthorityEnvelope.ExpiresAt, CreatedAt = now, UpdatedAt = now
        });
        if (initialTeam is not null)
            db.WorkstreamTeamAssignments.Add(new WorkstreamTeamAssignmentRecord
            {
                Id = Guid.NewGuid(), OrganizationId = proposal.OrganizationId, WorkstreamId = workstream.Id,
                TeamId = initialTeam.Id, StartsAt = now
            });
        foreach (var supervisor in request.InitialSupervisors)
            db.WorkstreamSupervisionAssignments.Add(new WorkstreamSupervisionAssignment
            {
                Id = Guid.NewGuid(), OrganizationId = proposal.OrganizationId, WorkstreamId = workstream.Id,
                SupervisorOrganizationUserId = supervisor.SupervisorOrganizationUserId,
                RoleKey = supervisor.RoleKey.Trim(), StartsAt = now
            });
        var position = 0;
        foreach (var milestone in request.InitialMilestones)
        {
            ValidateLifecycleStage(profile.DefinitionJson, milestone.LifecycleStage);
            var milestoneId = Guid.NewGuid();
            db.WorkstreamMilestones.Add(new WorkstreamMilestoneRecord
            {
                Id = milestoneId, OrganizationId = proposal.OrganizationId, WorkstreamId = workstream.Id,
                Key = milestone.Key, Name = milestone.Name, LifecycleStage = milestone.LifecycleStage,
                TargetDate = milestone.TargetDate,
                RequiredEvidenceTypeKeysJson = JsonSerializer.Serialize(milestone.RequiredEvidenceTypeKeys, JsonOptions),
                RequiredReviewerRoleKeysJson = JsonSerializer.Serialize(milestone.RequiredReviewerRoleKeys, JsonOptions),
                Position = position++
            });
            var matchingEvidence = request.InitialEvidence.Where(x =>
                milestone.RequiredEvidenceTypeKeys.Contains(x.TypeKey, StringComparer.Ordinal)).ToList();
            var submitted = milestone.RequiredEvidenceTypeKeys.Count > 0 && milestone.RequiredEvidenceTypeKeys.All(required =>
                matchingEvidence.Any(x => string.Equals(x.TypeKey, required, StringComparison.Ordinal)));
            db.WorkstreamGates.Add(new WorkstreamGateRecord
            {
                Id = Guid.NewGuid(), OrganizationId = proposal.OrganizationId, WorkstreamId = workstream.Id,
                MilestoneId = milestoneId, Key = milestone.Key, Name = milestone.Name,
                LifecycleStage = milestone.LifecycleStage,
                RequiredEvidenceTypeKeysJson = JsonSerializer.Serialize(milestone.RequiredEvidenceTypeKeys, JsonOptions),
                RequiredReviewerRoleKeysJson = JsonSerializer.Serialize(milestone.RequiredReviewerRoleKeys, JsonOptions),
                EvidenceJson = JsonSerializer.Serialize(matchingEvidence, JsonOptions),
                Status = submitted ? W.WorkstreamGateStatuses.Submitted : W.WorkstreamGateStatuses.Pending,
                SubmissionSummary = submitted ? "Initial accepted evidence was submitted with the approval-bound Workstream plan." : null,
                SubmittedAt = submitted ? now : null,
                DueAt = milestone.TargetDate
            });
        }
        return new(workstream.Id, workstream.Revision, $"Created Workstream '{workstream.Name}'.");
    }

    private async Task<ManagedActionExecutionResult> ChangeAsync(
        ActionProposal proposal, OrganizationUser actor, JsonElement binding, JsonElement payload, CancellationToken token)
    {
        var request = payload.Deserialize<W.WorkstreamChangeProposalRequest>(JsonOptions)
            ?? throw new InvalidOperationException("The Workstream change command is invalid.");
        var workstream = await db.Workstreams.SingleOrDefaultAsync(x =>
            x.OrganizationId == proposal.OrganizationId && x.Id == request.WorkstreamId, token)
            ?? throw new InvalidOperationException("The Workstream no longer exists.");
        if (workstream.Revision > request.ExpectedRevision)
        {
            // An already-applied command is identified by the proposal audit binding, not merely by revision.
            var applied = await db.AuditEvents.AsNoTracking().AnyAsync(x => x.EventType == "workstream.change.v1.executed" &&
                x.EntityId == workstream.Id && x.MetadataJson != null && x.MetadataJson.Contains(proposal.Id.ToString()), token);
            if (applied) return new(workstream.Id, workstream.Revision, "The Workstream change was already applied.");
            throw new DbUpdateConcurrencyException("The Workstream changed after the proposal was reviewed.");
        }
        if (workstream.Revision != request.ExpectedRevision)
            throw new DbUpdateConcurrencyException("The Workstream revision no longer matches the approval binding.");
        if (workstream.AccountableManagerOrganizationUserId != actor.Id && !await db.WorkstreamSupervisionAssignments.AsNoTracking().AnyAsync(x =>
                x.WorkstreamId == workstream.Id && x.SupervisorOrganizationUserId == actor.Id && x.EndsAt == null, token))
            throw new UnauthorizedAccessException("Only an accountable manager or active supervisor may approve this change.");
        var boundDigest = binding.TryGetProperty("profileDefinitionDigest", out var digest) ? digest.GetString() : null;
        if (!string.Equals(workstream.ProfileDefinitionDigest, boundDigest, StringComparison.Ordinal))
            throw new InvalidOperationException("The Workstream profile digest changed after the proposal was reviewed.");
        var profile = await db.WorkstreamProfileDefinitions.AsNoTracking().SingleAsync(x =>
            x.Key == workstream.ProfileKey && x.Version == workstream.ProfileVersion && x.DefinitionDigest == workstream.ProfileDefinitionDigest, token);
        ApplyChanges(workstream, profile, request.Changes);
        workstream.Revision++; workstream.UpdatedAt = clock.GetUtcNow();
        return new(workstream.Id, workstream.Revision, $"Updated Workstream '{workstream.Name}'.");
    }

    private static void ApplyChanges(Workstream workstream, WorkstreamProfileDefinitionRecord profile, JsonElement changes)
    {
        foreach (var change in changes.EnumerateObject())
        {
            switch (change.Name)
            {
                case "name": workstream.Name = RequiredString(change.Value, "name", 512); break;
                case "outcome": workstream.Outcome = RequiredString(change.Value, "outcome", 8192); break;
                case "successCriteria" when change.Value.ValueKind == JsonValueKind.Array:
                    workstream.SuccessCriteriaJson = change.Value.GetRawText(); break;
                case "lifecycleStage":
                    var next = RequiredString(change.Value, "lifecycleStage", 80);
                    ValidateLifecycleTransition(profile.DefinitionJson, workstream.LifecycleStage, next);
                    workstream.LifecycleStage = next; break;
                case "targetDate": workstream.TargetDate = change.Value.ValueKind == JsonValueKind.Null ? null : change.Value.GetDateTimeOffset(); break;
                case "budgetAmount": workstream.BudgetAmount = change.Value.ValueKind == JsonValueKind.Null ? null : change.Value.GetDecimal(); break;
                case "budgetCurrency": workstream.BudgetCurrency = change.Value.ValueKind == JsonValueKind.Null ? null : RequiredString(change.Value, "budgetCurrency", 8); break;
                case "profileData":
                    using (var schema = JsonDocument.Parse(profile.MetadataSchemaJson))
                        WorkstreamProfileDefinitionValidator.ValidateProfileData(schema.RootElement, change.Value);
                    workstream.ProfileDataJson = change.Value.GetRawText(); break;
                default: throw new InvalidOperationException($"Unsupported Workstream change field '{change.Name}'.");
            }
        }
    }

    private static void ValidateLifecycleStage(string definitionJson, string stage)
    {
        using var document = JsonDocument.Parse(definitionJson);
        var stages = document.RootElement.GetProperty("lifecycle").GetProperty("stages");
        if (!stages.EnumerateArray().Any(x => x.GetProperty("key").GetString() == stage))
            throw new InvalidOperationException($"Lifecycle stage '{stage}' is not declared by the profile.");
    }

    private static void ValidateLifecycleTransition(string definitionJson, string current, string next)
    {
        if (current == next) return;
        using var document = JsonDocument.Parse(definitionJson);
        var lifecycle = document.RootElement.GetProperty("lifecycle");
        ValidateLifecycleStage(definitionJson, next);
        if (!lifecycle.TryGetProperty("transitions", out var transitions) || transitions.ValueKind != JsonValueKind.Array ||
            !transitions.EnumerateArray().Any(x => x.GetProperty("from").GetString() == current && x.GetProperty("to").GetString() == next))
            throw new InvalidOperationException($"Lifecycle transition '{current}' to '{next}' is not allowed by the profile.");
    }

    private static string RequiredString(JsonElement element, string name, int maximum)
    {
        if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()) || element.GetString()!.Length > maximum)
            throw new InvalidOperationException($"Workstream field '{name}' is invalid.");
        return element.GetString()!.Trim();
    }
}
