using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Communications;
using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class ChiefLeadershipService
{
    // IDs bind the immutable answer to a platform-resolved employee, never a display name.
    private static string ConfirmOwnerOption(Guid id) => $"confirm-owner:{id:N}";

    private static bool CoversDomain(ChiefCoverageAssignment assignment, ChiefCoverageRecord row) =>
        assignment.RoleKeys.Any(role => (row.ProfileKey, row.DomainId, role) switch
        {
            ("game-studio", "creative-product", "game-director") => true,
            ("game-studio", "research", "research-lead" or "research-director" or "chief-science-officer") => true,
            (_, "financial", "finance-lead" or "financial-controller" or "chief-financial-officer") => true,
            (_, "legal", "legal-lead" or "legal-counsel" or "chief-legal-officer") => true,
            ("media-content", "creative", "creative-director") => true,
            (_, "product" or "offering", "product-manager") => true,
            _ => false
        });

    private async Task<IReadOnlyList<CreateExecutiveDecisionOption>> CoverageChoicesAsync(Guid organization,
        ChiefCoverageRecord row, ChiefCoverageResult result, IReadOnlyList<ChiefCoverageAssignment> assignments, CancellationToken token)
    {
        var defer = new CreateExecutiveDecisionOption("defer", "Review later", "Defer this responsibility for 30 days.");
        var external = new CreateExecutiveDecisionOption("external", "Arrange qualified external coverage",
            "Track the need; the professional's engagement and qualifications still require confirmation.");
        // An owner's acceptance cannot establish professional qualifications.
        if (result.HumanRequired) return [external, defer];

        var candidates = assignments.Where(x => CoversDomain(x, row))
            .OrderByDescending(x => x.OrganizationUserId == result.AccountableOwnerId).ThenBy(x => x.OrganizationUserId)
            .Take(3).Select(x => x.OrganizationUserId).ToArray();
        if (candidates.Length == 0)
            return [new("owner", "I will own this", "I accept accountability for this responsibility."), external, defer];

        var names = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == organization && candidates.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.DisplayName, token);
        var choices = candidates.Select(id => new CreateExecutiveDecisionOption(ConfirmOwnerOption(id),
            $"Yes, {ShortName(names[id])} is accountable", "Confirm this employee's accountability and capacity for this responsibility.")).ToList();
        if (choices.Count == 1)
            choices.Add(new("owner", "I will own this instead", "Assign accountability for this responsibility to me."));
        if (choices.Count < 3)
            choices.Add(new("reassign", "Choose another accountable manager", "Request an ownership review; no replacement or hire is assumed."));
        choices.Add(defer);
        return choices;
    }

    private static string ShortName(string name) => name.Length <= 115 ? name : name[..112] + "...";

    private static string CoveragePrompt(ChiefCoverageRecord row, ChiefCoverageResult result)
    {
        // This workflow records accountability, professional engagement or deferral. Keep
        // the question aligned with those effects even if the model asks a factual question.
        var context = result.Rationale.Length > 1400 ? result.Rationale[..1397] + "..." : result.Rationale;
        return context + (result.HumanRequired
            ? $"\n\nHow should we arrange qualified professional coverage for {row.Label}?"
            : $"\n\nWho should be accountable for {row.Label}, or should we review this responsibility later?");
    }

    private async Task RefreshLegacyCoverageQuestionsAsync(ChiefLeadershipAgenda agenda, OrganizationUser chief,
        IReadOnlyList<ChiefCoverageRecord> rows, CancellationToken token)
    {
        var assignments = await AssignmentsAsync(agenda.Id, token);
        foreach (var row in rows.Where(x => x.ExecutionStatus == "WaitingDecision" && x.DecisionId != null && x.AcceptedResultJson != null))
        {
            var prior = await db.ExecutiveDecisions.SingleOrDefaultAsync(x => x.Id == row.DecisionId &&
                x.OrganizationId == agenda.Id && x.RequestingInstallationId == chief.AgentInstallationId &&
                x.Status == ExecutiveDecisionStatus.Pending, token);
            if (prior is null) continue;
            var result = JsonSerializer.Deserialize<ChiefCoverageResult>(row.AcceptedResultJson!, Json);
            if (result is null) continue;
            var choices = await CoverageChoicesAsync(agenda.Id, row, result, assignments, token);
            using var stored = JsonDocument.Parse(prior.OptionsJson);
            var oldIds = stored.RootElement.GetProperty("options").EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToArray();
            // Only the known old template is eligible. Preserve answered and newer decisions.
            if (!oldIds.SequenceEqual(new[] { "owner", "external", "defer" }) ||
                choices.Select(x => x.Id).SequenceEqual(oldIds)) continue;
            var key = $"chief-coverage-choices-v2:{prior.Id:N}";
            var replacement = await decisions.CreateAsync(new(agenda.Id, prior.ConversationId, prior.ChatTurnId,
                prior.ConversationMessageId, chief.AgentInstallationId!.Value, CoveragePrompt(row, result), choices, choices[0].Id, key), token);
            db.ChiefFocusPrompts.Add(new() { Id = replacement.Id, OrganizationId = agenda.Id, InstallationId = chief.AgentInstallationId.Value,
                ConversationId = prior.ConversationId, ProfileKey = agenda.ProfileKey, ProfileVersion = agenda.ProfileVersion,
                ExpectedFocusRevision = agenda.FocusRevision, AssessmentId = row.Id, Purpose = "Coverage", IdempotencyKey = key });
            row.DecisionId = replacement.Id; row.Revision++; row.UpdatedAt = clock.GetUtcNow(); agenda.Revision++;
        }
    }
}
