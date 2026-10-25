using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Communications;
using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class ChiefLeadershipService
{
    private async Task<IReadOnlyList<ChiefCoverageAssignment>> AssignmentsAsync(Guid organization, CancellationToken token)
    {
        var people = await db.CoreOrganizationUsers.AsNoTracking().Include(x => x.AgentInstallation).ThenInclude(x => x!.PackageVersion)
            .Where(x => x.OrganizationId == organization && x.IsActive && x.ArchivedAt == null).OrderBy(x => x.Id).ToListAsync(token);
        var positions = await db.LeadershipAssignments.AsNoTracking().Where(x => x.OrganizationId == organization && x.EndsAt == null).ToListAsync(token);
        return people.Select(person =>
        {
            var roles = positions.Where(x => x.OrganizationUserId == person.Id).Select(x => x.PositionKey).ToList();
            // Approved installation metadata supplies role identity, never an employee display name.
            if (person.AgentInstallation?.PackageVersion?.ManifestJson is { } manifest)
            {
                try
                {
                    using var doc = JsonDocument.Parse(manifest);
                    if (doc.RootElement.TryGetProperty("rolePolicy", out var policy) && policy.TryGetProperty("declaredRoleKeys", out var keys))
                        roles.AddRange(keys.EnumerateArray().Select(x => x.GetString()!).Where(x => !string.IsNullOrWhiteSpace(x)));
                }
                catch (JsonException) { /* An unavailable role identity cannot establish coverage. */ }
            }
            return new ChiefCoverageAssignment(person.Id, person.AgentInstallationId, person.EmployeeType == EmployeeType.Human, roles.Distinct().Order().ToArray());
        }).ToArray();
    }

    private async Task<string> InputsAsync(Guid organization, CancellationToken token)
    {
        var business = await db.BusinessProfiles.AsNoTracking().Where(x => x.OrganizationId == organization).Select(x => (long?)x.Revision).SingleOrDefaultAsync(token);
        var finance = await db.FinancialOperatingProfiles.AsNoTracking().Where(x => x.OrganizationId == organization).Select(x => (long?)x.Revision).SingleOrDefaultAsync(token);
        var people = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == organization && x.IsActive)
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.Revision, x.RoleId, x.AgentInstallationId, x.ReportsToOrganizationUserId }).ToListAsync(token);
        var assignments = await AssignmentsAsync(organization, token);
        var objectives = await db.CoreStrategicObjectives.AsNoTracking().Where(x => x.OrganizationId == organization)
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.Title, x.Description, x.Status }).ToListAsync(token);
        var workstreams = await db.Workstreams.AsNoTracking().Where(x => x.OrganizationId == organization)
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.Revision, x.AccountableManagerOrganizationUserId, x.Status, x.BudgetAmount, x.BudgetCurrency }).ToListAsync(token);
        return Digest(new { business, finance, people, assignments, objectives, workstreams });
    }

    private async Task<ChiefLeadershipSnapshot> SnapshotAsync(ChiefLeadershipAgenda agenda, OrganizationUser chief, ReadChiefLeadershipRequest request, CancellationToken token)
    {
        var focus = agenda.FocusId is { } focusId ? db.ChiefFocusRecords.Local.SingleOrDefault(x => x.Id == focusId)
            ?? await db.ChiefFocusRecords.AsNoTracking().SingleAsync(x => x.Id == focusId && x.OrganizationId == agenda.Id, token) : null;
        var rows = await RowsAsync(agenda, token);
        var executable = focus is not null && !agenda.Paused;
        // Active hiring and native decisions take precedence over new proactive work.
        var firstGameDirector = agenda.ProfileKey == "game-studio" && rows.Any(x => x.DomainId == "creative-product" && x.ExecutionStatus == "Ready");
        if (rows.Any(x => x.ExecutionStatus is "WaitingHire" or "WaitingDecision" ||
            x.ExecutionStatus == "Blocked" && !(firstGameDirector && x.DomainId == "custom"))) executable = false;
        var leadershipQuestionIds = await db.ChiefFocusPrompts.Where(x => x.OrganizationId == agenda.Id).Select(x => x.Id).ToListAsync(token);
        if (await db.ExecutiveDecisions.AnyAsync(x => x.OrganizationId == agenda.Id && x.RequestingInstallationId == chief.AgentInstallationId &&
            x.Status == ExecutiveDecisionStatus.Pending && !leadershipQuestionIds.Contains(x.Id), token)) executable = false;
        if (await db.WorkforcePlans.AnyAsync(x => x.OrganizationId == agenda.Id && x.RequestingInstallationId == chief.AgentInstallationId &&
            x.Status == ProposalStatus.Pending, token)) executable = false;
        Guid? answered = null;
        if (request.AnswerTurnId is { } answerTurn)
            answered = await (from decision in db.ExecutiveDecisions
                join purpose in db.ChiefFocusPrompts on decision.Id equals purpose.Id
                where decision.OrganizationId == agenda.Id && decision.RequestingInstallationId == chief.AgentInstallationId &&
                    purpose.InstallationId == chief.AgentInstallationId && decision.NextChatTurnId == answerTurn && decision.Status == ExecutiveDecisionStatus.Answered
                select (Guid?)decision.Id).SingleOrDefaultAsync(token);
        var page = rows.Where(x => request.AfterAssessmentId is null || x.Id.CompareTo(request.AfterAssessmentId.Value) > 0)
            .OrderBy(x => x.Id).Take(request.PageSize).ToArray();
        return new(agenda.FocusRevision, focus is null ? null : new(focus.Id, focus.Revision, focus.DomainId, focus.CustomText, focus.DecisionMakerId,
                focus.ConversationId, focus.SourceTurnId, focus.SourceDecisionId, focus.EffectiveAt, focus.SupersedesId),
            page.Select(Map).ToArray(), await AssignmentsAsync(agenda.Id, token), true, agenda.Paused,
            chief.AgentInstallationId!.Value, executable ? rows.Where(x => x.ExecutionStatus == "Ready").OrderBy(x => agenda.ProfileKey == "game-studio" && x.DomainId == "creative-product" ? 0 : 1).FirstOrDefault()?.Id : null,
            await InputsAsync(agenda.Id, token), answered,
            null);
    }

    public async Task<ChiefCoverageAssessment> AssessAsync(Guid organization, Guid installation, RecordChiefAssessmentRequest request, CancellationToken token = default)
    {
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(token) : null;
        await HiringOrganizationLock.AcquireAsync(db, organization, token);
        var chief = await ChiefAsync(organization, installation, token);
        if (await ReplayAsync<ChiefCoverageAssessment>(organization, installation, request.IdempotencyKey, request, token) is { } replay) return replay;
        var agenda = await AgendaAsync(organization, token);
        var snapshot = await SnapshotAsync(agenda, chief, new(), token);
        if (!snapshot.ProactiveEnabled || snapshot.Paused || snapshot.NextAssessmentId != request.AssessmentId)
            throw new DbUpdateConcurrencyException("The assessment is paused, preempted or not eligible. Read current leadership state.");
        var row = await db.ChiefCoverageRecords.SingleAsync(x => x.Id == request.AssessmentId && x.OrganizationId == organization, token);
        if (row.Revision != request.ExpectedRevision || agenda.FocusRevision != request.ExpectedFocusRevision ||
            snapshot.InputFingerprint != request.InputFingerprint)
            throw new DbUpdateConcurrencyException("Leadership inputs changed; assess current state again.");
        var result = request.Result;
        if (result is null || result.EvidenceOrganizationUserIds is null) throw new ArgumentException("A typed result and bounded evidence list are required.");
        if (!new[] { "NeedsCoverage", "InterimCoverage", "AdequatelyCovered", "AtRisk", "Deferred", "NotApplicable" }.Contains(result.CoverageStatus) ||
            !new[] { "None", "Question", "Hire", "External", "Review", "Blocked" }.Contains(result.NextAction) ||
            string.IsNullOrWhiteSpace(result.Rationale) || result.Rationale.Length > 2048 || string.IsNullOrWhiteSpace(result.CoverageModel) || result.CoverageModel.Length > 80 ||
            result.EvidenceOrganizationUserIds.Count > 30 || result.EvidenceOrganizationUserIds.Any(id => !snapshot.Assignments.Any(x => x.OrganizationUserId == id)))
            throw new ArgumentException("The typed coverage result or its evidence references are invalid.");
        if (result.AccountableOwnerId is { } accountable && (!snapshot.Assignments.Any(x => x.OrganizationUserId == accountable) ||
            !result.EvidenceOrganizationUserIds.Contains(accountable))) throw new ArgumentException("Accountability requires current referenced assignment evidence.");
        if (result.CoverageStatus is "AdequatelyCovered" or "InterimCoverage" && result.AccountableOwnerId is null)
            throw new ArgumentException("Supported coverage requires an accountable current owner.");
        if (result.AccountableOwnerId is { } ownerId && row.AnswerDecisionId is null &&
            !snapshot.Assignments.Any(x => x.OrganizationUserId == ownerId && x.RoleKeys.Any(k => k != LeadershipPositionKeys.ChiefExecutiveOfficer && k != LeadershipPositionKeys.ChiefOfStaff)))
            throw new ArgumentException("The CEO title alone does not establish domain coverage. Ask the owner about accountability.");
        if (result.ReviewAt is { } review && (review <= clock.GetUtcNow() || review > clock.GetUtcNow().AddDays(90)))
            throw new ArgumentException("A review must be in the next 90 days.");
        // Validate every branch before touching tracked domain state; a rejected command must
        // leave no pending mutation that a later save in the same scope could accidentally flush.
        if (result.NextAction == "Hire")
        {
            if (result.HumanRequired || result.CoverageStatus is not ("NeedsCoverage" or "AtRisk") ||
                result.RoleKey is not ("product-manager" or "creative-director" or "game-director") ||
                string.IsNullOrWhiteSpace(result.RoleTitle) || result.RoleTitle.Length > 256 ||
                string.IsNullOrWhiteSpace(result.RoleObjective) || result.RoleObjective.Length > 2048)
                throw new ArgumentException("Only a verified gap for a supported CEO-direct agent manager can create a recommendation.");
            var expectedRole = (agenda.ProfileKey, row.DomainId) switch
            {
                ("general" or "saas" or "ecommerce" or "custom", "product") => "product-manager",
                ("professional-services", "offering") => "product-manager",
                ("game-studio", "creative-product") => "game-director",
                ("media-content", "creative") => "creative-director",
                _ => null
            };
            if (result.RoleKey != expectedRole)
                throw new ArgumentException("The requested agent role is not supported for this profile and responsibility. Ask for appropriate coverage instead.");
            if (await db.ChiefCoverageRecords.AnyAsync(x => x.OrganizationId == organization && x.Id != row.Id && x.ExecutionStatus == "WaitingHire", token))
                throw new InvalidOperationException("One unresolved proactive recommendation already exists.");
            if (snapshot.Assignments.Any(x => x.RoleKeys.Contains(result.RoleKey)))
                throw new ArgumentException("Existing leadership ownership must be assessed before another company-level hire.");
        }
        if (result.NextAction == "Question" && (string.IsNullOrWhiteSpace(result.Question) || result.Question.Length > 2048))
            throw new ArgumentException("A bounded question is required.");
        if (result.NextAction == "None" && result.CoverageStatus is not ("AdequatelyCovered" or "NotApplicable" or "Deferred"))
            throw new ArgumentException("An unresolved domain cannot be completed without a next action.");
        if (result.NextAction == "Review" && result.ReviewAt is null) throw new ArgumentException("Review work needs an explicit next review time.");
        row.CoverageStatus = result.CoverageStatus; row.CoverageModel = result.CoverageModel;
        row.AccountableOwnerId = result.AccountableOwnerId; row.Rationale = result.Rationale;
        row.NextAction = result.NextAction; row.ReviewAt = result.ReviewAt; row.InputFingerprint = request.InputFingerprint;
        row.AcceptedResultJson = JsonSerializer.Serialize(result, Json); row.BlockCode = null;
        switch (result.NextAction)
        {
            case "Hire":
                var recommendation = await hiring.UpsertRecommendationAsync(organization, installation,
                    new CSweet.Contracts.Core.UpsertHiringRecommendationRequest(result.RoleTitle!, result.RoleObjective!, null, [], null,
                        $"chief-coverage:{row.Id:N}:{agenda.FocusRevision}") { RoleKey = result.RoleKey, Priority = 1, Headcount = 1 }, token);
                row.RecommendationId = recommendation.Id; row.ExecutionStatus = "WaitingHire";
                break;
            case "Question":
                if (string.IsNullOrWhiteSpace(result.Question) || result.Question.Length > 2048) throw new ArgumentException("A bounded question is required.");
                var focus = await db.ChiefFocusRecords.SingleAsync(x => x.Id == agenda.FocusId && x.OrganizationId == organization, token);
                var key = $"chief-coverage-question:{row.Id:N}:{row.Revision}";
                var message = await communication.SendAsync(organization, focus.ConversationId, chief.Id,
                    new CSweet.Contracts.Communications.SendCommunicationMessageRequest("I need one detail to establish appropriate leadership coverage.", key), token)
                    ?? throw new InvalidOperationException("The owner conversation is unavailable.");
                var question = await decisions.CreateAsync(new(organization, focus.ConversationId, null, message.Message.Id, installation,
                    result.Question, [new("owner", "I will own this", "I accept accountability for this responsibility."),
                        new("external", "Use an external professional", "Track the need; an engagement still requires confirmation."),
                        new("defer", "Review later", "Defer this domain for 30 days.")], "owner", key), token);
                db.ChiefFocusPrompts.Add(new() { Id = question.Id, OrganizationId = organization, InstallationId = installation,
                    ConversationId = focus.ConversationId, ProfileKey = agenda.ProfileKey, ProfileVersion = agenda.ProfileVersion,
                    ExpectedFocusRevision = agenda.FocusRevision, AssessmentId = row.Id, Purpose = "Coverage", IdempotencyKey = key });
                row.DecisionId = question.Id; row.ExecutionStatus = "WaitingDecision";
                break;
            case "External":
                row.ExecutionStatus = "Blocked"; row.BlockCode = "leadership.external_confirmation_required"; break;
            case "Blocked":
                row.ExecutionStatus = "Blocked"; row.BlockCode = "leadership.evidence_missing"; break;
            case "None":
                if (result.CoverageStatus is not ("AdequatelyCovered" or "NotApplicable" or "Deferred"))
                    throw new ArgumentException("An unresolved domain cannot be completed without a next action.");
                row.ExecutionStatus = "Complete"; break;
            default:
                if (result.ReviewAt is null) throw new ArgumentException("Review work needs an explicit next review time.");
                row.ExecutionStatus = "Complete"; break;
        }
        row.Revision++; row.UpdatedAt = clock.GetUtcNow(); agenda.Revision++;
        await ProjectAsync(chief, row, token);
        await AdvanceAsync(agenda, chief, token);
        var response = Map(row);
        Receipt(organization, installation, request.IdempotencyKey, request, response);
        await db.SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
        return response;
    }
}
