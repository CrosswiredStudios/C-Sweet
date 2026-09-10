using System.Text.Json;
using CSweet.Application.Communications;
using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.Communications;

public sealed partial class ExecutiveDecisionService
{
    private async Task<WorkstreamDecisionRecord?> ValidateWorkstreamDecisionAsync(
        CreateExecutiveDecisionCommand command, CancellationToken token)
    {
        if (command.WorkstreamDecisionId is not { } id) return null;
        if (command.ConfigurationChange is not null) throw new ArgumentException("A card cannot combine two decision actions.");
        var source = await db.WorkstreamDecisions.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == id && x.OrganizationId == command.OrganizationId &&
            x.RequestedByInstallationId == command.RequestingInstallationId, token)
            ?? throw new InvalidOperationException("The project decision does not belong to the requesting agent.");
        if (source.Status != W.DecisionStatuses.Pending)
            throw new InvalidOperationException("The project decision is no longer pending.");
        var options = JsonSerializer.Deserialize<List<W.DecisionOption>>(source.OptionsJson, JsonOptions) ?? [];
        if (command.Prompt != source.Summary || command.RecommendedOptionId != source.RecommendedOptionId ||
            !options.Select(x => (x.Id, x.Label, x.Description)).SequenceEqual(command.Options.Select(x => (x.Id, x.Label, x.Description))))
            throw new ArgumentException("The card must display the authoritative project decision and options.");
        return source;
    }

    private async Task<string?> ApplyWorkstreamDecisionAsync(ExecutiveDecision card, StoredDecisionOptions stored,
        Guid actor, string? optionId, string? freeText, CancellationToken token)
    {
        if (stored.WorkstreamDecisionId is not { } id) return null;
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM "WorkstreamDecisions" WHERE "Id" = {id} FOR UPDATE""", token);
        var source = await db.WorkstreamDecisions.SingleOrDefaultAsync(x =>
            x.Id == id && x.OrganizationId == card.OrganizationId, token);
        if (source is null) return "The project decision no longer exists.";
        // Refresh after locking; another card may have resolved the same decision.
        if (db.Database.IsNpgsql()) await db.Entry(source).ReloadAsync(token);
        if (source.Status != W.DecisionStatuses.Pending || source.Revision != stored.WorkstreamDecisionRevision)
            return "The project decision changed. Refresh before responding.";
        var options = JsonSerializer.Deserialize<List<W.DecisionOption>>(source.OptionsJson, JsonOptions) ?? [];
        var selected = options.SingleOrDefault(x => x.Id == (freeText is null ? optionId : "provide-direction"));
        if (selected is null) return "Select an authoritative option for this decision.";
        if (selected.Id == "provide-direction" && string.IsNullOrWhiteSpace(freeText))
            return "Enter your binding direction using Something else.";
        var now = DateTimeOffset.UtcNow;
        source.SelectedOptionId = selected.Id;
        source.Rationale = freeText ?? selected.Description ?? selected.Label;
        source.Status = W.DecisionStatuses.Decided;
        source.DecidedByOrganizationUserId = actor;
        source.UpdatedAt = now;
        source.Revision++;
        var workstream = await db.Workstreams.AsNoTracking().SingleAsync(x =>
            x.Id == source.WorkstreamId && x.OrganizationId == source.OrganizationId, token);
        var context = new W.AgentWorkContext(source.OrganizationId, source.WorkstreamId, null, null,
            null, null, null, card.Id, null, workstream.ProfileKey);
        var data = new W.GenericResourceEvent(Guid.NewGuid(), now, context, "Decision", source.Id,
            source.Revision, source.TypeKey, "decided", JsonSerializer.SerializeToElement(new {
                source.Id, source.SelectedOptionId, source.Rationale, actorOrganizationUserId = actor, executiveDecisionId = card.Id
            }, JsonOptions));
        db.AgentPlatformEventOutbox.Add(new AgentPlatformEventOutboxItem {
            Id = Guid.NewGuid(), OrganizationId = source.OrganizationId, EventType = W.WorkstreamEventNames.DecisionDecidedV1,
            DataJson = JsonSerializer.Serialize(data, JsonOptions),
            IdempotencyKey = $"{W.WorkstreamEventNames.DecisionDecidedV1}:{source.Id:N}:{source.Revision}",
            Status = AgentPlatformEventOutboxStatus.Pending, NextAttemptAt = now, OccurredAt = now
        });
        return null;
    }
}
