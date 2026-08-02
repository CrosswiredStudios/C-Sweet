using CSweet.Api.Chat;

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
}
