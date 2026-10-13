using System.Text.RegularExpressions;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class ProjectHealthService
{
    internal sealed record IncidentPresentation(string Title, string Summary, string Recovery, string NextStep, string Link, string LinkLabel)
    {
        public string Markdown => $"**{Title}**\n\n{Summary}\n\n**Recovery:** {Recovery}\n\n**Next step:** {NextStep}\n\n[{LinkLabel}]({Link})";
    }

    // Owner copy is a bounded projection of current operational state. Raw evidence, model
    // chunks and management routing history remain in diagnostics, never in the chat summary.
    internal async Task<IncidentPresentation> PresentAsync(ProjectIncident incident, CancellationToken token)
    {
        var project = await db.Workstreams.AsNoTracking().SingleAsync(x => x.Id == incident.WorkstreamId && x.OrganizationId == incident.OrganizationId, token);
        var projectName = Display(project.Name);
        var link = $"/organizations/{incident.OrganizationId}/projects/{project.Id}";
        AgentCoordinationSession? session = null;
        AgentWorkItem? work = null;
        if (IncidentSource(incident.Fingerprint, "coordination") is { } sessionId)
            session = await db.AgentCoordinationSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sessionId &&
                x.OrganizationId == incident.OrganizationId && x.WorkstreamId == project.Id, token);
        if (session is not null)
        {
            var correlation = session.Id.ToString("D");
            work = await db.AgentWorkItems.AsNoTracking().Where(x => x.OrganizationId == incident.OrganizationId.ToString() &&
                x.SourceType == "agent-coordination" && x.CorrelationId == correlation)
                .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(token);
            link = $"/organizations/{incident.OrganizationId}/communications/{session.ConversationId}?perspective={session.InitiatorOrganizationUserId}";
        }
        else if (IncidentSource(incident.Fingerprint, "work") is { } workId)
            work = await db.AgentWorkItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == workId && x.OrganizationId == incident.OrganizationId.ToString(), token);

        var label = session is null ? "Open project" : "Review collaboration";
        if (incident.Status == "Resolved")
            return new($"{projectName}: blocker cleared", session is null ? "The recorded blocker has cleared." :
                session.Status == AgentCoordinationStatus.Cancelled ? "The affected collaboration was cancelled." : "The affected collaboration completed.",
                "Confirmed from the current work records.", "No action is needed for this incident.", link, label);

        var subject = session is null ? Display(incident.Reason) : Display(session.Subject);
        var summary = session is null ? "The project has a recorded blocker that needs review." : $"The collaboration “{subject}” has not completed.";
        var next = session is null ? "Open the project, review the blocked work, and resolve its recorded dependency before retrying." :
            "Open the collaboration, review the failed step, then choose Retry collaboration when its cause is addressed. The existing transcript is preserved.";
        var recovery = "No recovery action has been recorded.";
        if (work is not null)
        {
            var run = await db.AgentRunLogs.AsNoTracking().Where(x => x.OrganizationId == incident.OrganizationId &&
                x.AgentWorkItemId == work.Id && x.ProviderStartedAt != null).OrderByDescending(x => x.StartedAt).FirstOrDefaultAsync(token);
            var providerName = run is null ? null : await db.LlmProviderProfiles.AsNoTracking().Where(x => x.Id == run.ProviderProfileId)
                .Select(x => x.Name).SingleOrDefaultAsync(token);
            var provider = providerName is null ? "the model provider" : Display(providerName);
            var code = FailureCode(work.LastError);
            if (code is "llm.provider_unavailable" or "llm.request_failed")
            {
                summary = code == "llm.provider_unavailable"
                    ? $"The request to {provider} was interrupted or temporarily unavailable; {subject} is still incomplete."
                    : $"The request to {provider} failed; {subject} is still incomplete. The saved error does not establish the underlying cause.";
                next = session is null ? "Check the provider connection, then retry the affected work." :
                    "Check the provider connection, then open the collaboration and choose Retry collaboration. The existing transcript is preserved.";
            }
            else if (code is "llm.dispatch_denied" or "capability.denied" or "platform.capability.denied")
            {
                summary = "The agent could not use the current provider or permissions. The blocked work has not completed.";
                next = "Review the agent's current provider and approved permissions before retrying the blocked work.";
            }
            var correlation = work.CorrelationId;
            var causation = work.CausationId;
            var attempts = session is null || causation is null ? work.AttemptCount : await db.AgentWorkItems.AsNoTracking().Where(x =>
                x.OrganizationId == work.OrganizationId && x.SourceType == "agent-coordination" && x.CorrelationId == correlation && x.CausationId == causation)
                .SumAsync(x => x.AttemptCount, token);
            recovery = work.Status is AgentWorkStatus.Pending or AgentWorkStatus.Leased
                ? $"A retry is {(work.Status == AgentWorkStatus.Leased ? "running" : "queued")}; completion is not yet confirmed."
                : $"{attempts} execution attempt(s) recorded; no retry is currently queued.";
            if (work.Status is AgentWorkStatus.Pending or AgentWorkStatus.Leased)
                next = "No action is needed while the current attempt runs. This item clears when recovery is confirmed.";
        }
        if (incident.Fingerprint == "health-assessment")
        {
            summary = "C-Sweet could not check this project's progress, so its health is currently unknown.";
            next = "Check the AgentHost service error, apply the platform fix, and restart AgentHost. Monitoring will reassess the project automatically.";
        }
        if (incident.ActionReference is not null && work?.Status is not (AgentWorkStatus.Pending or AgentWorkStatus.Leased))
            recovery = "A manager recorded a recovery action; completion has not yet been confirmed.";
        return new($"{projectName}: work needs attention", summary, recovery, next, link, label);
    }

    private static Guid? IncidentSource(string fingerprint, string kind) => fingerprint.StartsWith(kind + ":", StringComparison.Ordinal) &&
        Guid.TryParse(fingerprint[(kind.Length + 1)..], out var id) ? id : null;

    private static string? FailureCode(string? error) => error?.StartsWith("agent-failure:v1;", StringComparison.Ordinal) == true
        ? error.Split(';').FirstOrDefault(x => x.StartsWith("code=", StringComparison.Ordinal))?[5..] : null;

    private static string Display(string value)
    {
        var plain = Regex.Replace(value, @"[\r\n\t\[\]<>*_`#]", " ").Trim();
        return plain.Length <= 140 ? plain : plain[..137] + "…";
    }
}
