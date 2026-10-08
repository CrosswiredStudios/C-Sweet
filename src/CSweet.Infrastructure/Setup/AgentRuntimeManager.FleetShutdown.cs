using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed partial class AgentRuntimeManager
{
    private async Task<bool> ConfirmFleetShutdownAsync(AgentRuntimeInstance instance, CancellationToken token)
    {
        // Startup can fail before CreateAndStart returns its handle. Durable fleet rows
        // still bind those attempts to this runtime; absence of an in-memory handle is not proof.
        var assignments = await dbContext.ExecutionWorkloadAssignments.AsNoTracking().Where(x =>
            x.AgentRuntimeInstanceId == instance.Id && x.WorkloadKind == ExecutionWorkloadKind.Runtime)
            .OrderBy(x => x.Id).Select(x => x.Id).Take(65).ToArrayAsync(token);
        if (assignments.Length > 64) return false;
        var confirmed = true;
        foreach (var assignmentId in assignments)
        {
            var handle = new IsolationWorkloadHandle("execution-fleet", instance.Id, assignmentId.ToString("N"),
                CSweet.Office.Contracts.Workloads.WorkloadKind.Runtime);
            await workloads.StopAsync(handle, TimeSpan.Zero, token);
            confirmed &= IsStopped(await workloads.InspectAsync(handle, token));
        }
        return confirmed;
    }
}
