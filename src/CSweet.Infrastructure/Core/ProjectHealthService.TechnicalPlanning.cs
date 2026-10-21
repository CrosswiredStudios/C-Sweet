using CSweet.Agent.SDK;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;
using CSweet.Infrastructure.Persistence;
using System.Text.Json;

namespace CSweet.Infrastructure.Core;

public sealed partial class ProjectHealthService
{
    private const string TechnicalPlanningAction = "Escalate this C-Sweet technical issue through the reporting manager to the CEO for a code correction. " +
        "Preserve the approved scope and checkpoints. Retry the affected planning only after the correction is installed; no creative decision is requested.";
    internal static bool IsPlanningGeneratorDiagnostic(string? text) => text is not null &&
        (text.Contains("[csweet.planning.generator]", StringComparison.Ordinal) ||
         text.Contains("Progressive planning is blocked;", StringComparison.Ordinal) ||
         text.Contains("Progressive planning retained the existing scope but could not advance: The delivered outline does not match its retained scope digest.", StringComparison.Ordinal) ||
         text.Contains("Technical planning could not produce a valid proposal after ", StringComparison.Ordinal) ||
         text.Contains("Technical planning returned invalid JSON; revise the proposal.", StringComparison.Ordinal));

    private async Task<string?> TechnicalPlanningDiagnosticAsync(ProjectIncident incident, CancellationToken token) =>
        (await TechnicalPlanningFailureAsync(incident, token))?.Diagnostic;

    private sealed record PlanningFailure(string Diagnostic, Guid? SessionId);

    private async Task<PlanningFailure?> TechnicalPlanningFailureAsync(ProjectIncident incident, CancellationToken token)
    {
        if (IncidentSource(incident.Fingerprint, "coordination") is { } id)
        {
            var session = await db.AgentCoordinationSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id &&
                x.OrganizationId == incident.OrganizationId && x.WorkstreamId == incident.WorkstreamId, token);
            return session?.Status == AgentCoordinationStatus.Blocked && IsPlanningGeneratorDiagnostic(session.FinalSummary)
                ? new(session.FinalSummary!, session.Id) : null;
        }
        var ticketId = incident.AffectedWorkItemId ?? IncidentSource(incident.Fingerprint, "ticket");
        if (ticketId is not { } taskId) return null;
        var task = await db.CoreWorkTasks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == taskId &&
            x.OrganizationId == incident.OrganizationId, token);
        if (task is null || db.HealthProjectForTask(task) != incident.WorkstreamId || task.Status != WorkTaskStatus.Blocked) return null;
        var direct = IsPlanningGeneratorDiagnostic(task.BlockReason) ? new PlanningFailure(task.BlockReason!, null) : null;
        // Recover the old Producer's misleading conflict description only for its exact
        // planning cycle, project, board and initiating employee. Unrelated failures cannot classify it.
        string? fingerprint = null;
        try
        {
            using var context = JsonDocument.Parse(task.PersonalWorkContextJson ?? "{}");
            if (context.RootElement.ValueKind == JsonValueKind.Object && context.RootElement.TryGetProperty("sourceFingerprint", out var value) &&
                value.ValueKind == JsonValueKind.String) fingerprint = value.GetString();
        }
        catch (JsonException) { return direct; }
        var boardId = CSweetDbContext.ReadGuidProperty(task.PersonalWorkContextJson, "boardId");
        if (fingerprint is null || boardId is null || task.AssignedEmployeeId is null) return direct;
        var matches = await db.AgentCoordinationSessions.AsNoTracking().Where(x => x.OrganizationId == incident.OrganizationId &&
            x.WorkstreamId == incident.WorkstreamId && x.SourceKind == "Board" && x.SourceBoardId == boardId &&
            x.InitiatorOrganizationUserId == task.AssignedEmployeeId && x.Status == AgentCoordinationStatus.Blocked &&
            x.Turns.Any(t => t.SpeakerOrganizationUserId == x.InitiatorOrganizationUserId && t.ArtifactKey == fingerprint &&
                t.ArtifactType == "video-game.production.planning-cycle.v1"))
            .OrderByDescending(x => x.UpdatedAt).Take(2).ToArrayAsync(token);
        return matches.Length == 1 && IsPlanningGeneratorDiagnostic(matches[0].FinalSummary)
            ? new(matches[0].FinalSummary!, matches[0].Id) : direct;
    }

    private static IncidentOwnerReport TechnicalPlanningReport(IncidentOwnerReport report) => report with {
        Title = "C-Sweet technical issue: planning generator failed",
        Summary = "C-Sweet could not generate or validate the approved project's planning output. Sprint planning is blocked by a technical failure, not a creative disagreement.",
        Recovery = "A code correction and a verified planning retry are required. Completion has not been confirmed.",
        NextStep = "Managers will track the C-Sweet code correction through the CEO, then retry planning using the saved scope. No decision is requested.",
        RequiresOwnerInput = false, OwnerInputReason = null, Options = [], RecommendedOptionId = "" };

    private static void RetainTechnicalPlanningDiagnosis(ProjectIncident incident, string diagnostic)
    {
        incident.LikelyCause = "C-Sweet technical issue: planning generation or validation failed. " + ProjectHealthReader.Safe(diagnostic);
        incident.RecommendedAction = TechnicalPlanningAction;
        if (!incident.Facts.Contains(diagnostic, StringComparison.Ordinal))
            incident.Facts = ProjectHealthReader.Safe(incident.Facts + "\nSpecialist diagnostic: " + diagnostic);
    }
}
