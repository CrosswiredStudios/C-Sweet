using System.Text.Json;
using System.Text.RegularExpressions;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.Core;

/// <summary>Human review of the stored, approval-bound project command. Never accepts agent-authored UI.</summary>
public sealed class ProjectApprovalReader(CSweetDbContext db)
{
    public const string ActionType = "workstream.create.v2";
    public const string ReceiptKind = "project-approval-decision";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<Guid, Dictionary<Guid, string>> _peopleByOrganization = [];
    public sealed record Receipt(string Decision, string? Comment, Guid ActorId, string ActorName, Guid? ProjectId);

    public async Task<ApprovalDashboardItemResponse?> ReadAsync(Guid organizationId, Guid actorId, Guid proposalId,
        CancellationToken ct = default)
    {
        var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == actorId &&
            x.OrganizationId == organizationId && x.IsActive, ct);
        var proposal = await db.ActionProposals.AsNoTracking().SingleOrDefaultAsync(x => x.Id == proposalId &&
            x.OrganizationId == organizationId && x.ActionType == ActionType, ct);
        if (actor is null || proposal is null) return null;
        var requester = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.AgentInstallationId == proposal.AgentInstallationId && x.IsActive, ct);
        var configuration = await db.AgentInstallationConfigurations.AsNoTracking().Where(x =>
            x.AgentInstallationId == proposal.AgentInstallationId).Select(x => x.SettingsJson).SingleOrDefaultAsync(ct);
        var authorized = await new ProjectApprovalGovernance(db).CanDecideAsync(proposal, actor, ct);
        if (!authorized && actor.PermissionLevel != OrganizationPermissionLevel.Owner) return null;
        var item = new ApprovalDashboardItemResponse(proposal.Id, ApprovalDashboardKinds.AgentAction,
            "Create project", proposal.Summary, proposal.Status.ToString(), requester?.DisplayName ?? "Creative Director",
            authorized ? actor.DisplayName : "Assigned approver", proposal.CreatedAt, proposal.DecidedAt, "",
            authorized && proposal.Status == ProposalStatus.Pending) { AgentAction = ApprovalDashboardService.ReadManagedAction(proposal) };
        return await EnrichAsync(item, proposal, ct);
    }

    public async Task<ApprovalDashboardItemResponse> EnrichAsync(ApprovalDashboardItemResponse item, ActionProposal proposal,
        CancellationToken ct = default)
    {
        if (proposal.ActionType != ActionType) return item;
        var org = proposal.OrganizationId;
        try
        {
            using var binding = JsonDocument.Parse(proposal.PayloadJson);
            var request = binding.RootElement.GetProperty("payload").Deserialize<W.WorkstreamPlanProposalV2Request>(Json)
                ?? throw new JsonException();
            if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Outcome) ||
                request.AuthorityEnvelope is null || request.SuccessCriteria is null || request.InitialMilestones is null ||
                request.InitialEvidence is null || request.InitialSupervisors is null ||
                request.InitialMilestones.Any(x => x is null) || request.InitialEvidence.Any(x => x is null) || request.InitialSupervisors.Any(x => x is null) ||
                request.AuthorityEnvelope.AuthorizedStaffingRoleKeys is null || request.AuthorityEnvelope.AgentAuthorizedActionKeys is null ||
                request.AuthorityEnvelope.HumanRequiredActionKeys is null || item.AgentAction is null)
                throw new JsonException();
            if (!_peopleByOrganization.TryGetValue(org, out var people))
            {
                people = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == org)
                    .ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
                _peopleByOrganization[org] = people;
            }
            var team = request.InitialTeamId.HasValue ? await db.OrganizationTeams.AsNoTracking().Where(x =>
                x.Id == request.InitialTeamId && x.OrganizationId == org).Select(x => x.Name).SingleOrDefaultAsync(ct) : null;
            var evidenceIds = request.InitialEvidence.Where(x => x.Kind == "artifact").Select(x => x.ResourceId).ToArray();
            var artifacts = await db.CoreArtifacts.AsNoTracking().Include(x => x.Revisions)
                .Where(x => x.OrganizationId == org && evidenceIds.Contains(x.Id)).ToListAsync(ct);
            var documents = request.InitialEvidence.Where(x => x.Kind == "artifact").Select(e =>
            {
                var artifact = artifacts.SingleOrDefault(x => x.Id == e.ResourceId);
                return new ProjectApprovalDocument(artifact?.Title ?? "Supporting document",
                    $"/organizations/{org:D}/documents?artifact={e.ResourceId:D}",
                    artifact?.Revisions.SingleOrDefault(x => x.Id == e.RevisionId) is { } revision
                        ? $"Submitted revision {revision.Number} (see document history)" : "Submitted revision unavailable");
            }).ToList();
            var authority = request.AuthorityEnvelope;
            var review = new ProjectCreationApprovalResponse(request.Name, CleanText(request.Outcome), CleanText(request.Rationale),
                people.GetValueOrDefault(request.AccountableManagerOrganizationUserId, "Project lead unavailable"), team,
                Label(request.LifecycleStage), request.ProposedBudgetAmount, request.ProposedBudgetCurrency, request.TargetDate,
                request.SuccessCriteria.Select(CleanText).ToArray(),
                request.InitialSupervisors.Select(x => $"{people.GetValueOrDefault(x.SupervisorOrganizationUserId, "Supervisor unavailable")} · {Label(x.RoleKey)}").ToArray(),
                authority.AuthorizedStaffingRoleKeys.Select(Label).ToArray(), authority.AgentAuthorizedActionKeys.Select(Label).ToArray(),
                authority.HumanRequiredActionKeys.Select(Label).ToArray(), authority.MaximumBudgetVariance,
                authority.MaximumScheduleVarianceDays, authority.ExpiresAt,
                request.InitialMilestones.Select(x => new ProjectApprovalMilestone(x.Name, Label(x.LifecycleStage), x.TargetDate,
                    (x.RequiredEvidenceTypeKeys ?? []).Select(Label).ToArray(), (x.RequiredReviewerRoleKeys ?? []).Select(Label).ToArray())).ToArray(),
                documents, request.ProfileData.ValueKind == JsonValueKind.Object && request.ProfileData.TryGetProperty("participantIds", out _));
            var key = proposal.Id.ToString("N");
            var receiptJson = await db.PluginOperationalStates.AsNoTracking().Where(x => x.OrganizationId == org &&
                x.AgentInstallationId == proposal.AgentInstallationId && x.Kind == ReceiptKind && x.ExternalKey == key)
                .Select(x => x.PayloadJson).SingleOrDefaultAsync(ct);
            var receipt = receiptJson is null ? null : JsonSerializer.Deserialize<Receipt>(receiptJson, Json);
            var projectId = receipt?.ProjectId ?? await db.Workstreams.AsNoTracking().Where(x =>
                x.OrganizationId == org && x.SourceProposalId == proposal.Id).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
            var routeJson = await db.PluginOperationalStates.AsNoTracking().Where(x => x.OrganizationId == org &&
                x.Kind == "project-approval-route" && x.ExternalKey == key).Select(x => x.PayloadJson).SingleOrDefaultAsync(ct);
            if (routeJson is not null)
            {
                var route = JsonSerializer.Deserialize<ProjectApprovalGovernance.Route>(routeJson, Json)!;
                review = review with { Rationale = review.Rationale + " Escalated for manager review: " + route.Reason };
            }
            return item with { Title = $"Create project: {review.Name}", Summary = review.Outcome, ProjectCreation = review,
                ActionUri = "", DecisionComment = receipt?.Comment, DecisionKind = receipt?.Decision,
                ActualDecisionMaker = receipt?.ActorName, CreatedProjectId = projectId };
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return item with { Title = "Create project", ActionUri = "",
                ReviewError = "The submitted project plan could not be loaded. Ask the requester to submit a complete proposal." };
        }
    }

    public static string Label(string? value)
    {
        var text = Regex.Replace(value ?? "", @"(?<=[a-z])(?=[A-Z])", " ").Replace('-', ' ').Replace('_', ' ').Replace('.', ' ');
        return text.Length == 0 ? "Not specified" : char.ToUpperInvariant(text[0]) + text[1..];
    }
    private static string CleanText(string? value) => Regex.Replace(value ?? "", @"\s*\b[0-9a-fA-F]{64}\b", "").Trim();
}
