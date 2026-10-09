using System.Reflection;
using System.Text.Json;
using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Api.Chat;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresFact]
    public async Task ChatAndBackgroundMemoryTraceWriters_DoNotCollideOnStaleTurns()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture);
        await using var chatDb = fixture.Context();
        await using var memoryDb = fixture.Context();
        await chatDb.ChatTurns.SingleAsync(x => x.Id == turn.Id);
        await memoryDb.ChatTurns.SingleAsync(x => x.Id == turn.Id);
        var memory = fixture.Service(memoryDb, new UsageProviderFactory());
        var append = typeof(AgentMemoryService).GetMethod("AppendTurnMemoryTraceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await ((Task)append.Invoke(memory, [turn.Id, "enrichment.completed", "completed", "Enriched", "Done", CancellationToken.None])!);
        await memoryDb.SaveChangesAsync();
        var chat = new ChatTurnService(chatDb);
        await chat.TraceAsync(turn.Id, "draft", "draft.delta", "running", "First pitch");
        await Task.WhenAll(
            chat.TraceAsync(turn.Id, "output", "final.commit", "completed", "Saved"),
            new ChatTurnService(memoryDb).TraceAsync(turn.Id, "memory", "capture.completed", "completed", "Captured"));
        // An unrelated later tracked update must not restore the stale sequence counter.
        memoryDb.ChatTurns.Local.Single(x => x.Id == turn.Id).PartialResponse = "saved response";
        await memoryDb.SaveChangesAsync();
        await using var verify = fixture.Context();
        var sequences = await verify.ChatTurnTraceEvents.Where(x => x.ChatTurnId == turn.Id)
            .OrderBy(x => x.Sequence).Select(x => x.Sequence).ToArrayAsync();
        Assert.Equal(new long[] { 0, 1, 2, 3 }, sequences);
        Assert.Equal(4, (await verify.ChatTurns.SingleAsync(x => x.Id == turn.Id)).NextTraceSequence);
    }

    [MemoryPostgresFact]
    public async Task RecoveredChat_DeliversCompletedDispatchFinalResponseWithoutNewWork()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture);
        var (_, existing) = await SeedBrokerReadLeaseAsync(fixture);
        var protection = new EphemeralDataProtectionProvider();
        var protector = protection.CreateProtector("CSweet.AgentWorkInbox.v1");
        const string response = "I created revision 1 of the game pitch and submitted it for your review.";
        await using (var db = fixture.Context())
        {
            var work = await db.AgentWorkItems.SingleAsync(x => x.Id == existing.Id);
            work.SourceType = "chat-turn";
            work.SourceId = turn.Id.ToString("D");
            work.IdempotencyKey = $"chat-turn:{turn.Id:D}:attempt:1";
            work.Status = AgentWorkStatus.Completed;
            work.CompletedAt = DateTimeOffset.UtcNow;
            work.ProtectedResult = protector.Protect(JsonSerializer.SerializeToUtf8Bytes(new AgentWorkCompletion(true, null, null)));
            var attempt = await db.AgentWorkAttempts.SingleAsync(x => x.AgentWorkItemId == work.Id);
            attempt.FinishedAt = DateTimeOffset.UtcNow;
            for (var sequence = 1; sequence <= 100; sequence++)
            {
                var draft = new AssistantResponseChunk(turn.ConversationId.ToString("D"), sequence, "draft", false,
                    TurnId: turn.Id, Kind: "draft.delta", Attempt: 1);
                var draftBytes = JsonSerializer.SerializeToUtf8Bytes(draft, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                db.AgentWorkProgress.Add(new AgentWorkProgress { Id = Guid.NewGuid(), AgentWorkItemId = work.Id,
                    AgentWorkAttemptId = attempt.Id, Sequence = sequence, ProtectedValue = protector.Protect(draftBytes),
                    SizeBytes = draftBytes.Length, OccurredAt = DateTimeOffset.UtcNow });
            }
            var chunk = new AssistantResponseChunk(turn.ConversationId.ToString("D"), 101, response, true,
                TurnId: turn.Id, Kind: "final.commit", Attempt: 1);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(chunk, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            db.AgentWorkProgress.Add(new AgentWorkProgress { Id = Guid.NewGuid(), AgentWorkItemId = work.Id,
                AgentWorkAttemptId = attempt.Id, Sequence = 101, ProtectedValue = protector.Protect(bytes),
                SizeBytes = bytes.Length, OccurredAt = DateTimeOffset.UtcNow });
            var current = await db.ChatTurns.SingleAsync(x => x.Id == turn.Id);
            current.Status = ChatTurnStatus.Running;
            current.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        var services = new ServiceCollection();
        services.AddScoped(_ => fixture.Context());
        services.AddScoped<IChatTurnService>(p => new ChatTurnService(p.GetRequiredService<CSweetDbContext>()));
        services.AddScoped<IConversationService>(p => new ConversationService(p.GetRequiredService<CSweetDbContext>()));
        services.AddScoped<IAgentMemoryService>(p => fixture.Service(p.GetRequiredService<CSweetDbContext>(), new UsageProviderFactory()));
        services.AddScoped<AgentWorkInbox>(p => new AgentWorkInbox(p.GetRequiredService<CSweetDbContext>(), protection, TimeProvider.System));
        services.AddSingleton<IAgentInstallationConfigurationService>(new StaticInstallationConfigurationService(fixture.InstallationId, fixture.ProviderId, "test-model"));
        services.AddSingleton<IAgentInteractiveRuntimeService>(new ResetMessageReadyRuntime());
        services.AddSingleton<IAuditEventWriter>(new ResetMessageAudit());
        using var provider = services.BuildServiceProvider();
        var worker = new ChatTurnWorker(provider.GetRequiredService<IServiceScopeFactory>(), new ChatStreamRouter(),
            new ChatTurnEventRouter(), Options.Create(new ChatTurnOptions()), NullLogger<ChatTurnWorker>.Instance);
        var owner = (string)typeof(ChatTurnWorker).GetField("_leaseOwner", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(worker)!;
        await using (var scope = provider.CreateAsyncScope())
        {
            var turns = scope.ServiceProvider.GetRequiredService<IChatTurnService>();
            Assert.Equal(turn.Id, await turns.ClaimNextAsync(owner));
            var process = typeof(ChatTurnWorker).GetMethod("ProcessAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await ((Task)process.Invoke(worker, [scope.ServiceProvider, turn.Id, CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(30));
        }
        await using var verify = fixture.Context();
        var completed = await verify.ChatTurns.SingleAsync(x => x.Id == turn.Id);
        Assert.Equal(ChatTurnStatus.Completed, completed.Status);
        Assert.Equal(1, completed.Attempt);
        Assert.Equal(response, (await verify.CoreConversationMessages.SingleAsync(x => x.Id == completed.AssistantMessageId)).Content);
        Assert.Equal(existing.Id, (await verify.AgentWorkItems.SingleAsync(x => x.SourceId == turn.Id.ToString("D"))).Id);
    }
}
