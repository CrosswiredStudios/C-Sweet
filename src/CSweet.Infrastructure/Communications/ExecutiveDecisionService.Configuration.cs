using System.Text.Json;
using CSweet.Application.Communications;
using CSweet.Contracts.Agents;
using CSweet.Application.Setup;
using CSweet.Infrastructure.Setup;
using CSweet.Contracts.Communications;
using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Communications;

public sealed partial class ExecutiveDecisionService
{
    public const string ConfigurationChoiceAnsweredEvent = "com.csweet.agent.configuration-choice.answered.v1";

    private async Task<AgentConfigurationView> ReadConfigurationChoiceAsync(
        Guid organizationId, Guid installationId, AgentConfigurationChoice change, CancellationToken token)
    {
        var employee = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.AgentInstallationId == installationId && x.IsActive, token)
            ?? throw new InvalidOperationException("The requesting employee is unavailable.");
        var view = await (configurations ?? throw new InvalidOperationException("Configuration service is unavailable."))
            .GetEmployeeAsync(organizationId, employee.Id, token);
        var field = view.Fields.SingleOrDefault(x => x.Key == change.Key);
        if (field is null || field.Type != "select" ||
            field.Options?.Any(x => x.Value == change.CurrentValue) != true ||
            field.Options.Any(x => x.Value == change.ProposedValue) != true ||
            change.CurrentValue == change.ProposedValue)
            throw new ArgumentException("A configuration choice must identify two distinct presets of a select field.");
        if (!view.EffectiveValues.TryGetValue(change.Key, out var current) ||
            current.ValueKind != JsonValueKind.String || current.GetString() != change.CurrentValue)
            throw new InvalidOperationException("The employee configuration changed. Request a fresh decision.");
        return view;
    }

    private async Task<AnswerExecutiveDecisionResponse> AnswerConfigurationChoiceAsync(
        ExecutiveDecision decision, AgentConfigurationChoice change, StoredOption selected,
        Guid actorId, string answerKey, CancellationToken token)
    {
        var isOwner = await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x =>
            x.Id == actorId && x.OrganizationId == decision.OrganizationId && x.IsActive &&
            x.EmployeeType == EmployeeType.Human && x.PermissionLevel == OrganizationPermissionLevel.Owner, token);
        if (!isOwner) return Failure("not_authorized", "Only a business owner can approve an employee configuration decision.");
        if (selected.Id is not ("apply" or "leave-unchanged"))
            return Failure("validation_error", "The selected configuration action is invalid.");
        var employee = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == decision.OrganizationId && x.AgentInstallationId == decision.RequestingInstallationId && x.IsActive, token);
        if (employee is null) return Failure("agent_unavailable", "The requesting employee is unavailable.");
        if (router is null || configurations is null)
            throw new InvalidOperationException("Configuration decision delivery is unavailable.");

        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
        if (db.Database.IsNpgsql())
        {
            // Serialize immutable answers across requests before saving settings or enqueuing continuation.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"ExecutiveDecisions\" WHERE \"Id\" = {decision.Id} FOR UPDATE", token);
            await db.Entry(decision).ReloadAsync(token);
            if (decision.Status == ExecutiveDecisionStatus.Answered)
                return decision.AnswerIdempotencyKey == answerKey
                    ? new(true, null, "The answer was already submitted.", ToCard(decision))
                    : Failure("decision_already_answered", "This decision already has an immutable answer.", ToCard(decision));
            if (decision.Status != ExecutiveDecisionStatus.Pending)
                return Failure("decision_not_pending", "This decision is no longer pending.", ToCard(decision));
        }
        AgentConfigurationView view;
        try
        {
            view = selected.Id == "apply"
                ? await ReadConfigurationChoiceAsync(decision.OrganizationId, decision.RequestingInstallationId, change, token)
                : await configurations.GetEmployeeAsync(decision.OrganizationId, employee.Id, token);
            if (selected.Id == "apply")
            {
                var overrides = view.Overrides.ToDictionary(x => x.Key, x => x.Value.Clone(), StringComparer.Ordinal);
                overrides[change.Key] = JsonSerializer.SerializeToElement(change.ProposedValue);
                view = await configurations.SaveEmployeeOverridesAsync(decision.OrganizationId, employee.Id,
                    new PutAgentConfigurationOverridesRequest(overrides, view.ExpectedRevision), token);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or AgentConfigurationConflictException or AgentInstallationException)
        {
            return Failure("configuration_conflict", exception.Message, ToCard(decision));
        }
        var effectiveValue = view.EffectiveValues[change.Key].GetString();
        var now = DateTimeOffset.UtcNow;
        db.CoreConversationMessages.Add(new ConversationMessage
        {
            Id = Guid.NewGuid(), ConversationId = decision.ConversationId, SenderOrganizationUserId = actorId,
            Role = ConversationRole.User, Content = $"Decision: {decision.Prompt}\nAnswer: {selected.Label}",
            CreatedAt = now, CorrelationId = decision.Id
        });
        decision.SelectedOptionId = selected.Id;
        decision.AnsweredByOrganizationUserId = actorId;
        decision.AnswerIdempotencyKey = answerKey;
        decision.AnsweredAt = now;
        decision.UpdatedAt = now;
        decision.Status = ExecutiveDecisionStatus.Answered;
        await db.SaveChangesAsync(token);
        var delivered = await router.EnqueueEventAsync(decision.OrganizationId.ToString("D"), ConfigurationChoiceAnsweredEvent,
            JsonSerializer.SerializeToElement(new
            {
                decisionId = decision.Id, organizationId = decision.OrganizationId,
                conversationId = decision.ConversationId, key = change.Key, value = effectiveValue,
                selectedOptionId = selected.Id, actorOrganizationUserId = actorId
            }, JsonOptions), decision.Id, $"configuration-choice:{decision.Id:N}", decision.RequestingInstallationId,
            requireSubscription: true, cancellationToken: token);
        if (delivered != 1) throw new InvalidOperationException("The requesting agent cannot receive the configuration decision.");
        if (transaction is not null) await transaction.CommitAsync(token);
        if (audit is not null)
            await audit.WriteAsync("communication.configuration-choice.answered", nameof(ExecutiveDecision), decision.Id,
                $"Owner selected {selected.Id} for employee configuration field {change.Key}.", cancellationToken: token);
        return new(true, null, "Decision submitted.", ToCard(decision));
    }
}
