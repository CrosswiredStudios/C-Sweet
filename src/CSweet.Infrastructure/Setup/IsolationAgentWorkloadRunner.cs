using System.Text;
using CSweet.AgentRuntime.Abstractions;
using CSweet.Application.Setup;

namespace CSweet.Infrastructure.Setup;

public sealed class IsolationAgentWorkloadRunner(
    IAgentIsolationProviderSelector selector,
    IEnumerable<IAgentIsolationProvider> providers,
    IAgentGuestSessionCoordinator guestSessions,
    IAgentArtifactMediaStore artifactMedia) : IPluginWorkloadRunner
{
    private readonly IReadOnlyDictionary<string, IAgentIsolationProvider> _providers = providers
        .ToDictionary(provider => provider.Descriptor.ProviderId, StringComparer.Ordinal);

    public async Task<IsolationWorkloadHandle> CreateAndStartAsync(
        RuntimeWorkloadSpec workload,
        AgentTrustLevel trustLevel,
        string? preferredProviderId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workload);
        await artifactMedia.EnsureReadOnlyMediaAsync(workload.Artifact.Digest, cancellationToken);
        IsolationProviderSelection selection;
        try
        {
            selection = await selector.SelectAsync(new IsolationSelectionRequest(
                trustLevel,
                new IsolationCapabilityRequirements(IsolationAssurance.CertifiedHardwareVirtualMachine),
                workload.GuestImage.Digest,
                workload.BrokerLease.ProtocolVersion,
                preferredProviderId), cancellationToken);
        }
        catch (IsolationUnavailableException exception)
        {
            throw new AgentWorkloadException(exception.Message, exception);
        }

        IsolationWorkloadHandle? handle = null;
        try
        {
            handle = await selection.Provider.CreateAsync(workload, cancellationToken);
            await selection.Provider.StartAsync(handle, cancellationToken);
            await guestSessions.StartAsync(handle, workload, cancellationToken);
            return handle;
        }
        catch (Exception exception) when (exception is IsolationUnavailableException or InvalidDataException or IOException or TimeoutException or InvalidOperationException)
        {
            if (handle is not null)
            {
                try { await guestSessions.StopAsync(handle, CancellationToken.None); }
                catch (Exception) { }
                try { await selection.Provider.DestroyAsync(handle, CancellationToken.None); }
                catch (Exception) { }
            }
            throw new AgentWorkloadException("The certified isolation provider could not start the workload.", exception);
        }
    }

    public Task<IsolationWorkloadStatus?> InspectAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) =>
        Provider(handle).InspectAsync(handle, cancellationToken);

    public async Task StopAsync(IsolationWorkloadHandle handle, TimeSpan gracePeriod, CancellationToken cancellationToken = default)
    {
        await guestSessions.StopAsync(handle, cancellationToken);
        await Provider(handle).StopAsync(handle, gracePeriod, cancellationToken);
    }

    public async Task DestroyAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default)
    {
        await guestSessions.StopAsync(handle, cancellationToken);
        await Provider(handle).DestroyAsync(handle, cancellationToken);
    }

    public async Task<string> GetLogsAsync(IsolationWorkloadHandle handle, int maximumBytes, CancellationToken cancellationToken = default)
    {
        if (maximumBytes is < 1 or > 1024 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using var content = new MemoryStream();
        await foreach (var chunk in Provider(handle).StreamLogsAsync(handle, maximumBytes, cancellationToken))
        {
            var remaining = maximumBytes - (int)content.Length;
            if (remaining <= 0) break;
            await content.WriteAsync(chunk.Content[..Math.Min(remaining, chunk.Content.Length)], cancellationToken);
        }
        return Encoding.UTF8.GetString(content.ToArray());
    }

    private IAgentIsolationProvider Provider(IsolationWorkloadHandle handle) =>
        _providers.TryGetValue(handle.ProviderId, out var provider)
            ? provider
            : throw new AgentWorkloadException($"Isolation provider '{handle.ProviderId}' is not registered.");
}
