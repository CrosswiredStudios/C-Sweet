using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace CSweet.Infrastructure.Persistence;

public sealed partial class CSweetDbContext
{
    private readonly Dictionary<string, Guid> pendingAuditMutations = new();

    private Guid AuditMutationId(string key)
    {
        // Reuse a receipt while a failed save is retried, but retain every later
        // transition even if it returns to a previously recorded value.
        if (pendingAuditMutations.TryGetValue(key, out var pending) &&
            ChangeTracker.Entries<CSweet.Domain.Compute.ComputeAuditOutbox>().Any(x => x.Entity.Id == pending && x.State == EntityState.Added))
            return pending;
        return pendingAuditMutations[key] = Guid.NewGuid();
    }

    public static Guid AuditSourceId(string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));

    public void QueueAudit(AuditEventWriteRequest request)
    {
        var id = request.EventId ?? Guid.NewGuid();
        if (AuditOutbox.Local.Any(x => x.Id == id)) return;
        if (request.Payload is { } body && AuditPayloadSanitizer.Capture(body, request.ContentType).FullContent is string safe)
            request = request with { Payload = Encoding.UTF8.GetBytes(safe) };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request with { EventId = id, UseAmbientOrganization = false });
        AuditOutbox.Add(new()
        {
            Id = id, CreatedAt = DateTimeOffset.UtcNow, SourceEntityType = request.EntityType, SourceEntityId = request.EntityId,
            RequestJson = AuditProtection is null ? Encoding.UTF8.GetString(bytes) : "{}",
            ProtectedRequest = AuditProtection?.CreateProtector("CSweet.AuditOutbox.v1").Protect(bytes)
        });
    }

    private void CaptureAgentAuditEvents()
    {
        ChangeTracker.DetectChanges();
        foreach (var entry in ChangeTracker.Entries().Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray())
        {
            var request = DescribeAuditSource(entry);
            if (request is not null) QueueAudit(request);
        }
    }

    // Also used by the historical importer. Historical snapshots never invent missing transitions.
    internal AuditEventWriteRequest? DescribeAuditSource(EntityEntry entry, bool historical = false)
    {
        Guid? organization = null, installation = null, employee = null;
        Guid id;
        string category, eventType, outcome = "Completed";
        string? correlation = null;
        var when = DateTimeOffset.UtcNow;
        var associations = new List<AuditEmployeeAssociation>();
        AuditActor? actor = null;
        object? extra = null;
        bool Changed(params string[] names) => entry.State == EntityState.Added || historical ||
            names.Any(name => entry.Property(name).IsModified && !Equals(entry.OriginalValues[name], entry.CurrentValues[name]));
        T? Find<T>(Guid key) where T : class => Set<T>().Find(key);
        void Associate(Guid? employeeId, string role = "Affected")
        { if (employeeId.HasValue) associations.Add(new(employeeId.Value, role)); }
        void ResolveWork(AgentWorkItem? work)
        {
            if (work is null) return;
            organization = Guid.TryParse(work.OrganizationId, out var org) ? org : null;
            installation = work.AgentInstallationId; correlation = work.CorrelationId;
        }
        switch (entry.Entity)
        {
            case ConversationMessage message:
                if (!Changed(nameof(message.Content)) && entry.State != EntityState.Deleted) return null;
                var chat = Find<Conversation>(message.ConversationId);
                if (chat is null) return null;
                id = message.Id; organization = chat.OrganizationId; category = "Communication";
                eventType = entry.State == EntityState.Deleted ? "communication.message.deleted" :
                    entry.State == EntityState.Modified && !historical ? "communication.message.edited" : "communication.message.sent";
                when = entry.State == EntityState.Added || historical ? message.CreatedAt : when;
                correlation = message.ChatTurnId?.ToString("D") ?? message.CorrelationId.ToString("D");
                Associate(message.SenderOrganizationUserId, "Actor");
                var participants = ConversationParticipants.AsNoTracking().Where(x => x.ConversationId == message.ConversationId).ToList();
                participants = ConversationParticipants.Local.Concat(participants).DistinctBy(x => x.Id)
                    .Where(x => x.ConversationId == message.ConversationId && x.JoinedAt <= when && (x.LeftAt == null || x.LeftAt > when)).ToList();
                foreach (var participant in participants.Where(x => x.OrganizationUserId != message.SenderOrganizationUserId)) Associate(participant.OrganizationUserId, "Recipient");
                if (message.SenderOrganizationUserId is Guid sender && Find<OrganizationUser>(sender) is { } user)
                    actor = new(user.EmployeeType == EmployeeType.Agent ? "Agent" : "Human", true, user.ApplicationUserId, user.Id, user.DisplayName, InstallationId: user.AgentInstallationId);
                break;
            case ConversationParticipant participant:
                if (!Changed(nameof(participant.LeftAt), nameof(participant.Role))) return null;
                var conversation = Find<Conversation>(participant.ConversationId);
                if (conversation is null) return null;
                id = participant.Id; organization = conversation.OrganizationId; category = "Communication";
                eventType = entry.State == EntityState.Deleted || participant.LeftAt.HasValue ? "communication.participant.left" :
                    entry.State == EntityState.Modified && !historical ? "communication.participant.updated" : "communication.participant.joined";
                when = participant.LeftAt ?? participant.JoinedAt; Associate(participant.OrganizationUserId);
                break;
            case ChatTurn turn:
                if (!Changed(nameof(turn.Status))) return null;
                id = turn.Id; organization = turn.OrganizationId; employee = turn.TargetAgentOrganizationUserId;
                category = "ChatTurn"; outcome = turn.Status.ToString(); eventType = "chat.turn." + outcome.ToLowerInvariant();
                when = turn.UpdatedAt; correlation = turn.Id.ToString("D");
                break;
            case ChatTurnTraceEvent trace:
                if (entry.State != EntityState.Added && !historical) return null;
                var traceTurn = Find<ChatTurn>(trace.ChatTurnId);
                if (traceTurn is null) return null;
                id = trace.Id; organization = traceTurn.OrganizationId; employee = traceTurn.TargetAgentOrganizationUserId;
                category = "ChatTrace"; eventType = trace.EventType; outcome = trace.Status;
                when = trace.OccurredAt; correlation = trace.ChatTurnId.ToString("D");
                break;
            case AgentWorkItem work:
                if (!Changed(nameof(work.Status), nameof(work.AttemptCount))) return null;
                id = work.Id; ResolveWork(work); category = "AgentWork"; outcome = work.Status.ToString();
                eventType = "agent.work." + outcome.ToLowerInvariant(); when = work.CompletedAt ?? (entry.State == EntityState.Added || historical ? work.CreatedAt : when);
                extra = new { input = DecodeWork(work.ProtectedPayload), result = DecodeWork(work.ProtectedResult) };
                break;
            case AgentWorkAttempt attempt:
                if (!Changed(nameof(attempt.FinishedAt))) return null;
                id = attempt.Id; ResolveWork(Find<AgentWorkItem>(attempt.AgentWorkItemId)); category = "AgentWork";
                eventType = attempt.FinishedAt.HasValue ? "agent.work.attempt.stopped" : "agent.work.attempt.started";
                when = attempt.FinishedAt ?? attempt.ClaimedAt; outcome = attempt.Error is null ? (attempt.FinishedAt.HasValue ? "Completed" : "Running") : "Failed";
                break;
            case CSweet.Domain.Analytics.WorkLifecycleEvent lifecycle:
                if (entry.State != EntityState.Added) return null;
                id = lifecycle.Id; organization = lifecycle.OrganizationId; category = "WorkItem";
                eventType = "work.lifecycle.recorded"; when = lifecycle.OccurredAt; outcome = lifecycle.Status;
                break;
            case CSweet.Domain.Analytics.WorkExecutionInterval interval:
                if (!Changed(nameof(interval.EndedAt))) return null;
                id = interval.Id; organization = interval.OrganizationId; category = "AgentWork";
                eventType = interval.EndedAt.HasValue ? "work.execution.paused" : "work.execution.started";
                when = interval.EndedAt ?? interval.StartedAt; outcome = interval.EndReason ?? "Running";
                break;
            case AgentWorkProgress progress:
                if (entry.State != EntityState.Added && !historical) return null;
                id = progress.Id; ResolveWork(Find<AgentWorkItem>(progress.AgentWorkItemId)); category = "AgentWork";
                eventType = "agent.work.progress"; when = progress.OccurredAt; outcome = "Running";
                extra = DecodeWork(progress.ProtectedValue);
                break;
            case WorkTask ticket:
                if (!Changed(nameof(ticket.Status), nameof(ticket.AssignedEmployeeId), nameof(ticket.AssignedAgentInstallationId))) return null;
                id = ticket.Id; organization = ticket.OrganizationId; installation = ticket.AssignedAgentInstallationId; employee = ticket.AssignedEmployeeId;
                if (!historical && entry.State == EntityState.Modified) Associate(entry.Property(nameof(ticket.AssignedEmployeeId)).OriginalValue as Guid?);
                category = "WorkItem"; outcome = ticket.Status.ToString(); eventType = "work.item." + outcome.ToLowerInvariant();
                when = ticket.UpdatedAt; correlation = ticket.CorrelationId;
                break;
            case WorkItemActivity activity:
                if (entry.State != EntityState.Added && !historical) return null;
                id = activity.Id; organization = activity.OrganizationId; category = "WorkItem"; eventType = activity.EventType;
                when = activity.OccurredAt;
                var activityTicket = Find<WorkTask>(activity.WorkItemId);
                employee = historical ? null : activityTicket?.AssignedEmployeeId; installation = historical ? null : activityTicket?.AssignedAgentInstallationId;
                if (Find<OrganizationUser>(activity.ActorSubjectId) is { } activityActor && activityActor.OrganizationId == organization) Associate(activityActor.Id, "Actor");
                break;
            case WorkOrchestrationEvent orchestration:
                if (entry.State != EntityState.Added && !historical) return null;
                id = orchestration.Id; organization = orchestration.OrganizationId; category = "WorkItem";
                eventType = orchestration.EventType; when = orchestration.OccurredAt;
                if (orchestration.StageExecutionId is Guid stageId && Find<WorkStageExecution>(stageId) is { } stage)
                { employee = stage.OrganizationUserId; installation = stage.AgentInstallationId; }
                else if (orchestration.ItemExecutionId is Guid executionId && Find<WorkItemExecution>(executionId) is { } execution &&
                    Find<WorkTask>(execution.WorkItemId) is { } workTicket && !historical)
                { employee = workTicket.AssignedEmployeeId; installation = workTicket.AssignedAgentInstallationId; }
                correlation = orchestration.ItemExecutionId?.ToString("D");
                break;
            case AgentRunLog run:
                if (!Changed(nameof(run.Status), nameof(run.CompletedAt))) return null;
                id = run.Id; organization = run.OrganizationId; employee = run.EmployeeId; installation = run.AgentInstallationId;
                category = "Model"; eventType = run.CompletedAt.HasValue ? "model.call.completed" : "model.call.started";
                outcome = run.Status; when = run.CompletedAt ?? run.StartedAt; correlation = run.ChatTurnId?.ToString("D") ?? run.Id.ToString("D");
                extra = run.RequestEvidenceJson is null ? null : JsonSerializer.Deserialize<JsonElement>(run.RequestEvidenceJson);
                break;
            case AgentRuntimeEvent runtimeEvent:
                if (entry.State != EntityState.Added && !historical) return null;
                id = runtimeEvent.Id; installation = Find<AgentRuntimeInstance>(runtimeEvent.AgentRuntimeInstanceId)?.AgentInstallationId;
                category = "AgentRuntime"; eventType = "agent.runtime." + runtimeEvent.Status.ToString().ToLowerInvariant();
                outcome = runtimeEvent.Status.ToString(); when = runtimeEvent.OccurredAt;
                break;
            case AgentPlatformEventOutboxItem platformEvent:
                if (!Changed(nameof(platformEvent.Status))) return null;
                id = platformEvent.Id; organization = platformEvent.OrganizationId; installation = platformEvent.TargetInstallationId;
                category = "PlatformEvent"; eventType = "agent.event." + platformEvent.Status.ToString().ToLowerInvariant();
                outcome = platformEvent.Status.ToString(); when = platformEvent.PublishedAt ?? platformEvent.OccurredAt;
                break;
            default: return null;
        }
        // A source has a stable organization; never attribute a different tenant's installation.
        if (installation.HasValue && !historical)
        {
            var employees = CoreOrganizationUsers.Local.Concat(CoreOrganizationUsers.AsNoTracking().Where(x => x.AgentInstallationId == installation).ToList()).DistinctBy(x => x.Id);
            foreach (var owner in employees.Where(x => x.AgentInstallationId == installation && (!organization.HasValue || x.OrganizationId == organization)))
            { organization ??= owner.OrganizationId; Associate(owner.Id); }
        }
        Associate(employee);
        if (!organization.HasValue) return null;
        var values = entry.Properties.Where(x => !x.Metadata.Name.StartsWith("Protected", StringComparison.Ordinal) &&
            !x.Metadata.Name.Contains("LeaseToken", StringComparison.Ordinal) && !x.Metadata.Name.Contains("BrokerToken", StringComparison.Ordinal))
            .ToDictionary(x => x.Metadata.Name, x => x.CurrentValue);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { source = values, evidence = extra, historical,
            historyNotice = historical ? "Imported source snapshot; missing transitions and original versions cannot be reconstructed." : null }, EventJsonOptions);
        // Sanitize before persisting even the pending outbox evidence.
        var sanitized = AuditPayloadSanitizer.Capture(payload, "application/json").FullContent!;
        var identity = entry.State == EntityState.Added || historical ? $"source:{entry.Metadata.ClrType.Name}:{id:D}" :
            $"source:{entry.Metadata.ClrType.Name}:{id:D}:{eventType}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sanitized)))}";
        if (historical && entry.Entity is ConversationMessage or ChatTurn or AgentWorkItem or WorkTask)
            eventType = "audit.source.snapshot";
        return new(eventType, category, Outcome: outcome, OrganizationId: organization, EntityType: entry.Metadata.ClrType.Name,
            EntityId: id, Summary: entry.Entity switch
            {
                WorkTask workTicket => workTicket.Title,
                AgentWorkItem agentWork => agentWork.Name,
                AgentRunLog modelRun => modelRun.Model,
                ChatTurnTraceEvent traceEntry => traceEntry.Title,
                _ => eventType
            }, OccurredAt: when, CorrelationId: correlation,
            Actor: actor ?? new("Platform", DisplayName: "C-Sweet platform", InstallationId: installation),
            ContentType: "application/json", Payload: Encoding.UTF8.GetBytes(sanitized),
            UseAmbientOrganization: false, EventId: entry.State == EntityState.Added || historical ? AuditSourceId(identity) : AuditMutationId(identity), Employees: associations.Distinct().ToArray());
    }

    private object? DecodeWork(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            if (AuditProtection is null) return new { availability = "ProtectionUnavailable" };
            return JsonSerializer.Deserialize<JsonElement>(AuditProtection.CreateProtector("CSweet.AgentWorkInbox.v1").Unprotect(bytes));
        }
        catch (CryptographicException) { return new { availability = "ProtectionUnavailable" }; }
        catch (JsonException) { return new { availability = "UnsupportedPayload" }; }
    }
}
