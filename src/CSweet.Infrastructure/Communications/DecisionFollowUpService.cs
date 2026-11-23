using CSweet.Application.Communications;
using CSweet.Application.Core;
using CSweet.Contracts.Communications;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Communications;

public sealed class DecisionFollowUpService(CSweetDbContext db, IChatTurnService turns, IExecutiveDecisionService decisions)
{
    internal const string Provider = CSweet.Domain.Communications.CommunicationProviderKeys.DecisionFollowUp;

    private IQueryable<OrganizationUser> Speaker(Guid org, Guid chat, Guid decision, Guid human) =>
        from d in db.ExecutiveDecisions.AsNoTracking()
        join employee in db.CoreOrganizationUsers on d.RequestingInstallationId equals employee.AgentInstallationId
        where d.Id == decision && d.OrganizationId == org && d.ConversationId == chat &&
            employee.OrganizationId == org && employee.IsActive && employee.EmployeeType == EmployeeType.Agent &&
            db.CoreConversations.Any(c => c.Id == chat && c.OrganizationId == org && c.ArchivedAt == null) &&
            db.ConversationParticipants.Any(p => p.ConversationId == chat && p.OrganizationUserId == employee.Id && p.LeftAt == null) &&
            db.ConversationParticipants.Any(p => p.ConversationId == chat && p.OrganizationUserId == human && p.LeftAt == null &&
                p.OrganizationUser!.OrganizationId == org && p.OrganizationUser.IsActive && p.OrganizationUser.EmployeeType == EmployeeType.Human)
        select employee;

    public async Task<Guid?> AskAsync(Guid org, Guid chat, Guid decision, Guid human, DecisionQuestionRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > 4000 || request.RequestId is not Guid requestId || requestId == Guid.Empty)
            throw new ArgumentException("A question and request identity are required.");
        await using var tx = db.Database.CurrentTransaction is null && db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
        await HiringOrganizationLock.AcquireAsync(db, org, token);
        var speaker = await Speaker(org, chat, decision, human).SingleOrDefaultAsync(token);
        if (speaker is null) return null;
        var channel = $"{decision:N}/{request.OptionId}";
        var key = $"inquiry:{decision:N}:{human:N}:{requestId:N}";
        var prior = await db.CoreConversationMessages.AsNoTracking().SingleOrDefaultAsync(m => m.ConversationId == chat &&
            m.SourceProvider == Provider && m.IdempotencyKey == key, token);
        if (prior is not null)
        {
            if (prior.Content != request.Question.Trim() || prior.SourceChannelExternalId != channel)
                throw new InvalidOperationException("This follow-up identity already belongs to another question.");
            return prior.ChatTurnId;
        }
        if (!await IsPendingAsync(org, chat, decision, request.OptionId, token)) return null;
        var started = await turns.StartForAgentAsync(org, chat, speaker.Id, request.Question, human, Provider, channel, key,
            cancellationToken: token);
        if (started is null) throw new InvalidOperationException("The question could not be queued.");
        if (tx is not null) await tx.CommitAsync(token);
        return started.Turn.Id;
    }

    internal async Task<Guid?> PendingSpeakerAsync(Guid org, Guid chat, Guid decision, Guid human, string? option, CancellationToken token)
    {
        var speaker = await Speaker(org, chat, decision, human).Select(p => (Guid?)p.Id).SingleOrDefaultAsync(token);
        return speaker.HasValue && await IsPendingAsync(org, chat, decision, option, token) ? speaker : null;
    }

    private async Task<bool> IsPendingAsync(Guid org, Guid chat, Guid decision, string? option, CancellationToken token) =>
        (await decisions.ListForMessagesAsync(org, chat, token)).Values.Any(c => c.Id == decision && c.Status == "Pending" &&
            (option is null || c.Options.Any(o => o.Id == option)));

    public async Task<IReadOnlyList<DecisionFollowUpExchange>?> HistoryAsync(Guid org, Guid chat, Guid decision, Guid human, CancellationToken token)
    {
        if (!await Speaker(org, chat, decision, human).AnyAsync(token)) return null;
        var source = await db.ExecutiveDecisions.AsNoTracking().SingleAsync(d => d.Id == decision, token);
        var options = ExecutiveDecisionService.IncidentCard(source).Options;
        var prefix = $"{decision:N}/";
        var questions = await db.ChatTurns.AsNoTracking().Where(t => t.OrganizationId == org && t.ConversationId == chat &&
                t.UserMessage!.SourceProvider == Provider && t.UserMessage.SourceChannelExternalId!.StartsWith(prefix))
            .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).Take(100)
            .Select(t => new { t.Id, t.Status, t.UserMessage!.Content, Channel = t.UserMessage.SourceChannelExternalId!,
                Answer = t.AssistantMessage!.Content, t.UserMessage.CoordinationSessionId })
            .ToArrayAsync(token);
        var ids = questions.Where(q => q.CoordinationSessionId.HasValue).Select(q => q.CoordinationSessionId!.Value).ToArray();
        var replies = await db.CoreConversationMessages.AsNoTracking().Where(m => m.ConversationId == chat &&
                m.CoordinationSessionId.HasValue && ids.Contains(m.CoordinationSessionId.Value) &&
                m.Role == ConversationRole.Assistant && m.DeliveryIntent == CommunicationDeliveryIntent.Response)
            .OrderByDescending(m => m.Sequence).Select(m => new { m.CoordinationSessionId, m.Content }).ToArrayAsync(token);
        return questions.Select(q => new DecisionFollowUpExchange(q.Id, q.Content,
            options.FirstOrDefault(o => o.Id == q.Channel[prefix.Length..])?.Label, q.Status.ToString(), q.Answer,
            replies.FirstOrDefault(r => r.CoordinationSessionId == q.CoordinationSessionId)?.Content)).ToArray();
    }
}
