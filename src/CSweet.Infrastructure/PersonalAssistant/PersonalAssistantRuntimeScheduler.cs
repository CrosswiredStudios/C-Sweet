using CSweet.Application.PersonalAssistant;
using CSweet.Application.Setup;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.PersonalAssistant;

public sealed class PersonalAssistantRuntimeScheduler(IAgentRuntimeManager runtimes,
    ILogger<PersonalAssistantRuntimeScheduler> logger) : IPersonalAssistantRuntimeScheduler
{
    public async Task WakeAsync(Guid installationId, CancellationToken token)
    {
        try { await runtimes.EnsureRuntimeQueuedAsync(installationId, "Personal assistant work is available.", true, token); }
        catch (AgentInstallationException exception)
        { logger.LogInformation(exception, "Personal assistant work remains queued until its runtime is ready."); }
    }
}
