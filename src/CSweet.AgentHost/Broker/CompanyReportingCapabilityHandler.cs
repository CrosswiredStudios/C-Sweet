using System.Runtime.CompilerServices;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Contracts.Core;
using CSweet.Infrastructure.Core;
namespace CSweet.AgentHost.Broker;

public sealed class CompanyReportingCapabilityHandler(CompanyDashboardService service) : IPlatformCapabilityHandler
{
    public bool CanHandle(string capability) => capability is CompanyReportingCapabilities.Finance or CompanyReportingCapabilities.Legal or CompanyReportingCapabilities.Project;
    public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request, [EnumeratorCancellation] CancellationToken token)
    {
        string? error = null;
        try
        {
            if (!session.Grant.RequestedCapabilities.Contains(request.Capability) ||
                !Guid.TryParse(session.BusinessId, out var organizationId) || !Guid.TryParse(session.InstallationId, out var installationId))
                throw new UnauthorizedAccessException("This installation is not granted the reporting capability.");
            using var payload = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(request.Payload.Span));
            await service.PublishAsync(organizationId, installationId, request.Capability, payload.RootElement, token);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or UnauthorizedAccessException)
        {
            error = ex.Message;
        }
        yield return new CapabilityResult
        {
            RequestId = request.RequestId, Succeeded = error is null, ContentType = "application/json", Error = error,
            Payload = JsonPayload.FromUtf8(error is null ? "{\"accepted\":true}" : "{\"isError\":true}")
        };
    }
}
