using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.Core;

public sealed partial class ProjectHealthService(CSweetDbContext db, ProjectHealthReader reader, TimeProvider clock, ILogger<ProjectHealthService>? logger = null)
{
    internal static readonly TimeSpan Threshold = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // No dependency on an agent being online. Repeated passes rediscover durable deadlines.
    public async Task ReviewDueAsync(CancellationToken token)
    {
        var now = clock.GetUtcNow();
        var missing = await db.Workstreams.Where(x => x.Status != WorkstreamStatus.Completed && x.Status != WorkstreamStatus.Cancelled &&
            !db.ProjectHealthStates.Any(h => h.WorkstreamId == x.Id)).OrderBy(x => x.CreatedAt).Take(100).ToListAsync(token);
        foreach (var project in missing)
        {
            var manager = await MonitoringManagerAsync(project, token);
            // Track discovery even for unassigned projects so one old page cannot starve new projects.
            db.ProjectHealthStates.Add(new() { WorkstreamId = project.Id, OrganizationId = project.OrganizationId,
                ProducerEmployeeId = manager ?? Guid.Empty, LastProgressAt = project.CreatedAt, NextReviewAt = now });
        }
        await db.SaveChangesAsync(token);
        var signals = await db.ProjectHealthSignals.Where(x => x.ProcessedAt == null).OrderBy(x => x.OccurredAt).Take(250).ToListAsync(token);
        var wakeIds = signals.Select(x => x.WorkstreamId).Distinct().ToArray();
        var wakes = await db.ProjectHealthStates.Where(x => wakeIds.Contains(x.WorkstreamId)).ToListAsync(token);
        foreach (var state in wakes) state.NextReviewAt = now;
        foreach (var group in signals.Where(x => x.SourceKind == "ProjectIncidentDiagnosticFailure").GroupBy(x => x.SourceId))
        {
            var incident = await db.ProjectIncidents.SingleOrDefaultAsync(x => x.Id == group.Key && x.Status == "Open", token);
            if (incident is null) continue;
            var errors = group.Where(x => x.OrganizationId == incident.OrganizationId && x.WorkstreamId == incident.WorkstreamId).ToArray();
            if (errors.Length == 0) continue;
            var evidence = Deserialize<ProjectDiagnosticEvidence>(incident.EvidenceJson).Concat(errors.Select(x =>
                new ProjectDiagnosticEvidence(x.SourceKind, x.Id, x.OccurredAt, x.Detail)))
                .DistinctBy(x => new { x.Kind, x.SourceId, x.Detail }).TakeLast(100).ToArray();
            incident.EvidenceJson = JsonSerializer.Serialize(evidence, Json);
            incident.MissingEvidence = "Diagnosis or incident delivery failed: " + ProjectHealthReader.Safe(errors.Last().Detail);
            incident.Revision++;
            // Do not wake the failing diagnostic path again. Its original handoff deadline still applies.
            db.QueueAudit(new("project-health.incident.diagnostic-failed", OrganizationId: incident.OrganizationId,
                EntityType: nameof(ProjectIncident), EntityId: incident.Id, Summary: incident.MissingEvidence, OccurredAt: now));
        }
        foreach (var signal in signals) signal.ProcessedAt = now;
        await db.SaveChangesAsync(token);
        var due = await db.ProjectHealthStates.Where(x => x.NextReviewAt <= now).OrderBy(x => x.NextReviewAt).ThenBy(x => x.WorkstreamId).Take(25).ToListAsync(token);
        foreach (var state in due)
        {
            try
            {
                if (db.Entry(state).State == EntityState.Detached) db.Attach(state);
                await ReviewAsync(state, token);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Isolate a broken project and discard uncommitted notifications/receipts before continuing.
                logger?.LogError(error, "Project health review failed for {ProjectId}; baseline incidents remain pending.", state.WorkstreamId);
                db.ChangeTracker.Clear();
                var failed = await db.ProjectHealthStates.SingleAsync(x => x.WorkstreamId == state.WorkstreamId, token);
                failed.NextReviewAt = now.AddMinutes(1); failed.Revision++;
                var incidents = await db.ProjectIncidents.Where(x => x.WorkstreamId == state.WorkstreamId && x.Status == "Open").Take(100).ToListAsync(token);
                foreach (var incident in incidents)
                    incident.MissingEvidence = "Project health assessment failed; the last recorded baseline is retained. Escalation remains scheduled.";
                await db.SaveChangesAsync(token);
            }
        }
        var reviews = await db.ProjectIncidents.Where(x => x.Status == "Open" && x.ReviewAt <= now && x.EscalateAt > now)
            .OrderBy(x => x.ReviewAt).Take(50).ToListAsync(token);
        foreach (var incident in reviews)
        {
            incident.ReviewAt = null; incident.Revision++;
            await NotifyAsync(incident, token);
            await db.SaveChangesAsync(token);
        }
        var overdue = await db.ProjectIncidents.Where(x => x.Status == "Open" && x.EscalateAt <= now)
            .OrderBy(x => x.EscalateAt).Take(50).ToListAsync(token);
        foreach (var incident in overdue)
        {
            await ForwardCoreAsync(incident, "No substantive response within 15 minutes; forwarding the available report. Diagnosis or delivery may be unavailable.", token);
            await db.SaveChangesAsync(token);
        }
    }

    private async Task<Guid?> MonitoringManagerAsync(Workstream project, CancellationToken token)
    {
        var supervisors = await db.WorkstreamSupervisionAssignments.Where(x => x.WorkstreamId == project.Id && x.OrganizationId == project.OrganizationId &&
            x.StartsAt <= clock.GetUtcNow() && x.EndsAt == null).OrderByDescending(x => x.StartsAt).ThenBy(x => x.Id).ToListAsync(token);
        var boardManagers = await db.WorkBoards.Where(x => x.WorkstreamId == project.Id && x.OrganizationId == project.OrganizationId && x.ArchivedAt == null &&
            x.ManagerOrganizationUserId != null).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Select(x => x.ManagerOrganizationUserId!.Value).ToListAsync(token);
        var candidates = supervisors.Where(x => x.RoleKey == "project-health-manager").Select(x => x.SupervisorOrganizationUserId)
            .Concat(boardManagers).Concat(supervisors.Select(x => x.SupervisorOrganizationUserId))
            .Append(project.AccountableManagerOrganizationUserId ?? Guid.Empty).Distinct().ToArray();
        var people = await db.CoreOrganizationUsers.Include(x => x.AgentInstallation)!.ThenInclude(x => x!.PackageVersion)
            .Where(x => x.OrganizationId == project.OrganizationId && candidates.Contains(x.Id) && x.IsActive && x.EmployeeType == EmployeeType.Agent).ToListAsync(token);
        foreach (var id in candidates)
            if (people.Any(x => x.Id == id && HasManagerType(x.AgentInstallation?.PackageVersion?.ManifestJson))) return id;
        return null;
    }

    private static bool HasManagerType(string? manifest)
    {
        try
        {
            using var doc = JsonDocument.Parse(manifest ?? "{}");
            if (!doc.RootElement.TryGetProperty("rolePolicy", out var role) || !role.TryGetProperty("profile", out var profile)) return false;
            var family = AgentBaseTypes.FromPolicyProfile(profile.GetString());
            return family == AgentBaseTypes.Manager && (!role.TryGetProperty("baseType", out var type) || type.GetString() == family);
        }
        catch (JsonException) { return false; }
    }

    private async Task ReviewAsync(ProjectHealthState state, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        var project = await db.Workstreams.SingleAsync(x => x.Id == state.WorkstreamId, token);
        var manager = await MonitoringManagerAsync(project, token);
        var initialize = state.Revision == 1;
        state.NextReviewAt = now.AddMinutes(manager.HasValue ? 1 : 5);
        state.Revision++;
        var open = await db.ProjectIncidents.Where(x => x.WorkstreamId == project.Id && x.Status == "Open").ToListAsync(token);
        if (project.Status is WorkstreamStatus.Completed or WorkstreamStatus.Cancelled)
        {
            foreach (var incident in open) await ResolveAsync(incident, "Project completed or cancelled.", token);
            state.NextReviewAt = now.AddDays(1);
            await db.SaveChangesAsync(token); return;
        }
        if (!manager.HasValue) { await db.SaveChangesAsync(token); return; }
        state.ProducerEmployeeId = manager.Value;
        var observation = await reader.ObserveAsync(project, token, initialize);
        state.LastProgressAt = observation.LastProgress > state.LastProgressAt ? observation.LastProgress : state.LastProgressAt;
        state.HasActiveWork = observation.Active; state.WaitingReason = observation.Waiting;
        foreach (var incident in open)
        {
            var recovered = observation.Recovered.Contains(incident.Fingerprint) || incident.Fingerprint == "idle" &&
                state.LastProgressAt > incident.LastProgressAt;
            if (recovered) await ResolveAsync(incident, "Authoritative project evidence confirms recovery.", token);
        }
        var failures = observation.Failures.ToList();
        if (failures.Count == 0 && !observation.Active && observation.Waiting is null && now - state.LastProgressAt >= Threshold)
            failures.Add(new("idle", "No meaningful progress or active project work for 15 minutes", null, observation.Evidence.Take(50).ToArray()));
        foreach (var failure in failures)
        {
            var existing = open.SingleOrDefault(x => x.Status == "Open" && x.Fingerprint == failure.Key);
            var evidence = failure.Evidence.Concat(observation.Evidence.Where(x => failure.Evidence.Any(f => f.SourceId == x.SourceId)))
                .DistinctBy(x => new { x.Kind, x.SourceId, x.Detail }).OrderBy(x => x.OccurredAt).TakeLast(100).ToArray();
            if (existing is not null)
            {
                var combined = Deserialize<ProjectDiagnosticEvidence>(existing.EvidenceJson).Concat(evidence)
                    .DistinctBy(x => new { x.Kind, x.SourceId, x.Detail }).OrderBy(x => x.OccurredAt).TakeLast(100).ToArray();
                var serialized = JsonSerializer.Serialize(combined, Json);
                if (serialized != existing.EvidenceJson)
                {
                    existing.EvidenceJson = serialized; existing.ReviewAt = null; existing.Revision++;
                    await NotifyAsync(existing, token); // Does not extend the hop deadline.
                }
                continue;
            }
            var created = new ProjectIncident { Id = Guid.NewGuid(), OrganizationId = project.OrganizationId,
                WorkstreamId = project.Id, ProducerEmployeeId = manager.Value, CurrentRecipientId = manager.Value,
                AffectedWorkItemId = failure.TicketId, Fingerprint = failure.Key, Reason = failure.Reason,
                Facts = ProjectHealthReader.Safe($"{project.Name}: {failure.Reason}. Last meaningful progress: {state.LastProgressAt:O}.\n" + string.Join("\n", failure.Evidence.Select(x => x.Detail))),
                EvidenceJson = JsonSerializer.Serialize(evidence, Json), DetectedAt = now, LastProgressAt = state.LastProgressAt,
                EscalateAt = now.Add(Threshold), MissingEvidence = "Only recorded, correlated evidence is available; missing calls or runtime detail cannot be reconstructed." };
            db.ProjectIncidents.Add(created);
            await NotifyAsync(created, token);
        }
        await db.SaveChangesAsync(token);
    }

    private async Task ResolveAsync(ProjectIncident incident, string reason, CancellationToken token)
    {
        incident.Status = "Resolved"; incident.ResolvedAt = clock.GetUtcNow(); incident.EscalateAt = null; incident.ReviewAt = null; incident.Revision++;
        incident.Facts = ProjectHealthReader.Safe(incident.Facts + "\n" + reason);
        await NotifyAsync(incident, token);
        // Notify prior human recipients as well, including a CEO who has already seen the report.
        var recipients = await db.ProjectIncidentDeliveries.Where(x => x.IncidentId == incident.Id).Select(x => x.RecipientId).Distinct().ToArrayAsync(token);
        foreach (var recipient in recipients.Where(x => x != incident.CurrentRecipientId))
            QueueHumanDelivery(incident, recipient, incident.ProducerEmployeeId);
    }

    private async Task ForwardCoreAsync(ProjectIncident incident, string reason, CancellationToken token)
    {
        var people = await db.CoreOrganizationUsers.Where(x => x.OrganizationId == incident.OrganizationId && x.IsActive).ToListAsync(token);
        var history = Deserialize<ManagementIncidentHop>(incident.HistoryJson).ToList();
        var current = people.SingleOrDefault(x => x.Id == incident.CurrentRecipientId);
        var seen = history.Select(x => x.FromEmployeeId).Append(incident.CurrentRecipientId).ToHashSet();
        var project = await db.Workstreams.SingleAsync(x => x.Id == incident.WorkstreamId, token);
        var manager = await MonitoringManagerAsync(project, token) ?? incident.ProducerEmployeeId;
        var chain = new List<OrganizationUser>(); var chainSeen = new HashSet<Guid>();
        var cursor = people.SingleOrDefault(x => x.Id == manager);
        while (cursor is not null && chainSeen.Add(cursor.Id))
        {
            chain.Add(cursor);
            cursor = cursor.ReportsToOrganizationUserId is { } managerId ? people.SingleOrDefault(x => x.Id == managerId) : null;
        }
        var currentIndex = chain.FindIndex(x => x.Id == incident.CurrentRecipientId);
        var next = cursor is not null ? null : chain.Skip(currentIndex >= 0 ? currentIndex + 1 : 1).FirstOrDefault(x => !seen.Contains(x.Id));
        if (currentIndex < 0) reason += " The reporting hierarchy changed; routing along the current monitoring manager's reporting chain.";
        if (next is null)
        {
            next = people.Where(x => x.EmployeeType == EmployeeType.Human && x.PermissionLevel == OrganizationPermissionLevel.Owner)
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).FirstOrDefault();
            reason += " Reporting relationship missing or cyclic; routing to the active human owner.";
        }
        if (next is null)
        {
            incident.EscalateAt = clock.GetUtcNow().Add(Threshold);
            incident.MissingEvidence = "No active human owner or valid next manager is available. Delivery remains pending.";
            incident.Revision++; return;
        }
        history.Add(new(incident.CurrentRecipientId, next.Id, clock.GetUtcNow(), ProjectHealthReader.Safe(reason)));
        incident.HistoryJson = JsonSerializer.Serialize(history, Json);
        incident.CurrentRecipientId = next.Id; incident.Revision++;
        incident.ReviewAt = null; incident.Disposition = IncidentDispositions.Escalate;
        incident.EscalateAt = next.EmployeeType == EmployeeType.Human ? null : clock.GetUtcNow().Add(Threshold);
        await NotifyAsync(incident, token);
    }

    private async Task NotifyAsync(ProjectIncident incident, CancellationToken token)
    {
        var recipient = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.OrganizationId == incident.OrganizationId && x.Id == incident.CurrentRecipientId && x.IsActive, token);
        if (recipient?.EmployeeType == EmployeeType.Human)
            QueueHumanDelivery(incident, recipient.Id, Deserialize<ManagementIncidentHop>(incident.HistoryJson).LastOrDefault()?.FromEmployeeId ?? incident.ProducerEmployeeId);
        else if (recipient?.AgentInstallationId is { } installation)
            db.AgentPlatformEventOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = incident.OrganizationId,
                TargetInstallationId = installation, EventType = incident.CurrentRecipientId == incident.ProducerEmployeeId ? ProjectHealthEvents.ReviewDue : ProjectHealthEvents.IncidentChanged,
                IdempotencyKey = $"project-health:{incident.Id:N}:{incident.Revision}:{recipient.Id:N}",
                DataJson = JsonSerializer.Serialize(new ProjectHealthHint(incident.WorkstreamId, incident.Id, incident.Revision), Json),
                OccurredAt = clock.GetUtcNow(), NextAttemptAt = clock.GetUtcNow() });
        db.QueueAudit(new("project-health.incident.changed", OrganizationId: incident.OrganizationId,
            EntityType: nameof(ProjectIncident), EntityId: incident.Id, Summary: incident.Reason,
            Payload: JsonSerializer.SerializeToUtf8Bytes(Map(incident), Json), ContentType: "application/json",
            OccurredAt: clock.GetUtcNow(), Employees: [new(incident.CurrentRecipientId), new(incident.ProducerEmployeeId)]));
    }

    private void QueueHumanDelivery(ProjectIncident incident, Guid recipient, Guid sender) => db.ProjectIncidentDeliveries.Add(new() {
        Id = Guid.NewGuid(), IncidentId = incident.Id, RecipientId = recipient, SenderId = sender, Revision = incident.Revision,
        Markdown = Format(incident), NextAttemptAt = clock.GetUtcNow() });

    internal static string Format(ProjectIncident x) => $"### Project incident — {x.Status}\n\n{x.Reason}\n\n" +
        $"**Observed facts**\n{x.Facts}\n\n**Likely cause**\n{x.LikelyCause}\n\n**Missing evidence**\n{x.MissingEvidence}\n\n" +
        $"**Requested action**\n{x.RecommendedAction}\n\n**Recorded recovery action**\n{x.ActionReference ?? "None recorded."}\n\n**Escalation history**\n" +
        string.Join("\n", Deserialize<ManagementIncidentHop>(x.HistoryJson).Select(h => $"- {h.OccurredAt:O}: {h.FromEmployeeId} → {h.ToEmployeeId}: {h.Reason}")) +
        "\n\n**Evidence**\n" + string.Join("\n", Deserialize<ProjectDiagnosticEvidence>(x.EvidenceJson).Take(10).Select(e =>
            $"- {e.Kind} `{e.SourceId}`: {e.Detail}" + (e.Link is null ? "" : $" [Open source]({e.Link})"))) + $"\n\nIncident `{x.Id}` · Project `{x.WorkstreamId}`. Acknowledgement does not resolve this incident.";

    private static IReadOnlyList<T> Deserialize<T>(string json) => JsonSerializer.Deserialize<T[]>(json, Json) ?? [];
    private static ManagementIncident Map(ProjectIncident x) => new(x.Id, x.WorkstreamId, x.ProducerEmployeeId, x.CurrentRecipientId,
        x.Status, x.Reason, x.AffectedWorkItemId, x.DetectedAt, x.LastProgressAt, x.EscalateAt, x.Revision, x.Facts,
        x.LikelyCause, x.MissingEvidence, x.RecommendedAction, Deserialize<ManagementIncidentHop>(x.HistoryJson))
        { Disposition = x.Disposition, ReviewAt = x.ReviewAt, ActionReference = x.ActionReference };
}
