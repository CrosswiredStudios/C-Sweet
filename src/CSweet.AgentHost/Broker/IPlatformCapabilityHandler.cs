namespace CSweet.AgentHost.Broker;

public interface IPlatformCapabilityHandler
{
    bool CanHandle(string capability);

    IAsyncEnumerable<CapabilityResult> HandleAsync(
        AgentSession session,
        RequestCapability request,
        CancellationToken cancellationToken);
}

internal sealed class LlmPlatformCapabilityAdapter(PlatformLlmCapabilityHandler handler) : IPlatformCapabilityHandler
{
    public bool CanHandle(string capability) => capability == CSweet.Agent.SDK.PlatformCapabilities.LlmChatStream;
    public IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request, CancellationToken token) =>
        handler.StreamAsync(session, request, token);
}

internal sealed class MemoryPlatformCapabilityAdapter(PlatformMemoryCapabilityHandler handler) : IPlatformCapabilityHandler
{
    public bool CanHandle(string capability) => PlatformMemoryCapabilityHandler.IsPlatformMemoryCapability(capability);
    public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        yield return await handler.HandleAsync(session, request, token);
    }
}

internal sealed class WebPlatformCapabilityAdapter(PlatformWebProxyCapabilityHandler handler) : IPlatformCapabilityHandler
{
    public bool CanHandle(string capability) => capability is CSweet.Application.Setup.PluginPlatformCapabilities.WebFetch or CSweet.Application.Setup.PluginPlatformCapabilities.WebRequest;
    public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        yield return await handler.HandleAsync(session, request, token);
    }
}

internal sealed class WebSocketPlatformCapabilityAdapter(PlatformWebSocketCapabilityHandler handler) : IPlatformCapabilityHandler
{
    public bool CanHandle(string capability) => capability == CSweet.Application.Setup.PluginPlatformCapabilities.WebSocket;
    public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        yield return await handler.HandleAsync(session, request, token);
    }
}
