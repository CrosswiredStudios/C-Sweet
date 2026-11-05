using System.Runtime.CompilerServices;
using CSweet.Agent.SDK;
using CSweet.Infrastructure.Agents;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

/// <summary>
/// Lets an agent inside its VM see its own effective route and remaining shared task budget before it
/// plans expensive work, instead of discovering an exhausted ceiling through a failed model call.
/// Implicit and read-only: it reveals only the caller's installation policy and grants nothing.
/// </summary>
public sealed class HarnessStatusCapabilityHandler(CSweetDbContext db) : IPlatformCapabilityHandler
{
    public const string Capability = "platform.harness.status.read.v1";
    public bool CanHandle(string capability) => capability == Capability;

    public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request,
        [EnumeratorCancellation] CancellationToken token)
    {
        if (!Guid.TryParse(session.InstallationId, out var installation) ||
            !await db.AgentInstallations.AsNoTracking().AnyAsync(x => x.Id == installation && x.BusinessId == session.BusinessId && x.IsEnabled, token))
        {
            yield return new() { RequestId = request.RequestId, Succeeded = false, Error = "The active installation scope is unavailable.",
                ContentType = "application/json", Payload = JsonPayload.FromUtf8("{\"isError\":true}") };
            yield break;
        }
        // Work comes from authenticated queue attribution, never the request payload.
        var status = await HarnessRuntime.ReadStatusAsync(db, session.BusinessId, installation, InferenceExecutionAttribution.Current?.WorkId,
            session.Grant.RequestedCapabilities.Contains(HarnessContextCapabilityHandler.Capability), token);
        yield return new() { RequestId = request.RequestId, Succeeded = true, ContentType = "application/json", Payload = JsonPayload.From(status) };
    }
}
