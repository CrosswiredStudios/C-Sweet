using CSweet.Application.Llm;
using CSweet.Infrastructure.Analytics;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;

namespace CSweet.Infrastructure.Llm;

public sealed class AgentRunLogWriter : IAgentRunLogWriter
{
    private readonly CSweetDbContext _context;

    public AgentRunLogWriter(CSweetDbContext context)
    {
        _context = context;
    }

    public async Task WriteAsync(AgentRunLog log, CancellationToken cancellationToken = default)
    {
        if (_context.Entry(log).State == Microsoft.EntityFrameworkCore.EntityState.Detached)
        {
            var existing = await _context.AgentRunLogs.FindAsync([log.Id], cancellationToken);
            if (existing is null)
            {
                if (log.WorkItemId is null && log.AgentWorkAttemptId is null)
                    await InferenceAttribution.CaptureAsync(_context, log, log.AgentWorkItemId, cancellationToken);
                _context.AgentRunLogs.Add(log);
            }
            else
            {
                log.WorkItemId = existing.WorkItemId;
                log.AgentWorkAttemptId = existing.AgentWorkAttemptId;
                log.AgentWorkItemId = existing.AgentWorkItemId;
                log.WorkstreamId = existing.WorkstreamId;
                log.AncestorWorkItemIdsJson = existing.AncestorWorkItemIdsJson;
                log.AttributionKind = existing.AttributionKind;
                _context.Entry(existing).CurrentValues.SetValues(log);
            }
        }
        await _context.SaveChangesAsync(cancellationToken);
    }
}
