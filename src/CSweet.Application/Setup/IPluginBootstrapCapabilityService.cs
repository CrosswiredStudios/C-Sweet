using System.Text.Json;

namespace CSweet.Application.Setup;

public interface IPluginBootstrapCapabilityService
{
    Task<JsonElement> InvokeAsync(Guid organizationId, Guid installationId, string stepId,
        JsonElement arguments, CancellationToken cancellationToken = default);
}
