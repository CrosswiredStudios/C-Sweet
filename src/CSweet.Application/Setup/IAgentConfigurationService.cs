using CSweet.Contracts.Agents;

namespace CSweet.Application.Setup;

public interface IAgentConfigurationService
{
    Task<AgentConfigurationView> GetDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default);
    Task<AgentConfigurationView> SaveDefinitionAsync(Guid definitionId,
        PutAgentDefinitionConfigurationRequest request, CancellationToken cancellationToken = default);
    Task<AgentConfigurationView> GetEmployeeAsync(Guid organizationId, Guid employeeId,
        CancellationToken cancellationToken = default);
    Task<AgentConfigurationView> SaveEmployeeOverridesAsync(Guid organizationId, Guid employeeId,
        PutAgentConfigurationOverridesRequest request, CancellationToken cancellationToken = default);
    Task<AgentConfigurationView> RestoreEmployeeOverrideAsync(Guid organizationId, Guid employeeId, string key, long expectedRevision,
        CancellationToken cancellationToken = default);
    Task<AgentConfigurationView> RestoreAllEmployeeOverridesAsync(Guid organizationId, Guid employeeId, long expectedRevision,
        CancellationToken cancellationToken = default);
    Task<EffectiveAgentConfiguration> ResolveInstallationAsync(Guid installationId,
        CancellationToken cancellationToken = default);
}

public sealed record EffectiveAgentConfiguration(
    Guid InstallationId,
    string SchemaVersion,
    IReadOnlyDictionary<string, System.Text.Json.JsonElement> Settings,
    long Revision,
    string Digest);
