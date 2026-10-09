using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;
using DomainSession = CSweet.Domain.Communications.AgentCoordinationSession;
using AgentWorkKind = CSweet.Domain.Setup.AgentWorkKind;

namespace CSweet.Infrastructure.Setup;

public sealed partial class AgentWorkInbox
{
    // Called only with the native service's freshly built DTO, inside the transaction
    // that also persists its primary session/turn and notification outbox records.
    internal Task<AgentWorkItem> EnqueueCaseCoordinationAsync(DomainSession session, AgentCoordinationTurnRequest request,
        Guid eventId, DateTimeOffset deadline, CancellationToken token)
    {
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Native collaboration enqueue requires its caller-owned transaction.");
        if (db.Entry(session).State == EntityState.Detached)
            throw new InvalidOperationException("Native collaboration enqueue requires the tracked primary session.");
        var inputs = NativeWorkInputEvidence.Coordination(session, request);
        return EnqueueCoreAsync(session.OrganizationId.ToString("D"), request.Self.AgentInstallationId, AgentWorkKind.Event,
            AgentCoordinationEvents.TurnRequested, JsonSerializer.SerializeToElement(request, JsonOptions),
            $"coordination:{session.Id:N}:turn:{session.NextTurnOrdinal}:revision:{session.Revision}", deadline,
            session.Id.ToString("D"), session.Turns.OrderByDescending(x => x.Ordinal).First().Id.ToString("D"),
            "agent-coordination", eventId.ToString("D"), 3, token, null, inputs);
    }
}
