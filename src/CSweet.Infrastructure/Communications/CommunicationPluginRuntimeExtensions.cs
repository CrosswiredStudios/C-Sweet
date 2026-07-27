using CSweet.Communications.Abstractions;
using CSweet.Infrastructure.Setup;
using Microsoft.Extensions.DependencyInjection;

namespace CSweet.Infrastructure.Communications;

public static class CommunicationPluginRuntimeExtensions
{
    public static IServiceCollection AddCommunicationPluginRuntime(this IServiceCollection services)
    {
        services.AddScoped<AgentWorkInbox>();
        services.AddScoped<AgentWorkRouter>();
        services.AddScoped<ICommunicationPluginClient, DurableCommunicationPluginClient>();
        return services;
    }
}
