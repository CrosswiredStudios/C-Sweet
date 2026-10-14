using CSweet.Application.Communications;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;
using CSweet.Agent.SDK;
using CSweet.Domain.Setup;
using System.Text.Json;

namespace CSweet.Infrastructure.Communications;

public sealed partial class ExecutiveDecisionService
{
    private ProjectHealthService IncidentHealth => new(db, new ProjectHealthReader(db, TimeProvider.System), TimeProvider.System);

    private async Task ValidateIncidentChoiceAsync(CreateExecutiveDecisionCommand command, CancellationToken token)
    {
        if (command.Incident is not { } binding) return;
        if (command.ConfigurationChange is not null || command.WorkstreamDecisionId is not null || command.TaskMerge is not null)
            throw new ArgumentException("An incident card cannot combine decision actions.");
        var incident = await db.ProjectIncidents.SingleOrDefaultAsync(x => x.Id == binding.IncidentId && x.OrganizationId == command.OrganizationId, token)
            ?? throw new UnauthorizedAccessException();
        var reporter = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.Id == incident.ReporterOrganizationUserId &&
            x.OrganizationId == command.OrganizationId && x.IsActive && x.AgentInstallationId == command.RequestingInstallationId, token)
            ?? throw new UnauthorizedAccessException();
        if (!await CurrentIncidentChoiceAsync(binding, command.OrganizationId, incident.CurrentRecipientId, reporter.Id, token))
            throw new InvalidOperationException("The incident report changed.");
        var report = ProjectHealthService.OwnerReport(incident)!;
        if (command.RecommendedOptionId != report.RecommendedOptionId ||
            !report.Options.Select(x => (x.Id, x.Label, (string?)x.Description)).SequenceEqual(command.Options.Select(x => (x.Id, x.Label, x.Description))))
            throw new ArgumentException("The card must display the persisted manager options.");
    }

    private async Task<bool> CurrentIncidentChoiceAsync(IncidentDecisionChoice binding, Guid organization, Guid actor,
        Guid reporterId, CancellationToken token)
    {
        var incident = await db.ProjectIncidents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == binding.IncidentId && x.OrganizationId == organization, token);
        if (incident is null || incident.Status != "Open" || incident.CurrentRecipientId != actor ||
            incident.ReportRevision != binding.ReportRevision || incident.ReporterOrganizationUserId != reporterId)
            return false;
        var reporter = await db.CoreOrganizationUsers.Include(x => x.AgentInstallation)!.ThenInclude(x => x!.Grant)
            .SingleOrDefaultAsync(x => x.Id == reporterId && x.OrganizationId == organization && x.IsActive, token);
        if (reporter?.AgentInstallation is not { IsEnabled: true, RevisionStatus: PluginRevisionStatus.Active, Grant: not null } installation)
            return false;
        var capabilities = JsonSerializer.Deserialize<string[]>(installation.Grant.RequiredCapabilitiesJson, JsonOptions) ?? [];
        if (!capabilities.Contains(ProjectHealthCapabilities.Presentation) || !capabilities.Contains(ProjectHealthCapabilities.Resolution)) return false;
        try
        {
            await IncidentHealth.ReadIncidentsAsync(organization, actor, new(IncidentId: incident.Id), token);
            await IncidentHealth.ReadIncidentsAsync(organization, reporterId, new(IncidentId: incident.Id), token);
        }
        catch (UnauthorizedAccessException) { return false; }
        if (!await db.CoreOrganizationUsers.AnyAsync(x => x.Id == actor && x.OrganizationId == organization && x.IsActive &&
            x.EmployeeType == EmployeeType.Human, token)) return false;
        return ProjectHealthService.OwnerReport(incident)?.SourceHash == (await IncidentHealth.CurrentIncidentStateAsync(incident, token)).Hash;
    }

    private async Task<bool> CurrentIncidentDecisionAsync(ExecutiveDecision decision, Guid actor, CancellationToken token)
    {
        var binding = ReadDecisionOptions(decision.OptionsJson).Incident;
        if (binding is null) return true;
        var reporterId = await db.CoreOrganizationUsers.Where(x => x.OrganizationId == decision.OrganizationId &&
            x.AgentInstallationId == decision.RequestingInstallationId && x.IsActive).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(token);
        return reporterId is { } reporter && await CurrentIncidentChoiceAsync(binding, decision.OrganizationId, actor, reporter, token);
    }

    internal static CSweet.Contracts.Communications.ExecutiveDecisionCardResponse IncidentCard(ExecutiveDecision decision) => ToCard(decision);

    internal static void RecordIncidentOutcome(ExecutiveDecision decision, string outcome) => decision.OptionsJson =
        JsonSerializer.Serialize(ReadDecisionOptions(decision.OptionsJson) with { ResolutionOutcome = outcome }, JsonOptions);

    internal static string IncidentIntendedOutcome(ExecutiveDecision decision) => decision.FreeTextAnswer ??
        ReadDecisionOptions(decision.OptionsJson).IncidentIntendedOutcomes?.GetValueOrDefault(decision.SelectedOptionId ?? "") ??
        ReadDecisionOptions(decision.OptionsJson).Options.Single(x => x.Id == decision.SelectedOptionId).Label;

    internal static string? RecordedIncidentOutcome(ExecutiveDecision decision) => ReadDecisionOptions(decision.OptionsJson).ResolutionOutcome;
}
