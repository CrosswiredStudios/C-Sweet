using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [Fact]
    public async Task Project_chat_metadata_uses_current_name_without_changing_the_memory_or_conversation_identity()
    {
        await using var fixture = await DurabilityFixture.CreateAsync();
        await using var db = fixture.Context();
        var message = await db.CoreConversationMessages.SingleAsync(x => x.Id == fixture.MessageId);
        var turn = new ChatTurn { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, ConversationId = message.ConversationId,
            UserMessageId = message.Id, TargetAgentOrganizationUserId = fixture.EmployeeId, Attempt = 1, Status = ChatTurnStatus.RecallingMemory };
        db.ChatTurns.Add(turn);
        var human = await db.CoreOrganizationUsers.SingleAsync(x => x.Id == fixture.HumanId);
        human.PermissionLevel = OrganizationPermissionLevel.Owner;
        var project = new Workstream { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, Name = "Prototype",
            AccountableManagerOrganizationUserId = human.Id };
        db.Workstreams.Add(project);
        (await db.CoreConversations.SingleAsync(x => x.Id == turn.ConversationId)).WorkstreamId = project.Id;
        await db.SaveChangesAsync();
        var evidence = new MemoryRecallDispatchEvidence(db);
        var binding = new MemoryRecallDispatchEvidence.Binding(turn.Id, 1, fixture.OrganizationId, turn.ConversationId, message.Id,
            fixture.EmployeeId, fixture.HumanId, fixture.InstallationId, new string('A', 64));
        var before = await evidence.CapturePromptAsync(binding, null, default);
        Assert.Equal("Prototype", before.Metadata!.Context["projectName"]);
        await new ProjectSetupService(db, TimeProvider.System, new(db, TimeProvider.System)).RenameAsync(human, project.Id, new("NEON BREAKER", 1), default);
        var after = await evidence.CapturePromptAsync(binding, null, default);
        Assert.Equal("NEON BREAKER", after.Metadata!.Context["projectName"]);
        Assert.Equal(project.Id.ToString("D"), after.Metadata.Context["projectId"]);
        Assert.Equal(before.Metadata.WorkContext, after.Metadata.WorkContext);
        Assert.Equal(before.Current, after.Current);
        Assert.Equal(before.Conversation, after.Conversation);
        Assert.NotEqual(before.Evidence.Auxiliary!.ContextHash, after.Evidence.Auxiliary!.ContextHash);
    }
}
