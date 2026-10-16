using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Application.Communications;
using Microsoft.EntityFrameworkCore;
using CSweet.Infrastructure.Communications;

namespace CSweet.Infrastructure.Core;

public sealed partial class ProjectHealthService
{
    internal static async Task<Guid[]> ResolutionTurnLineageAsync(CSweet.Infrastructure.Persistence.CSweetDbContext context,
        Guid organization, Guid turnId, Guid employee, CancellationToken token)
    {
        var ids = new HashSet<Guid>(); Guid? cursor = turnId;
        while (cursor is { } id && ids.Count < 32 && ids.Add(id))
        {
            var turn = await context.ChatTurns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == organization &&
                x.TargetAgentOrganizationUserId == employee, token);
            if (turn is null) return [];
            cursor = turn.RetryOfTurnId;
        }
        return cursor is null ? ids.ToArray() : [];
    }
    private async Task RefreshOwnerReportsAsync(CancellationToken token)
    {
        var now = clock.GetUtcNow(); var retryAt = now.AddMinutes(-15);
        var candidates = await db.ProjectIncidents.Where(x => x.Status == "Open" &&
            (x.OwnerReportRequestedAt == null || x.OwnerReportRequestedAt <= retryAt) &&
            db.CoreOrganizationUsers.Any(p => p.Id == x.CurrentRecipientId && p.IsActive && p.EmployeeType == EmployeeType.Human))
            .OrderBy(x => x.OwnerReportRequestedAt).ThenBy(x => x.DetectedAt).Take(25).ToListAsync(token);
        foreach (var incident in candidates)
        {
            incident.OwnerReportRequestedAt = now;
            var reporter = incident.ReporterOrganizationUserId ?? Deserialize<ManagementIncidentHop>(incident.HistoryJson).LastOrDefault()?.FromEmployeeId ?? incident.ProducerEmployeeId;
            try { await RequirePresentationAuthorAsync(incident, reporter, token); }
            catch (UnauthorizedAccessException) { continue; }
            var employee = await db.CoreOrganizationUsers.Include(x => x.AgentInstallation)!.ThenInclude(x => x!.Grant)
                .SingleOrDefaultAsync(x => x.Id == reporter && x.IsActive && x.OrganizationId == incident.OrganizationId, token);
            if (employee?.AgentInstallation is not { IsEnabled: true, RevisionStatus: CSweet.Domain.Setup.PluginRevisionStatus.Active, Grant: not null } installation ||
                !Deserialize<string>(installation.Grant.RequiredCapabilitiesJson).Contains(ProjectHealthCapabilities.Presentation) ||
                !Deserialize<string>(installation.Grant.EventSubscriptionsJson).Contains(ProjectHealthEvents.OwnerReportRequested)) continue;
            var state = await CurrentIncidentStateAsync(incident, token);
            var report = OwnerReport(incident);
            var needsReadableReport = false;
            if (report is not null)
                try { ManagerIncidentCoordinator.ValidateReport(report); }
                catch (ArgumentException) { needsReadableReport = true; }
            if (report is { RequiresOwnerInput: not null } && report.SourceHash == state.Hash && !needsReadableReport) continue;
            if (!await HasCurrentIncidentFailureAsync(incident, token)) continue;
            var key = $"owner-report:{incident.Id:N}:{reporter:N}:{state.Hash}:owner-input-v2:{installation.PackageVersionId:N}";
            if (await db.AgentPlatformEventOutbox.AnyAsync(x => x.IdempotencyKey == key, token)) continue;
            db.AgentPlatformEventOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = incident.OrganizationId,
                TargetInstallationId = installation.Id, EventType = ProjectHealthEvents.OwnerReportRequested, IdempotencyKey = key,
                DataJson = JsonSerializer.Serialize(new ProjectHealthHint(incident.WorkstreamId, incident.Id, incident.Revision), Json),
                OccurredAt = now, NextAttemptAt = now });
        }
        await db.SaveChangesAsync(token);
    }
    internal static IncidentOwnerReport? OwnerReport(ProjectIncident incident) => incident.OwnerReportJson is null
        ? null : JsonSerializer.Deserialize<IncidentOwnerReport>(incident.OwnerReportJson, Json);

    internal async Task<IncidentCurrentState> CurrentIncidentStateAsync(ProjectIncident incident, CancellationToken token)
    {
        var project = await db.Workstreams.AsNoTracking().SingleAsync(x => x.Id == incident.WorkstreamId && x.OrganizationId == incident.OrganizationId, token);
        var facts = new List<ProjectDiagnosticEvidence>();
        var affectedId = incident.AffectedWorkItemId ?? IncidentSource(incident.Fingerprint, "ticket") ?? IncidentSource(incident.Fingerprint, "dependency-wait") ?? IncidentSource(incident.Fingerprint, "recovery-wait");
        var ticket = affectedId is { } id ? await db.CoreWorkTasks.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == id && x.OrganizationId == incident.OrganizationId, token) : null;
        if (ticket is not null && db.HealthProjectForTask(ticket) == project.Id)
            facts.Add(new("CurrentWorkItem", ticket.Id, ticket.UpdatedAt, ProjectHealthReader.Safe(
                $"{ticket.Title}: {ticket.Status}. {ticket.BlockReason ?? ticket.WaitingReason ?? ticket.ResultSummary}"),
                (await ResolveWorkItemDestinationAsync(incident, token))?.Link ?? $"/organizations/{incident.OrganizationId}/projects/{project.Id}"));
        var decisions = await db.WorkstreamDecisions.AsNoTracking().Where(x => x.OrganizationId == incident.OrganizationId &&
            x.WorkstreamId == project.Id && x.Status != "Superseded" && x.Status != "Cancelled")
            .OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.Id).Take(20).ToArrayAsync(token);
        foreach (var decision in decisions)
            facts.Add(new("CurrentProjectDecision", decision.Id, decision.UpdatedAt, ProjectHealthReader.Safe(
                $"{decision.Summary}\nType: {decision.TypeKey}; authority rule: {decision.AuthorityRuleKey}.\nStatus: {decision.Status}; selected option: {decision.SelectedOptionId}. {decision.Rationale}\nImpact: {decision.BlockingImpact}")));
        var artifacts = await db.CoreArtifacts.AsNoTracking().Where(x => x.OrganizationId == incident.OrganizationId &&
            x.WorkstreamId == project.Id && x.ArchivedAt == null && x.AcceptedRevisionId != null)
            .OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.Id).Take(3).ToArrayAsync(token);
        var revisionIds = artifacts.Select(x => x.AcceptedRevisionId!.Value).ToArray();
        var revisions = await db.ArtifactRevisions.AsNoTracking().Where(x => revisionIds.Contains(x.Id)).ToArrayAsync(token);
        var documentReferences = new List<IncidentDocumentReference>();
        foreach (var decision in decisions)
        {
            try
            {
                var references = JsonSerializer.Deserialize<CSweet.WorkManagement.Contracts.EvidenceReference[]>(decision.EvidenceJson, Json) ?? [];
                foreach (var reference in references.Where(x => x.RevisionId is not null && x.Digest is { Length: 64 }))
                    if (await db.CoreArtifacts.AnyAsync(x => x.Id == reference.ResourceId && x.OrganizationId == incident.OrganizationId && x.WorkstreamId == project.Id && x.ArchivedAt == null, token))
                        documentReferences.Add(new(reference.ResourceId, reference.RevisionId!.Value, reference.Digest!));
            }
            catch (JsonException) { /* Unreadable historical references are not reconstructed. */ }
        }
        var documents = documentReferences.Concat(artifacts.Select(x => new IncidentDocumentReference(x.Id, x.AcceptedRevisionId!.Value,
            revisions.Single(r => r.Id == x.AcceptedRevisionId).ContentSha256))).Distinct().Take(8).ToArray();
        object? operation = null;
        if (IncidentSource(incident.Fingerprint, "coordination") is { } sessionId)
        {
            var collaboration = await db.AgentCoordinationSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sessionId &&
                x.OrganizationId == incident.OrganizationId && x.WorkstreamId == project.Id, token);
            operation = collaboration is null ? null : new { collaboration.Id, collaboration.Revision, collaboration.Status, collaboration.FinalSummary };
            if (collaboration is not null)
                facts.Add(new("CurrentCollaboration", collaboration.Id, collaboration.UpdatedAt,
                    $"{collaboration.Subject}. Current status: {collaboration.Status}. {collaboration.FinalSummary}"));
        }
        else if (IncidentSource(incident.Fingerprint, "stage") is { } stageId)
            operation = await db.WorkStageExecutions.AsNoTracking().Where(x => x.Id == stageId && x.ItemExecution!.WorkItem!.OrganizationId == incident.OrganizationId)
                .Select(x => new { x.Id, x.Status, x.LastError, x.LastSummary, x.UpdatedAt }).SingleOrDefaultAsync(token);
        else if (IncidentSource(incident.Fingerprint, "work") is { } workId)
            operation = await db.AgentWorkItems.AsNoTracking().Where(x => x.Id == workId && x.OrganizationId == incident.OrganizationId.ToString())
                .Select(x => new { x.Id, x.Status, x.LastError, x.AttemptCount }).SingleOrDefaultAsync(token);
        else if (IncidentSource(incident.Fingerprint, "chat") is { } turnId)
        {
            var turn = await db.ChatTurns.AsNoTracking().Where(x => x.Id == turnId && x.OrganizationId == incident.OrganizationId)
                .Select(x => new { x.Id, x.Status, x.ErrorCode, x.ErrorMessage, x.UpdatedAt }).SingleOrDefaultAsync(token);
            var execution = await CurrentChatExecutionAsync(incident, turnId, token);
            operation = new { turn, execution };
            if (turn is not null)
                facts.Add(new("CurrentInterruptedReply", turn.Id, turn.UpdatedAt, ProjectHealthReader.Safe(
                    $"Reply status: {turn.Status}. {turn.ErrorMessage} {execution.Summary}")));
        }
        var serialized = JsonSerializer.SerializeToUtf8Bytes(new { ownerInputPolicy = 2, project.Id, project.Revision, incident.Fingerprint,
            incident.Reason, incident.EvidenceJson, ticket = ticket is null ? null : new { ticket.Id, ticket.Revision, ticket.Status, ticket.BlockReason,
                ticket.WaitingReason, ticket.ResultSummary, ticket.AssignedEmployeeId, ticket.AccountableOrganizationUserId },
            decisions = decisions.Select(x => new { x.Id, x.Revision, x.Status }), documents, operation }, Json);
        return new(Convert.ToHexString(SHA256.HashData(serialized)), project.Name, facts, documents);
    }

    private async Task RequirePresentationAuthorAsync(ProjectIncident incident, Guid actor, CancellationToken token, bool allowClosedRead = false)
    {
        var human = await db.CoreOrganizationUsers.AnyAsync(x => x.Id == incident.CurrentRecipientId && x.OrganizationId == incident.OrganizationId &&
            x.IsActive && x.EmployeeType == EmployeeType.Human, token);
        var reporter = incident.ReporterOrganizationUserId ?? Deserialize<ManagementIncidentHop>(incident.HistoryJson).LastOrDefault()?.FromEmployeeId ?? incident.ProducerEmployeeId;
        if ((!allowClosedRead && incident.Status != "Open") || !await CanReadAsync(incident.OrganizationId, actor, incident.WorkstreamId, incident, token) ||
            (incident.CurrentRecipientId != actor && (!human || actor != reporter))) throw new UnauthorizedAccessException();
    }

    private async Task<IncidentOwnerReport> ValidateOwnerReportAsync(ProjectIncident incident, IncidentOwnerReport report, CancellationToken token)
    {
        ManagerIncidentCoordinator.ValidateReport(report);
        if (report.SourceHash != (await CurrentIncidentStateAsync(incident, token)).Hash)
            throw new InvalidOperationException("The report's source evidence changed.");
        // Sanitization and bounds also apply to model-generated presentation text.
        report = report with { Title = ValidateText(report.Title), Summary = ValidateText(report.Summary),
            Recovery = ValidateText(report.Recovery), NextStep = ValidateText(report.NextStep),
            OwnerInputReason = report.OwnerInputReason is null ? null : ValidateText(report.OwnerInputReason),
            Options = report.Options.Select(x => x with { Label = ValidateText(x.Label), Description = ValidateText(x.Description),
                IntendedOutcome = ValidateText(x.IntendedOutcome) }).ToArray() };
        return report;
    }

    private static void StoreOwnerReport(ProjectIncident incident, Guid actor, IncidentOwnerReport report)
    {
        var serialized = JsonSerializer.Serialize(report, Json);
        if (incident.OwnerReportJson == serialized && incident.ReporterOrganizationUserId == actor) return;
        incident.OwnerReportJson = serialized; incident.ReporterOrganizationUserId = actor; incident.ReportRevision++;
        incident.ResolutionOutcome = null;
    }

    public async Task<ManagementIncident> UpdatePresentationAsync(Guid organization, Guid actor, UpdateManagementIncidentPresentation request, CancellationToken token)
    {
        var incident = await RequireIncidentAsync(organization, actor, request.IncidentId, token);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200) throw new ArgumentException("A bounded key is required.");
        if (request.OwnerReport is null == string.IsNullOrWhiteSpace(request.ResolutionOutcome))
            throw new ArgumentException("Supply a report or a resolution outcome.");
        var receipt = await db.ProjectIncidentReceipts.FindAsync([incident.Id, actor, request.IdempotencyKey], token);
        if (receipt is not null)
        {
            var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, Json)));
            if (receipt.RequestHash != hash) throw new InvalidOperationException("Idempotency key reused with different content.");
            return Map(incident);
        }
        await RequirePresentationAuthorAsync(incident, actor, token);
        if (incident.Revision != request.ExpectedRevision) throw new InvalidOperationException("Incident changed.");
        if (request.OwnerReport is not null)
        {
            StoreOwnerReport(incident, actor, await ValidateOwnerReportAsync(incident, request.OwnerReport, token));
            if (request.OwnerReport.RequiresOwnerInput == false) await RetainManagerRecoveryAsync(incident, actor, token);
        }
        else
        {
            var decision = await db.ExecutiveDecisions.SingleOrDefaultAsync(x => x.Id == request.ResolutionDecisionId && x.OrganizationId == organization &&
                x.Status == ExecutiveDecisionStatus.Answered, token) ?? throw new UnauthorizedAccessException();
            var choice = ExecutiveDecisionServiceIncidentBinding(decision);
            if (choice?.IncidentId != incident.Id || decision.RequestingInstallationId !=
                await db.CoreOrganizationUsers.Where(x => x.Id == actor).Select(x => x.AgentInstallationId).SingleAsync(token))
                throw new UnauthorizedAccessException();
            incident.ResolutionOutcome = ValidateText(request.ResolutionOutcome!);
            ExecutiveDecisionService.RecordIncidentOutcome(decision, incident.ResolutionOutcome);
            // Keep a bounded, incident-bound way to try another fix or supply missing facts.
            // Changed source evidence waits for a freshly generated report instead of reusing stale choices.
            if (OwnerReport(incident) is { } previous && previous.SourceHash == (await CurrentIncidentStateAsync(incident, token)).Hash)
            {
                var refreshed = previous with { Recovery = incident.ResolutionOutcome[..Math.Min(1000, incident.ResolutionOutcome.Length)] };
                incident.OwnerReportJson = JsonSerializer.Serialize(refreshed, Json);
                incident.ReportRevision++;
            }
            else incident.OwnerReportRequestedAt = null;
        }
        db.ProjectIncidentReceipts.Add(new() { IncidentId = incident.Id, ActorId = actor, IdempotencyKey = request.IdempotencyKey,
            RequestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, Json))) });
        incident.Revision++;
        await NotifyAsync(incident, token);
        await db.SaveChangesAsync(token);
        return Map(incident);
    }

    private static IncidentDecisionChoice? ExecutiveDecisionServiceIncidentBinding(ExecutiveDecision decision)
    {
        using var document = JsonDocument.Parse(decision.OptionsJson);
        return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("incident", out var binding)
            ? binding.Deserialize<IncidentDecisionChoice>(Json) : null;
    }

    public async Task<IncidentResolutionContext> ReadResolutionAsync(Guid organization, Guid actor, ReadIncidentResolution request, CancellationToken token)
    {
        var lineage = await ResolutionTurnLineageAsync(db, organization, request.TurnId, actor, token);
        var decision = await db.ExecutiveDecisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.DecisionId && x.OrganizationId == organization &&
            x.NextChatTurnId != null && lineage.Contains(x.NextChatTurnId.Value) && x.Status == ExecutiveDecisionStatus.Answered, token) ?? throw new UnauthorizedAccessException();
        var binding = ExecutiveDecisionServiceIncidentBinding(decision) ?? throw new UnauthorizedAccessException();
        var incident = await RequireIncidentAsync(organization, actor, binding.IncidentId, token);
        await RequirePresentationAuthorAsync(incident, actor, token, allowClosedRead: true);
        var employee = await db.CoreOrganizationUsers.SingleAsync(x => x.Id == actor && x.OrganizationId == organization, token);
        if (employee.AgentInstallationId != decision.RequestingInstallationId || incident.CurrentRecipientId != decision.AnsweredByOrganizationUserId)
            throw new UnauthorizedAccessException();
        var report = OwnerReport(incident) ?? throw new InvalidOperationException("Report unavailable.");
        var handled = await db.ProjectIncidentReceipts.AnyAsync(x => x.IncidentId == incident.Id && x.ActorId == actor &&
            x.IdempotencyKey == $"resolution-result:{decision.Id:N}", token);
        var terminal = incident.Status is "Resolved" or "Superseded";
        handled |= terminal;
        if (!handled && incident.ReportRevision != binding.ReportRevision) throw new InvalidOperationException("Report changed.");
        if (!handled && report.SourceHash != (await CurrentIncidentStateAsync(incident, token)).Hash)
            throw new InvalidOperationException("The selected fix needs review against changed project evidence.");
        var outcome = ExecutiveDecisionService.IncidentIntendedOutcome(decision);
        var turn = await db.ChatTurns.AsNoTracking().SingleAsync(x => x.Id == decision.NextChatTurnId && x.TargetAgentOrganizationUserId == actor, token);
        return new(Map(incident), decision.Id, binding.ReportRevision, outcome, decision.SelectedOptionId, decision.FreeTextAnswer, handled)
            { TurnId = turn.Id, ConversationId = decision.ConversationId, SourceMessageId = turn.UserMessageId,
                RecordedOutcome = ExecutiveDecisionService.RecordedIncidentOutcome(decision) ?? (terminal
                    ? incident.Status == "Resolved" ? "This blocker has cleared according to the current work records. No further recovery action is needed."
                        : "This incident was superseded. Review the current manager report; actions from this old report will not be replayed."
                    : null) };
    }
}
