using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class ProjectHealthService
{
    public async Task<Guid> RequireActorAsync(Guid organization, Guid installation, string capability, CancellationToken token)
    {
        var employee = await db.CoreOrganizationUsers.Include(x => x.AgentInstallation)!.ThenInclude(x => x!.Grant)
            .SingleOrDefaultAsync(x => x.OrganizationId == organization && x.AgentInstallationId == installation && x.IsActive && x.EmployeeType == EmployeeType.Agent, token);
        if (employee?.AgentInstallation is not { IsEnabled: true, RevisionStatus: PluginRevisionStatus.Active, Grant: not null } agent ||
            !Deserialize<string>(agent.Grant.RequiredCapabilitiesJson).Contains(capability, StringComparer.Ordinal)) throw new UnauthorizedAccessException();
        return employee.Id;
    }

    private async Task<bool> CanReadAsync(Guid organization, Guid actor, Guid projectId, ProjectIncident? incident, CancellationToken token)
    {
        var project = await db.Workstreams.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organization && x.Id == projectId, token);
        if (project is null) return false;
        var manager = await MonitoringManagerAsync(project, token);
        var people = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == organization && x.IsActive).ToListAsync(token);
        var user = people.SingleOrDefault(x => x.Id == actor);
        if (user is null) return false;
        if (user.EmployeeType == EmployeeType.Human && user.PermissionLevel == OrganizationPermissionLevel.Owner) return true;
        if (actor == manager) return true;
        // A previously assigned reporter or an old recipient is not a perpetual access grant.
        var seen = new HashSet<Guid>();
        var managers = new HashSet<Guid>();
        var next = manager;
        while (next.HasValue)
        {
            if (!seen.Add(next.Value)) return false;
            var person = people.SingleOrDefault(x => x.Id == next);
            next = person?.ReportsToOrganizationUserId;
            if (next.HasValue) managers.Add(next.Value);
        }
        return incident is not null && managers.Contains(actor);
    }

    private async Task<ProjectIncident> RequireIncidentAsync(Guid organization, Guid actor, Guid id, CancellationToken token)
    {
        var incident = await db.ProjectIncidents.SingleOrDefaultAsync(x => x.OrganizationId == organization && x.Id == id, token);
        if (incident is null || !await CanReadAsync(organization, actor, incident.WorkstreamId, incident, token)) throw new UnauthorizedAccessException();
        return incident;
    }

    public async Task<ProjectHealthSnapshot> ReadAsync(Guid organization, Guid actor, ReadProjectHealth request, CancellationToken token)
    {
        var incident = await db.ProjectIncidents.AsNoTracking().FirstOrDefaultAsync(x => x.OrganizationId == organization && x.WorkstreamId == request.WorkstreamId, token);
        if (!await CanReadAsync(organization, actor, request.WorkstreamId, incident, token)) throw new UnauthorizedAccessException();
        var state = await db.ProjectHealthStates.SingleAsync(x => x.OrganizationId == organization && x.WorkstreamId == request.WorkstreamId, token);
        var ids = await db.ProjectIncidents.Where(x => x.OrganizationId == organization && x.WorkstreamId == request.WorkstreamId && x.Status == "Open").Select(x => x.Id).Take(200).ToArrayAsync(token);
        return new(state.WorkstreamId, state.LastProgressAt, state.NextReviewAt, state.HasActiveWork, state.WaitingReason, ids);
    }

    public async Task<ManagementIncidentPage> ReadIncidentsAsync(Guid organization, Guid actor, ReadManagementIncidents request, CancellationToken token)
    {
        if (request.IncidentId.HasValue) return new([Map(await RequireIncidentAsync(organization, actor, request.IncidentId.Value, token))], null);
        var limit = Math.Clamp(request.Limit, 1, 100); var offset = Math.Max(0, request.Offset);
        var batch = await db.ProjectIncidents.Where(x => x.OrganizationId == organization && x.Status == "Open" &&
            (request.WorkstreamId.HasValue || x.ReviewAt == null) &&
            (x.CurrentRecipientId == actor || request.WorkstreamId.HasValue && x.ProducerEmployeeId == actor) &&
            (!request.WorkstreamId.HasValue || x.WorkstreamId == request.WorkstreamId))
            .OrderBy(x => x.DetectedAt).ThenBy(x => x.Id).Skip(offset).Take(limit + 1).ToListAsync(token);
        var allowed = new List<ManagementIncident>();
        foreach (var incident in batch.Take(limit))
            if (await CanReadAsync(organization, actor, incident.WorkstreamId, incident, token)) allowed.Add(Map(incident));
        return new(allowed, batch.Count > limit ? offset + limit : null);
    }

    public async Task<ManagementIncident> ReportAsync(Guid organization, Guid actor, ReportManagementIncident request, CancellationToken token)
    {
        var incident = await RequireIncidentAsync(organization, actor, request.IncidentId, token);
        var facts = ValidateText(request.Facts); var cause = ValidateText(request.LikelyCause);
        var missing = ValidateText(request.MissingEvidence); var action = ValidateText(request.RecommendedAction);
        if (request.Disposition is not (IncidentDispositions.Escalate or IncidentDispositions.Investigating or IncidentDispositions.AwaitingRecovery))
            throw new ArgumentException("Unknown incident disposition.");
        var reference = request.ActionReference is null ? null : ValidateText(request.ActionReference);
        if (await IsReplayAsync(incident, actor, request.IdempotencyKey, request, request.ExpectedRevision, token, () =>
        {
            if (request.Disposition != IncidentDispositions.Escalate &&
                (request.ReviewAt is null || request.ReviewAt <= clock.GetUtcNow() || incident.EscalateAt is null || request.ReviewAt >= incident.EscalateAt))
                throw new ArgumentException("Follow-up must occur before the existing escalation deadline.");
            if (request.Disposition == IncidentDispositions.AwaitingRecovery && reference is null)
                throw new ArgumentException("Awaiting recovery requires a recorded action reference.");
        })) return Map(incident);
        incident.Facts = facts; incident.LikelyCause = cause;
        incident.MissingEvidence = missing; incident.RecommendedAction = action;
        incident.Disposition = request.Disposition; incident.ActionReference = reference;
        if (request.Disposition == IncidentDispositions.Escalate)
            await ForwardCoreAsync(incident, action, token);
        else
        {
            incident.ReviewAt = request.ReviewAt; incident.Revision++;
            db.QueueAudit(new("project-health.incident.assessed", OrganizationId: organization, EntityType: nameof(ProjectIncident), EntityId: incident.Id,
                Actor: new("Agent", OrganizationUserId: actor), Summary: action,
                Payload: JsonSerializer.SerializeToUtf8Bytes(Map(incident), Json), ContentType: "application/json", OccurredAt: clock.GetUtcNow()));
        }
        await db.SaveChangesAsync(token);
        return Map(incident);
    }

    public async Task<ManagementIncident> ForwardAsync(Guid organization, Guid actor, ForwardManagementIncident request, CancellationToken token)
    {
        var incident = await RequireIncidentAsync(organization, actor, request.IncidentId, token);
        var reason = ValidateText(request.Reason);
        if (await IsReplayAsync(incident, actor, request.IdempotencyKey, request, request.ExpectedRevision, token)) return Map(incident);
        await ForwardCoreAsync(incident, reason, token);
        await db.SaveChangesAsync(token);
        return Map(incident);
    }

    private async Task<bool> IsReplayAsync<T>(ProjectIncident incident, Guid actor, string key, T request, long revision, CancellationToken token, Action? validate = null)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200) throw new ArgumentException("A bounded idempotency key is required.");
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, Json)));
        var receipt = await db.ProjectIncidentReceipts.FindAsync([incident.Id, actor, key], token);
        if (receipt is not null)
        {
            if (receipt.RequestHash != hash) throw new InvalidOperationException("Idempotency key reused with different content.");
            return true;
        }
        RequireCurrent(incident, actor, revision);
        validate?.Invoke();
        db.ProjectIncidentReceipts.Add(new() { IncidentId = incident.Id, ActorId = actor, IdempotencyKey = key, RequestHash = hash });
        return false;
    }

    private static void RequireCurrent(ProjectIncident incident, Guid actor, long revision)
    {
        if (incident.Status != "Open" || incident.CurrentRecipientId != actor || incident.Revision != revision)
            throw new InvalidOperationException("Incident changed; read its current revision and recipient.");
    }

    private static string ValidateText(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192) throw new ArgumentException("Report text must contain 1–8192 characters.");
        return AuditPayloadSanitizer.RedactText(value);
    }

    public async Task<ProjectDiagnosticsPage> ReadDiagnosticsAsync(Guid organization, Guid actor, ReadProjectDiagnostics request, CancellationToken token)
    {
        var incident = await RequireIncidentAsync(organization, actor, request.IncidentId, token);
        var evidence = Deserialize<ProjectDiagnosticEvidence>(incident.EvidenceJson).ToList();
        var limitations = new List<string> { "Only exact project/work correlations are included; unrecorded or uncorrelated evidence is unavailable." };
        var sources = evidence.Select(x => x.SourceId).ToArray();
        var attemptIds = await db.AgentWorkAttempts.Where(x => sources.Contains(x.Id)).Select(x => x.Id).ToArrayAsync(token);
        var runIds = await db.AgentRunLogs.Where(x => x.OrganizationId == organization && x.AgentWorkAttemptId.HasValue && attemptIds.Contains(x.AgentWorkAttemptId.Value))
            .Select(x => x.Id).ToArrayAsync(token);
        var records = await db.AuditEvents.AsNoTracking().Where(x => x.OrganizationId == organization && x.EntityId != null &&
            ((x.EntityType == "AgentRunLog" && runIds.Contains(x.EntityId.Value)) ||
             (x.EntityType == "Capability" && attemptIds.Contains(x.EntityId.Value)) ||
             (x.EntityType == "AgentWorkAttempt" && attemptIds.Contains(x.EntityId.Value))))
            .OrderByDescending(x => x.Sequence).Take(200).ToListAsync(token);
        var parentIds = records.Select(x => x.Id).ToArray();
        records.AddRange(await db.AuditEvents.AsNoTracking().Where(x => x.OrganizationId == organization && x.ParentEventId.HasValue && parentIds.Contains(x.ParentEventId.Value))
            .OrderByDescending(x => x.Sequence).Take(200).ToListAsync(token));
        foreach (var record in records.DistinctBy(x => x.Id))
        {
            var payload = await db.AuditEventPayloads.AsNoTracking().SingleOrDefaultAsync(x => x.AuditEventId == record.Id, token);
            var detail = record.PayloadPreview;
            if (payload is not null && db.AuditProtection is not null)
            {
                try
                {
                    var bytes = db.AuditProtection.CreateProtector("CSweet.AuditPayload.v1").Unprotect(payload.ProtectedContent);
                    if (Convert.ToHexString(SHA256.HashData(bytes)) != record.EvidenceSha256 ||
                        record.IntegrityVersion > 0 && (AuditIntegrity.ComputeRecordHash(record) != record.RecordHash ||
                        db.AuditProtection.CreateProtector("CSweet.SecurityAuditLedger.v1").Unprotect(record.IntegritySeal!) != record.RecordHash))
                    { limitations.Add("An evidence integrity check failed."); continue; }
                    detail = Encoding.UTF8.GetString(bytes);
                }
                catch (CryptographicException) { limitations.Add("Protected evidence could not be opened."); continue; }
            }
            else limitations.Add("Some records contain preview-only evidence.");
            if (detail is not null) evidence.Add(new(record.EventType, record.Id, record.OccurredAt,
                ProjectHealthReader.Safe(detail), record.ActorOrganizationUserId.HasValue ? $"/organizations/{organization}/employees/{record.ActorOrganizationUserId}" : null));
        }
        var offset = Math.Max(0, request.Offset); var limit = Math.Clamp(request.Limit, 1, 100);
        var ordered = evidence.DistinctBy(x => new { x.Kind, x.SourceId, x.Detail }).OrderBy(x => x.OccurredAt).ThenBy(x => x.SourceId).ToArray();
        if (records.Count >= 200) limitations.Add("Diagnostic query reached its bounded history window.");
        db.QueueAudit(new("project-health.diagnostics.read", OrganizationId: organization, EntityType: nameof(ProjectIncident), EntityId: incident.Id,
            Actor: new("Agent", OrganizationUserId: actor), Summary: "Authorized project diagnostic evidence inspected."));
        await db.SaveChangesAsync(token);
        return new(ordered.Skip(offset).Take(limit).ToArray(), offset + limit < ordered.Length ? offset + limit : null, limitations.Distinct().ToArray());
    }
}
