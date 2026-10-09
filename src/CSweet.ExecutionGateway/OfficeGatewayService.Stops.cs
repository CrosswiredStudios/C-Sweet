using CSweet.Office.Contracts.ControlPlane;
using CSweet.Domain.Setup;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.ExecutionGateway;

public sealed partial class OfficeGatewayService
{
    // Repeated delivery and reconnect discovery cover attempts the Node never received.
    // Rotate past unconfirmed entries without issuing execution authority.
    internal async Task<int> ReconcileStopsAsync(Guid nodeId, long sessionEpoch, int offset,
        IServerStreamWriter<HeadquartersControlMessage> stream, CancellationToken token)
    {
        var attempts = await db.ExecutionAssignmentAttempts.AsNoTracking().Where(x =>
                x.ExecutionNodeId == nodeId && x.StoppedAt == null &&
                db.ExecutionWorkloadAssignments.Any(a => a.Id == x.AssignmentId &&
                    (a.FencingEpoch != x.FencingEpoch || a.Status == ExecutionAssignmentStatus.Completed ||
                     a.Status == ExecutionAssignmentStatus.Cancelled || a.Status == ExecutionAssignmentStatus.Failed ||
                     a.Status == ExecutionAssignmentStatus.Fenced)))
            .OrderBy(x => x.AssignedAt).ThenBy(x => x.AssignmentId).ThenBy(x => x.FencingEpoch)
            .Skip(offset).Take(8).ToArrayAsync(token);
        foreach (var attempt in attempts)
            await stream.WriteAsync(new HeadquartersControlMessage
            {
                ProtocolVersion = "1.0", OfficeId = nodeId.ToString("D"), SessionEpoch = sessionEpoch,
                ReconcileAssignmentStop = new ReconcileAssignmentStop
                {
                    AssignmentId = attempt.AssignmentId.ToString("D"),
                    FencingEpoch = attempt.FencingEpoch, ProviderId = attempt.ProviderId
                }
            }, token);
        return attempts.Length == 0 ? 0 : offset + attempts.Length;
    }
}
