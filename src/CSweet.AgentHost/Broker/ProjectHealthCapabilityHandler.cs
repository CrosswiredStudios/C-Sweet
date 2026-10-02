using System.Text.Json;
using System.Text.Json.Serialization;
using CSweet.Agent.SDK;
using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

public sealed class ProjectHealthCapabilityHandler(ProjectHealthService health) : IPlatformCapabilityHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public bool CanHandle(string capability) => capability is ProjectHealthCapabilities.Read or ProjectHealthCapabilities.Diagnostics or
        ProjectHealthCapabilities.Incidents or ProjectHealthCapabilities.Report or ProjectHealthCapabilities.Forward;

    public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        CapabilityResult result;
        try
        {
            if (!Guid.TryParse(session.BusinessId, out var organization) || !Guid.TryParse(session.InstallationId, out var installation) ||
                !CanHandle(request.Capability) || request.Payload.Length > 64 * 1024) throw new UnauthorizedAccessException();
            var actor = await health.RequireActorAsync(organization, installation, request.Capability, token);
            var input = request.Payload.ToElement();
            object output = request.Capability switch {
                ProjectHealthCapabilities.Read => await health.ReadAsync(organization, actor, input.Deserialize<ReadProjectHealth>(Json)!, token),
                ProjectHealthCapabilities.Diagnostics => await health.ReadDiagnosticsAsync(organization, actor, input.Deserialize<ReadProjectDiagnostics>(Json)!, token),
                ProjectHealthCapabilities.Incidents => await health.ReadIncidentsAsync(organization, actor, input.Deserialize<ReadManagementIncidents>(Json)!, token),
                ProjectHealthCapabilities.Report => await health.ReportAsync(organization, actor, input.Deserialize<ReportManagementIncident>(Json)!, token),
                _ => await health.ForwardAsync(organization, actor, input.Deserialize<ForwardManagementIncident>(Json)!, token)
            };
            result = new() { RequestId = request.RequestId, Succeeded = true, Payload = JsonPayload.From(output, Json) };
        }
        catch (Exception error) when (error is UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException or DbUpdateException)
        {
            result = new() { RequestId = request.RequestId, Succeeded = false, FailureCode = "project-health.unavailable",
                Error = "The incident is unavailable, permission was revoked, or its revision changed. Re-read current state before acting." };
        }
        yield return result;
    }
}
