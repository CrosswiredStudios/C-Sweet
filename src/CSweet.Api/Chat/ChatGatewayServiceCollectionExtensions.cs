using CSweet.Api.Communications;
using CSweet.Application.Communications;

namespace CSweet.Api.Chat;

public static class ChatGatewayServiceCollectionExtensions
{
    public static IServiceCollection AddChatGateway(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        _ = configuration;
        services.AddSingleton<IChatStreamRouter, ChatStreamRouter>();
        services.AddScoped<ICommunicationEventPublisher, DurableCommunicationEventPublisher>();
        services.AddHostedService<CommunicationEventOutboxWorker>();
        return services;
    }
}
