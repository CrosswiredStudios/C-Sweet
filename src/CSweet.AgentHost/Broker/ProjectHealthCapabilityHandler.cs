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
        ProjectHealthCapabilities.Incidents or ProjectHealthCapabilities.Report or ProjectHealthCapabilities.Forward or
        ProjectHealthCapabilities.Presentation or ProjectHealthCapabilities.Resolution;

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
                ProjectHealthCapabilities.Presentation => await health.UpdatePresentationAsync(organization, actor, input.Deserialize<UpdateManagementIncidentPresentation>(Json)!, token),
                ProjectHealthCapabilities.Resolution => await health.ReadResolutionAsync(organization, actor, input.Deserialize<ReadIncidentResolution>(Json)!, token),
                _ => await health.ForwardAsync(organization, actor, input.Deserialize<ForwardManagementIncident>(Json)!, token)
            };
            result = new() { RequestId = request.RequestId, Succeeded = true, Payload = JsonPayload.From(output, Json) };
        }
        catch (Exception error) when (error is UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException or DbUpdateException)
        {
            var (code, retryable, message) = Classify(error);
            result = new() { RequestId = request.RequestId, Succeeded = false, FailureCode = code, Retryable = retryable, Error = message };
        }
        yield return result;
    }

    internal static (string Code, bool Retryable, string Message) Classify(Exception error) => error switch
    {
        ProjectHealthStateException conflict => (conflict.Code, conflict.Code is "project-health.stale_revision" or "project-health.stale_evidence", conflict.Message),
        UnauthorizedAccessException => ("project-health.denied", false, "The incident is unavailable or current authority was revoked."),
        ArgumentException or JsonException => ("project-health.invalid_report", false, "The incident report failed validation; correct its fields before resubmitting."),
        DbUpdateConcurrencyException => ("project-health.stale_revision", true, "Incident state changed concurrently; read current state."),
        DbUpdateException => ("project-health.persistence_failed", true, "The incident report could not be persisted; retain the same request identity."),
        _ => ("project-health.conflict", false, "The incident operation conflicts with current state; read its current diagnostics.")
    };
}
