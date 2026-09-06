using System.Text.Json;
using CSweet.Application.Communications;
using CSweet.Contracts.Communications;

namespace CSweet.Infrastructure.Communications;

/// <summary>A native setup action with server-bound identity, never a package-supplied redirect.</summary>
public sealed class PluginSetupUserActionWorkflowResolver : IUserActionWorkflowResolver
{
    public string WorkflowType => SuggestedUserActionWorkflows.OpenPluginSetup;
    public UserActionWorkflowResolution Resolve(Guid organizationId, JsonElement parameters) =>
        throw new UnauthorizedAccessException("Setup navigation requires the authenticated originating installation.");
    public UserActionWorkflowResolution Resolve(Guid organizationId, Guid originatingInstallationId, JsonElement parameters)
    {
        if (organizationId == Guid.Empty || originatingInstallationId == Guid.Empty ||
            parameters.ValueKind != JsonValueKind.Object || parameters.EnumerateObject().Any())
            throw new ArgumentException("Setup navigation takes no caller-selected destination or installation.");
        return new($"/organizations/{organizationId:D}/plugin-setup/{originatingInstallationId:D}", "{}");
    }
}
