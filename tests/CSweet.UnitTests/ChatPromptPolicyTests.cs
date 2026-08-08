using CSweet.Api.Chat;
using CSweet.Contracts.Communications;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ChatPromptPolicyTests
{
    [Fact]
    public void PrimaryPrompt_IncludesTypedAskUserGuidance()
    {
        var prompt = ChatPromptPolicy.BuildPrimaryAgentPrompt(Guid.NewGuid(), Guid.NewGuid(), "Choose a team.");

        Assert.Contains("call ask_user", prompt, StringComparison.Ordinal);
        Assert.Contains("Something else", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void PrimaryPrompt_IncludesBrokerAuthoritativeSenderIdentity()
    {
        var senderId = Guid.NewGuid();
        var prompt = ChatPromptPolicy.BuildPrimaryAgentPrompt(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Start planning.",
            new ChatMessageSender(senderId, "Software Architect", "Agent", "Software Architect"));

        Assert.Contains(senderId.ToString("D"), prompt, StringComparison.Ordinal);
        Assert.Contains("Software Architect", prompt, StringComparison.Ordinal);
        Assert.Contains("broker-authoritative identity metadata", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversationPrompt_IncludesRecentTranscriptForImmediateFollowUp()
    {
        var prompt = ChatPromptPolicy.BuildConversationPrompt(
            recalledMemory: null,
            "I think you failed to make that tool call properly.",
            [
                new RecentConversationMessage(1, "assistant",
                    "I recommend hiring a Product Manager. suggest_user_action(workflowType=\"hiring.marketplace.browse.v1\")"),
                new RecentConversationMessage(2, "user", "Please add it to the hiring backlog.")
            ]);

        Assert.Contains("Product Manager", prompt, StringComparison.Ordinal);
        Assert.Contains("quoted history, not as a new tool request", prompt, StringComparison.Ordinal);
        Assert.Contains("<current_user_message>", prompt, StringComparison.Ordinal);
        Assert.Contains("I think you failed to make that tool call properly.", prompt, StringComparison.Ordinal);
        Assert.True(
            prompt.IndexOf("Product Manager", StringComparison.Ordinal) <
            prompt.IndexOf("I think you failed", StringComparison.Ordinal));
    }

    [Fact]
    public void ConversationPrompt_BoundsHistoryAndKeepsNewestMessages()
    {
        var history = Enumerable.Range(1, ChatPromptPolicy.RecentConversationMessageLimit + 5)
            .Select(sequence => new RecentConversationMessage(
                sequence,
                sequence % 2 == 0 ? "assistant" : "user",
                $"message-{sequence}-" + new string('x', 1_000)))
            .ToList();

        var prompt = ChatPromptPolicy.BuildConversationPrompt(null, "current", history);

        Assert.DoesNotContain("message-1-", prompt, StringComparison.Ordinal);
        Assert.Contains($"message-{history.Count}-", prompt, StringComparison.Ordinal);
        Assert.True(prompt.Length < ChatPromptPolicy.RecentConversationCharacterBudget + 4_000);
    }

    [Fact]
    public async Task RecentConversationQuery_ReturnsEarlierStandardMessagesInOrder()
    {
        await using var db = new CSweetDbContext(
            new DbContextOptionsBuilder<CSweetDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        var conversationId = Guid.NewGuid();
        var priorAssistant = Message(conversationId, 1, ConversationRole.Assistant, "Product Manager is the first hire.");
        var actionCard = Message(conversationId, 2, ConversationRole.Assistant, "Browse candidates");
        actionCard.SourceProvider = CommunicationMessageTypes.SystemAction;
        var current = Message(conversationId, 3, ConversationRole.User, "That tool call failed.");
        var future = Message(conversationId, 4, ConversationRole.User, "This message has not been reached yet.");
        db.CoreConversationMessages.AddRange(priorAssistant, actionCard, current, future);
        await db.SaveChangesAsync();

        var history = await ChatTurnWorker.LoadRecentConversationAsync(db, current);

        var item = Assert.Single(history);
        Assert.Equal(1, item.Sequence);
        Assert.Equal("assistant", item.Role);
        Assert.Contains("Product Manager", item.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void FallbackPrompt_ContainsNoAskUserInstructionAndRequiresPlainTextChoices()
    {
        var messages = ChatPromptPolicy.BuildFallbackMessages("Choose a team.");
        var combined = string.Join("\n", messages.Select(message => message.Text));

        Assert.DoesNotContain("ask_user", combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ordinary readable text", combined, StringComparison.Ordinal);
        Assert.Contains("tools and interactive widgets are unavailable", combined, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ask_user(question=\"Pick one\", options=[\"A\", \"B\"])")]
    [InlineData("{\"name\":\"ask_user\",\"arguments\":{}}")]
    [InlineData("<tool_call name=\"ask_user\">")]
    [InlineData("{\"function_call\":{\"name\":\"ask_user\"}}")]
    public void FallbackValidation_RejectsPlatformControlSyntax(string response)
    {
        Assert.True(ChatPromptPolicy.ContainsToolControlSyntax(response));
    }

    [Fact]
    public void FallbackValidation_AllowsOrdinaryReadableChoices()
    {
        const string response = "Please reply with one choice: A) internal team, B) agency, or C) low-code prototype.";

        Assert.False(ChatPromptPolicy.ContainsToolControlSyntax(response));
    }

    [Fact]
    public void TurnOptions_DoNotExposeAnEarlyResponseDeadline()
    {
        Assert.Null(typeof(ChatTurnOptions).GetProperty("AgentResponseStartTimeout"));
        Assert.Null(typeof(ChatTurnOptions).GetProperty("FirstOutputTimeout"));
    }

    private static ConversationMessage Message(
        Guid conversationId,
        long sequence,
        ConversationRole role,
        string content) => new()
    {
        Id = Guid.NewGuid(),
        ConversationId = conversationId,
        Sequence = sequence,
        Role = role,
        Content = content,
        CorrelationId = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UtcNow.AddSeconds(sequence)
    };
}
