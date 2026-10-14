using CSweet.Contracts.PersonalAssistant;

namespace CSweet.Application.PersonalAssistant;

public interface IPersonalAssistantService
{
    Task<PersonalAssistantSettings> GetSettingsAsync(Guid userId, CancellationToken token = default);
    Task<PersonalAssistantSettings> ConfigureAsync(Guid userId, ConfigurePersonalAssistantRequest request, CancellationToken token = default);
    Task<IReadOnlyList<PersonalAssistantMessageResponse>> ListMessagesAsync(Guid userId, Guid? draftId = null, CancellationToken token = default);
    Task<PersonalAssistantMessageResponse> SendAsync(Guid userId, SendPersonalAssistantMessageRequest request, CancellationToken token = default);
    Task<Guid> RequireInstallationAsync(Guid userId, CancellationToken token = default);
}
