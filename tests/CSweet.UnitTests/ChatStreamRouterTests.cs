using CSweet.Api.Chat;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ChatStreamRouterTests
{
    [Theory]
    [InlineData("valid", true)]
    [InlineData("sender", false)]
    [InlineData("conversation", false)]
    [InlineData("installation", false)]
    [InlineData("old", false)]
    [InlineData("other-turn", false)]
    public async Task QuestionResponseReusesOnlyTheCurrentChiefsDurableQuestion(string change, bool expected)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var installation = Guid.NewGuid();
        var turn = new ChatTurn { Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), ConversationId = Guid.NewGuid(),
            TargetAgentOrganizationUserId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow };
        var message = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = turn.ConversationId,
            Role = ConversationRole.Assistant, SenderOrganizationUserId = turn.TargetAgentOrganizationUserId,
            CreatedAt = turn.CreatedAt.AddSeconds(1), Content = "Let's finish your hiring preferences." };
        var decision = new ExecutiveDecision { Id = Guid.NewGuid(), OrganizationId = turn.OrganizationId,
            ConversationId = turn.ConversationId, ConversationMessageId = message.Id, RequestingInstallationId = installation };
        if (change == "sender") message.SenderOrganizationUserId = Guid.NewGuid();
        if (change == "conversation") message.ConversationId = Guid.NewGuid();
        if (change == "installation") decision.RequestingInstallationId = Guid.NewGuid();
        if (change == "old") message.CreatedAt = turn.CreatedAt.AddSeconds(-1);
        if (change == "other-turn") message.ChatTurnId = Guid.NewGuid();
        db.AddRange(message, decision);
        await db.SaveChangesAsync();
        var resolved = await ChatTurnWorker.ResolveTerminalQuestionMessageAsync(db, turn, installation, message.Id, default);
        Assert.Equal(expected, resolved is not null);
        Assert.Equal(1, await db.CoreConversationMessages.CountAsync());
    }

    [Fact]
    public void TerminalQuestionRequiresFinalKindAndNonemptyMessageId()
    {
        var id = Guid.NewGuid();
        var chunk = new ChatStreamChunk(0, string.Empty, true, Kind: "terminal-question-message",
            Metadata: new Dictionary<string, string> { ["messageId"] = id.ToString("D") });
        Assert.True(ChatTurnWorker.TryGetTerminalQuestionMessageId(chunk, out var actual));
        Assert.Equal(id, actual);
        Assert.False(ChatTurnWorker.TryGetTerminalQuestionMessageId(chunk with { IsFinal = false }, out _));
        Assert.False(ChatTurnWorker.TryGetTerminalQuestionMessageId(chunk with { Kind = "output" }, out _));
        Assert.False(ChatTurnWorker.TryGetTerminalQuestionMessageId(chunk with { Metadata = null }, out _));
    }

    [Fact]
    public async Task ConversationAlias_RoutesLegacyChunksToDurableTurn()
    {
        var router = new ChatStreamRouter();
        var conversationId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        router.BindAlias(conversationId, turnId);
        var reader = router.Subscribe(turnId);

        router.Publish(conversationId, new ChatStreamChunk(0, "legacy", IsFinal: false));

        Assert.Equal("legacy", (await reader.ReadAsync()).Delta);
        router.UnbindAlias(conversationId, turnId);
    }

    [Fact]
    public async Task Publish_DeliversChunksInOrder()
    {
        var router = new ChatStreamRouter();
        var conversationId = Guid.NewGuid();
        var reader = router.Subscribe(conversationId);

        router.Publish(conversationId, new ChatStreamChunk(0, "hel", IsFinal: false));
        router.Publish(conversationId, new ChatStreamChunk(1, "lo", IsFinal: false));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        Assert.True(await reader.WaitToReadAsync(timeout.Token));

        Assert.True(reader.TryRead(out var first));
        Assert.True(reader.TryRead(out var second));
        Assert.Equal(new ChatStreamChunk(0, "hel", IsFinal: false), first);
        Assert.Equal(new ChatStreamChunk(1, "lo", IsFinal: false), second);
    }

    [Fact]
    public async Task Publish_FinalChunkCompletesReader()
    {
        var router = new ChatStreamRouter();
        var conversationId = Guid.NewGuid();
        var reader = router.Subscribe(conversationId);

        router.Publish(conversationId, new ChatStreamChunk(0, string.Empty, IsFinal: true));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        Assert.True(await reader.WaitToReadAsync(timeout.Token));
        Assert.True(reader.TryRead(out var final));
        Assert.True(final.IsFinal);

        await reader.Completion.WaitAsync(timeout.Token);
    }

    [Fact]
    public void Publish_UnknownConversationIsNoOp()
    {
        var router = new ChatStreamRouter();

        router.Publish(Guid.NewGuid(), new ChatStreamChunk(0, "ignored", IsFinal: false));
    }

    [Fact]
    public void TerminalResourceChangeChunk_RequiresFinalKindAndDurableRequestId()
    {
        var requestId = Guid.NewGuid();
        var terminal = new ChatStreamChunk(
            1,
            string.Empty,
            IsFinal: true,
            Kind: "terminal-resource-change",
            Metadata: new Dictionary<string, string>
            {
                ["resourceChangeRequestId"] = requestId.ToString("D")
            });

        Assert.True(ChatTurnWorker.TryGetTerminalResourceChangeRequestId(terminal, out var parsed));
        Assert.Equal(requestId, parsed);
        Assert.False(ChatTurnWorker.TryGetTerminalResourceChangeRequestId(
            terminal with { IsFinal = false }, out _));
        Assert.False(ChatTurnWorker.TryGetTerminalResourceChangeRequestId(
            terminal with { Metadata = null }, out _));
    }

    [Fact]
    public async Task Complete_CompletesAndRemovesConversation()
    {
        var router = new ChatStreamRouter();
        var conversationId = Guid.NewGuid();
        var reader = router.Subscribe(conversationId);

        router.Complete(conversationId);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await reader.Completion.WaitAsync(timeout.Token);
    }
}
