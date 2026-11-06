using System.Runtime.CompilerServices;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Agents;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

/// <summary>Bounded, installation-bound discovery after a wake or missed connection.</summary>
public sealed class AgentGovernanceCapabilityHandler(CSweetDbContext db, IAgentPostureReader postures) : IPlatformCapabilityHandler
{
    public const string Read = "platform.agent-governance.read.v1";
    public bool CanHandle(string capability) => capability == Read;
    public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request,
        [EnumeratorCancellation] CancellationToken token)
    {
        if (!Guid.TryParse(session.InstallationId, out var installation) ||
            !await db.AgentInstallations.AsNoTracking().AnyAsync(x => x.Id == installation && x.BusinessId == session.BusinessId &&
                x.IsEnabled && x.RevisionStatus == CSweet.Domain.Setup.PluginRevisionStatus.Active, token))
        {
            yield return new() { RequestId = request.RequestId, Succeeded = false, Error = "The active installation scope is unavailable.", Payload = JsonPayload.FromUtf8("{\"isError\":true}") };
            yield break;
        }
        Guid? approvalId = null;
        if (!request.Payload.IsEmpty)
        {
            using var input = JsonDocument.Parse(request.Payload.Span.ToArray());
            if (input.RootElement.TryGetProperty("approvalId", out var value) && value.TryGetGuid(out var parsed)) approvalId = parsed;
        }
        var posture = await postures.GetAsync(installation, token);
        var approvals = await db.AgentCapabilityApprovals.AsNoTracking().Where(x => x.AgentInstallationId == installation && (approvalId == null || x.Id == approvalId))
            .OrderByDescending(x => x.CreatedAt).Take(20)
            .Select(x => new { x.Id, x.Capability, PayloadJson = approvalId == null ? null : x.PayloadJson, x.Status, x.UpdatedAt }).ToListAsync(token);
        yield return new() { RequestId = request.RequestId, Succeeded = true, ContentType = "application/json",
            Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new { posture = posture.Posture.ToString(), approvals }, new JsonSerializerOptions(JsonSerializerDefaults.Web))) };
    }
}
