using System.Text.Json;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed partial class AgentWorkInbox
{
    // Trusted orchestration recovery only. Never exposed through an agent callback.
    internal async Task<(AgentWorkItem Work, JsonElement Payload)?> ReadPendingDeliveryDispatchAsync(
        Guid organization, Guid installation, Guid stage, CancellationToken ct)
    {
        var source = stage.ToString("D");
        var work = await db.AgentWorkItems.SingleOrDefaultAsync(x => x.OrganizationId == organization.ToString() &&
            x.AgentInstallationId == installation && x.SourceType == "WorkDeliveryStage" && x.SourceId == source &&
            x.Name == CSweet.WorkManagement.Contracts.WorkManagementCapabilityNames.ExecutionRun &&
            x.Kind == AgentWorkKind.Capability && x.Status == AgentWorkStatus.Pending, ct);
        if (work is null) return null;
        return (work, JsonDocument.Parse(_protector.Unprotect(work.ProtectedPayload)).RootElement.Clone());
    }
}
