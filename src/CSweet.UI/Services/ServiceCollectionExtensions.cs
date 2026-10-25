using CSweet.UI.Localization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CSweet.UI.Services;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCSweetApiClients(this IServiceCollection services)
    {
        services.AddScoped<ISetupApiClient, SetupApiClient>();
        services.AddScoped<ILlmProviderApiClient, LlmProviderApiClient>();
        services.AddScoped<PersonalAssistantApiClient>();
        services.AddScoped<IGenAiProviderApiClient, GenAiProviderApiClient>();
        services.AddScoped<IAgentRuntimeSettingsApiClient, AgentRuntimeSettingsApiClient>();
        services.AddScoped<IPluginAccountApiClient, PluginAccountApiClient>();
        services.AddScoped<IOrganizationApiClient, OrganizationApiClient>();
        services.AddScoped<CalendarApiClient>();
        services.AddScoped<IBusinessContext, BusinessContext>();
        services.AddScoped<IBusinessOnboardingApiClient, BusinessOnboardingApiClient>();
        services.AddScoped<IPlanningApiClient, PlanningApiClient>();
        services.AddScoped<IChatTurnApiClient, ChatTurnApiClient>();
        services.AddScoped<IAgentApiClient, AgentApiClient>();
        services.AddScoped<IPluginApiClient, PluginApiClient>();
        services.AddScoped<AuthSessionStore>();
        services.AddCSweetLocalization();
        services.AddScoped<IAuthenticationApiClient, AuthenticationApiClient>();
        services.AddScoped<AppRealtimeState>();
        services.AddScoped<IAppRealtimeConnection>(sp => sp.GetRequiredService<AppRealtimeState>());
        services.AddScoped<AppearanceState>();
        services.AddScoped<ExecutionCapacityAlertState>();
        services.AddScoped<CommunicationUnreadState>();
        services.AddSingleton(sp => NotificationSoundOptions.FromConfiguration(sp.GetService<IConfiguration>()));
        services.AddScoped<NotificationSoundPlayer>();
        services.AddScoped<ApprovalState>();
        services.AddScoped<OverviewAttentionState>();
        services.AddScoped<AgentHireOperationState>();
        services.AddScoped<BusinessOnboardingOperationState>();

        return services;
    }
}
