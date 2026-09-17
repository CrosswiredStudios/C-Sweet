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
            await InferenceAttribution.CaptureAsync(_context, log, log.AgentWorkItemId, cancellationToken);
            var existing = await _context.AgentRunLogs.FindAsync([log.Id], cancellationToken);
            if (existing is null) _context.AgentRunLogs.Add(log);
            else _context.Entry(existing).CurrentValues.SetValues(log);
        }
        await _context.SaveChangesAsync(cancellationToken);
    }
}
