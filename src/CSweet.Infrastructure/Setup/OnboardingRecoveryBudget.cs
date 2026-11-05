using System.Text.Json;
using CSweet.Contracts.Agents;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Host-owned kind; agent state handlers cannot write it. Never an execution grant.</summary>
public static class OnboardingRecoveryBudget
{
    public const string Kind = "agent-onboarding-recovery.v1";
    public sealed record Stored(Guid ActorId, Guid PackageVersionId, string ReviewedStateHash,
        OnboardingRecoveryReceipt Receipt);

    public static Task<PluginOperationalState?> FindAsync(CSweetDbContext db, AgentWorkItem work, CancellationToken ct) =>
        db.PluginOperationalStates.AsNoTracking().SingleOrDefaultAsync(x =>
            x.AgentInstallationId == work.AgentInstallationId && x.Kind == Kind && x.ExternalKey == work.Id.ToString("N"), ct);

    public static Stored Read(PluginOperationalState marker, AgentWorkItem work)
    {
        var stored = JsonSerializer.Deserialize<Stored>(marker.PayloadJson)
            ?? throw new InvalidOperationException("The onboarding recovery receipt is unavailable.");
        if (marker.OrganizationId.ToString("D") != work.OrganizationId || marker.AgentInstallationId != work.AgentInstallationId ||
            marker.Kind != Kind || marker.ExternalKey != work.Id.ToString("N") || work.Kind != AgentWorkKind.Event ||
            work.Name != CSweet.Agent.SDK.AgentLifecycleEvents.Onboarded || work.SourceType != "platform-event" ||
            stored.ActorId == Guid.Empty || stored.PackageVersionId == Guid.Empty || stored.Receipt.RecoveryId == Guid.Empty ||
            stored.Receipt.WorkId != work.Id || stored.Receipt.EventId == Guid.Empty || stored.Receipt.EventId.ToString("D") != work.SourceId ||
            stored.Receipt.DeadlineAt - stored.Receipt.RequestedAt != TimeSpan.FromHours(1) ||
            stored.Receipt.PermittedAttempt < 1 || marker.Revision < 1)
            throw new InvalidOperationException("The onboarding recovery receipt is inconsistent.");
        return stored;
    }
}
