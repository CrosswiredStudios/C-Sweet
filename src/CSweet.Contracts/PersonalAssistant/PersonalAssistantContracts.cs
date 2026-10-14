using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using CSweet.Contracts.Plugins;

namespace CSweet.Contracts.PersonalAssistant;

public static class PersonalAssistantCapabilities
{
    public const string AgentId = "com.csweet.personal-assistant";
    public const string Converse = "personal-assistant.converse.v1";
    public const string Analyze = "personal-assistant.analyze-repositories.v1";
    public static string UserScope(Guid userId) => $"user:{userId:D}";
}

public sealed record ConfigurePersonalAssistantRequest(Guid ProviderId, [Required, StringLength(256)] string Model,
    long? ExpectedRevision = null, int MaxContextWindowTokens = 256000, int MaxOutputTokens = 128000,
    bool SetInstallationDefault = false, [StringLength(100)] string? Name = null);
public sealed record PersonalAssistantSettings(Guid? InstallationId, Guid? ProviderId, string? Model,
    long Revision, bool Ready, string? Detail, IReadOnlyList<PluginConfigurationField> Fields,
    IReadOnlyDictionary<string, JsonElement> Settings, string Name = "AI Assistant");
public sealed record SendPersonalAssistantMessageRequest([Required, StringLength(16000)] string Content,
    [Required, StringLength(160)] string IdempotencyKey, Guid? DraftId = null);
public sealed record PersonalAssistantMessageResponse(Guid Id, string Role, string Content, string Status,
    DateTimeOffset CreatedAt, Guid? DraftId);
public sealed record AssistantConversationMessage(string Role, string Content);
public sealed record AssistantConversationRequest(IReadOnlyList<AssistantConversationMessage> Messages, string? Context = null);
public sealed record AssistantConversationResponse(string Message);
