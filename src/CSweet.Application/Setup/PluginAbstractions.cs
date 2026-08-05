using CSweet.Contracts.Agents;
using CSweet.Domain.Setup;

namespace CSweet.Application.Setup;

// These generic ports deliberately extend the legacy agent ports. Existing agents keep their
// API while new plugin kinds depend only on plugin terminology.
public interface IPluginImportService : IAgentImportPreviewService;
public interface IPluginArchiveImportService
{
    Task<AgentImportPreviewResponse> PreviewSourceArchiveAsync(Stream archive, string fileName, CancellationToken cancellationToken = default);
}
public interface IPluginBuildExecutor : IAgentBuildExecutor;
public interface IPluginRuntimeManager : IAgentRuntimeManager;
public interface IPluginContainerRunner : IAgentContainerRunner;

public interface IPluginSourceResolver
{
    Task<string> ResolveCommitShaAsync(string repositoryUrl, string reference, CancellationToken cancellationToken = default);
}

public interface IPluginManifestReader
{
    PluginManifestEnvelope Read(ReadOnlyMemory<byte> manifestBytes, string manifestFileName);
}

public sealed record PluginManifestEnvelope(
    string ManifestFileName,
    string Kind,
    string Id,
    string Name,
    string Version,
    string ManifestJson);

public interface IPluginAuthorizationPolicy
{
    Task<bool> CanAccessOrganizationAsync(
        Guid installationId,
        Guid organizationId,
        CancellationToken cancellationToken = default);
}

public interface IPluginOAuthTokenBroker
{
    Task<string?> GetAccessTokenAsync(
        Guid installationId,
        PluginConnection connection,
        CancellationToken cancellationToken = default);
}

public static class PluginPlatformCapabilities
{
    public const string WebFetch = "web.fetch.v1";
    public const string WebRequest = "web.request.v1";
    public const string WebRender = "web.render.v1";
    public const string WebSocket = "web.socket.v1";
    public const string MediaTransfer = "platform.media.transfer.v1";
}
