namespace CSweet.Application.Core;

/// <summary>Trusted platform output. The receipt is stored with work, never accepted from an agent.</summary>
public sealed record PreparedMemoryRecall(string? Context, string ReceiptJson)
{
    public string ConversationPrompt { get; init; } = "";
    public string CurrentMessageContent { get; init; } = "";
    public string AgentPrompt { get; init; } = "";
    public PreparedChatMetadata? Metadata { get; init; }
}
