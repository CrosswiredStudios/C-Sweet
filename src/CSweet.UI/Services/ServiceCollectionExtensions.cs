using Microsoft.Extensions.DependencyInjection;

namespace CSweet.UI.Services;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCSweetApiClients(this IServiceCollection services)
    {
        services.AddScoped<ISetupApiClient, SetupApiClient>();
        services.AddScoped<ILlmProviderApiClient, LlmProviderApiClient>();
        services.AddScoped<IGenAiProviderApiClient, GenAiProviderApiClient>();
        services.AddScoped<IAgentRuntimeSettingsApiClient, AgentRuntimeSettingsApiClient>();
        services.AddScoped<IOrganizationApiClient, OrganizationApiClient>();
        services.AddScoped<IBusinessContext, BusinessContext>();
        services.AddScoped<IBusinessOnboardingApiClient, BusinessOnboardingApiClient>();
        services.AddScoped<IPlanningApiClient, PlanningApiClient>();
        services.AddScoped<IChatTurnApiClient, ChatTurnApiClient>();
        services.AddScoped<IAgentApiClient, AgentApiClient>();
        services.AddScoped<IPluginApiClient, PluginApiClient>();
        services.AddScoped<AuthSessionStore>();
        services.AddScoped<IAuthenticationApiClient, AuthenticationApiClient>();
        services.AddScoped<AppRealtimeState>();
        services.AddScoped<CommunicationUnreadState>();

        return services;
    }
}
