using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class HiringService
{
    private sealed record Assessment(string? Evidence, string? Selected, string? Question, string? Reason, HiringCandidateDecision? Clarification = null);
    private const string QuestionPrefix = "hiring-author-question:";

    private async Task<HiringCandidateSelectionResponse> AssessCandidatesAsync(WorkforcePlan plan,
        ResourceChangeRoleRecord? role, HiringPolicySettings policy, HiringSelectionMode mode,
        List<AvailableAgent> eligible, string? failure, HiringCandidateDecision? decision, CancellationToken token)
    {
        var saved = plan.HiringAssessmentJson is null ? new Assessment(null, null, null, null) :
            JsonSerializer.Deserialize<Assessment>(plan.HiringAssessmentJson, JsonOptions)!;
        var source = plan.SourceResourceChangeRequestId is Guid sourceId ? await db.ResourceChangeRequests.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == sourceId && x.OrganizationId == plan.OrganizationId, token) : null;
        Guid? author = source is not null && source.RequesterInstallationId != plan.RequestingInstallationId &&
            await db.CoreOrganizationUsers.AnyAsync(x => x.Id == source.RequesterOrganizationUserId && x.IsActive &&
                x.OrganizationId == plan.OrganizationId && x.AgentInstallationId == source.RequesterInstallationId, token)
            ? source.RequesterOrganizationUserId : null;
        var questionKey = QuestionPrefix + plan.Id.ToString("N");
        string? answer = null;
        string? dispatch = null;
        if (saved.Question is not null && author is Guid authorId)
        {
            var chief = await db.CoreOrganizationUsers.Where(x => x.OrganizationId == plan.OrganizationId &&
                x.AgentInstallationId == plan.RequestingInstallationId && x.IsActive).Select(x => x.Id).SingleAsync(token);
            var key = $"communication-message:{chief:N}:{questionKey}";
            var turn = await db.ChatTurns.AsNoTracking().Where(x => x.OrganizationId == plan.OrganizationId &&
                x.TargetAgentOrganizationUserId == authorId && x.UserMessage!.IdempotencyKey == key &&
                x.UserMessage.SenderOrganizationUserId == chief)
                .OrderByDescending(x => x.CreatedAt).Select(x => new { x.Status,
                    Reply = x.AssistantMessage!.SenderOrganizationUserId == authorId ? x.AssistantMessage.Content : null })
                .FirstOrDefaultAsync(token);
            if (turn is null) dispatch = questionKey;
            else if (turn.Status is ChatTurnStatus.Completed or ChatTurnStatus.CompletedWithWarnings or ChatTurnStatus.Failed or ChatTurnStatus.Cancelled)
                answer = string.IsNullOrWhiteSpace(turn.Reply) ? "The plan author supplied no usable reply." : turn.Reply;
        }
        else if (saved.Question is not null)
            answer = "The plan author is unavailable.";

        var brief = new HiringCandidateBrief("", plan.Title, plan.Objective, source?.ProductGoal, role?.Purpose,
            ReadStrings(source?.ConstraintsJson), ReadStrings(role?.RequiredCapabilitiesJson),
            ReadStrings(role?.PreferredSpecializationKeysJson), policy.Publishers, eligible, author, saved.Question, answer, dispatch);
        // Hash the set independently of discovery order. Preserve the presented order when binding the model's index.
        var canonical = brief with { Candidates = eligible.OrderBy(x => x.AgentReference, StringComparer.Ordinal).ToArray() };
        var evidence = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { canonical, mode }, JsonOptions)));
        brief = brief with { Evidence = evidence };
        var selected = eligible.Count == 1 ? eligible[0] : saved.Evidence == evidence ?
            eligible.SingleOrDefault(x => x.AgentReference == saved.Selected) : null;
        var rationale = selected is null ? failure ?? NoSuitableAgentRationale :
            eligible.Count == 1 ? $"{selected.Name} is the only eligible candidate for {plan.Title}." : saved.Reason!;
        if (eligible.Count > 1 && selected is null)
        {
            rationale = saved.Question is not null && answer is null ? WaitingForHiringAuthorRationale : ComparingCandidatesRationale;
            // A stale or concurrently replayed decision only reads the current state.
            if (decision?.Evidence == evidence && (saved.Question is null || answer is not null))
            {
                var choice = eligible.SingleOrDefault(x => x.AgentReference == decision.CandidateReference);
                if (choice is null || string.IsNullOrWhiteSpace(decision.Rationale))
                    throw new ArgumentException("Choose a currently eligible candidate and explain the fit.");
                if (decision.Question is not null)
                {
                    if (author is null || saved.Question is not null || string.IsNullOrWhiteSpace(decision.Question) ||
                        string.IsNullOrWhiteSpace(decision.AlternativeCondition) || choice.AgentReference == decision.AlternativeReference ||
                        !eligible.Any(x => x.AgentReference == decision.AlternativeReference))
                        throw new ArgumentException("A clarification requires a plan author and an answer that favors another eligible candidate.");
                    saved = new(null, null, decision.Question.Trim(), null, decision);
                    brief = brief with { Question = saved.Question, DispatchKey = questionKey };
                    rationale = WaitingForHiringAuthorRationale;
                }
                else
                {
                    selected = choice;
                    rationale = decision.Rationale.Trim();
                    saved = saved with { Evidence = evidence, Selected = choice.AgentReference, Reason = rationale };
                }
                plan.HiringAssessmentJson = JsonSerializer.Serialize(saved, JsonOptions);
                BusinessSuggestionService.Queue(db, plan.OrganizationId, CSweet.Contracts.Realtime.AppRealtimeEvents.HiringRecommendationsChanged, plan.Id);
                if (selected is not null && mode == HiringSelectionMode.Automatic)
                    await QueueReviewAsync(db, plan.OrganizationId, plan.RequestingInstallationId, plan.Id, "submit-" + evidence, token);
                if (decision.Question is not null)
                    await QueueReviewAsync(db, plan.OrganizationId, plan.RequestingInstallationId, plan.Id, "dispatch-" + evidence, token);
            }
        }
        plan.SelectedCatalogAgentJson = selected is null ? null : JsonSerializer.Serialize(selected, JsonOptions);
        plan.SelectionRationale = rationale;
        await db.SaveChangesAsync(token);
        return new(plan.Id, mode, selected, rationale, eligible.Count == 0 ? rationale : null)
            { Brief = eligible.Count > 1 && selected is null ? brief : null };
    }

    internal static async Task WakeAfterHiringAnswerAsync(CSweetDbContext db, ChatTurn turn, CancellationToken token)
    {
        var message = await db.CoreConversationMessages.AsNoTracking().Where(x => x.Id == turn.UserMessageId)
            .Select(x => new { x.IdempotencyKey, x.SenderOrganizationUserId }).SingleOrDefaultAsync(token);
        if (message?.IdempotencyKey is not { } key || message.SenderOrganizationUserId is not Guid sender) return;
        var prefix = $"communication-message:{sender:N}:{QuestionPrefix}";
        if (!key.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(key[prefix.Length..], "N", out var id)) return;
        var plan = await db.WorkforcePlans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id &&
            x.OrganizationId == turn.OrganizationId && x.Status == ProposalStatus.Pending && x.HiringAssessmentJson != null &&
            db.CoreOrganizationUsers.Any(u => u.Id == sender && u.AgentInstallationId == x.RequestingInstallationId) &&
            db.ResourceChangeRequests.Any(r => r.Id == x.SourceResourceChangeRequestId &&
                r.RequesterOrganizationUserId == turn.TargetAgentOrganizationUserId), token);
        if (plan is not null) await QueueReviewAsync(db, turn.OrganizationId, plan.RequestingInstallationId, id, turn.Id.ToString("N"), token);
    }

    private static async Task QueueReviewAsync(CSweetDbContext db, Guid org, Guid installation, Guid id, string revision,
        CancellationToken token)
    {
        var key = $"hiring-review:{id:N}:{revision}";
        if (await db.AgentPlatformEventOutbox.AnyAsync(x => x.OrganizationId == org && x.IdempotencyKey == key, token)) return;
        var now = DateTimeOffset.UtcNow;
        db.AgentPlatformEventOutbox.Add(new AgentPlatformEventOutboxItem { Id = Guid.NewGuid(), OrganizationId = org,
            TargetInstallationId = installation, EventType = HiringCandidateReviewWake.EventType,
            DataJson = JsonSerializer.Serialize(new HiringCandidateReviewWake(id), JsonOptions), IdempotencyKey = key,
            OccurredAt = now, NextAttemptAt = now });
    }
}
