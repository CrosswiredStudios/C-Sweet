namespace CSweet.Domain.PersonalAssistant;

public sealed class PersonalAssistantProfile
{
    public Guid ApplicationUserId { get; set; }
    public Guid AgentInstallationId { get; set; }
    public string Name { get; set; } = "AI Assistant";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PersonalAssistantMessage
{
    public Guid Id { get; set; }
    public Guid ApplicationUserId { get; set; }
    public Guid? DraftId { get; set; }
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
    public string Status { get; set; } = "Completed";
    public string IdempotencyKey { get; set; } = string.Empty;
    public Guid? AgentWorkItemId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
