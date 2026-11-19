using System.Globalization;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Persistence;

public sealed partial class CSweetDbContext
{
    private sealed record WaitSource(Guid Organization, string Kind, Guid Id, string Version);

    private WaitSource[] ChangedPersonalWaitSources() => ChangeTracker.Entries()
        .Where(e => e.State == EntityState.Modified)
        .Select(e => e.Entity switch {
            OrganizationTeam team when e.Property(nameof(team.Revision)).IsModified =>
                new WaitSource(team.OrganizationId, WorkforceEvents.Changed, team.Id, team.Revision.ToString(CultureInfo.InvariantCulture)),
            WorkstreamDecisionRecord decision when e.Property(nameof(decision.Status)).IsModified =>
                new WaitSource(decision.OrganizationId, WorkstreamEventNames.DecisionDecidedV1, decision.Id, decision.Status),
            _ => null
        }).OfType<WaitSource>().OrderBy(s => s.Kind, StringComparer.Ordinal).ThenBy(s => s.Id).ToArray();

    private async Task CapturePersonalDependencyWakesAsync(WaitSource[] sources, CancellationToken ct)
    {
        if (sources.Length == 0) return;
        var engine = new WorkItemMutationEngine(this, ExecutionClock);
        foreach (var source in sources)
        {
            // Serialize registering a wait with changing its source. The card transition and its
            // successor outbox entry commit with the roster or decision that woke it.
            await LockPersonalWaitSourceAsync(source.Organization, source.Kind, source.Id, ct);
            await engine.RecoverDependencyWaitsAsync(ct, organizationId: source.Organization,
                changed: (source.Kind, source.Id), committingVersion: source.Version);
        }
    }

    internal async Task LockPersonalWaitSourceAsync(Guid organization, string kind, Guid id, CancellationToken ct)
    {
        if (!Database.IsNpgsql()) return;
        if (kind == WorkforceEvents.Changed)
            await Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM "OrganizationTeams" WHERE "OrganizationId" = {organization} AND "Id" = {id} FOR UPDATE""", ct);
        else if (kind == WorkstreamEventNames.DecisionDecidedV1)
            await Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM "WorkstreamDecisions" WHERE "OrganizationId" = {organization} AND "Id" = {id} FOR UPDATE""", ct);
    }
}
