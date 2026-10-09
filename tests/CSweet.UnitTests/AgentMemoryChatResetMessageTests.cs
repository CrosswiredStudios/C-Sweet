using System.Reflection;
using System.Threading.Channels;
using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Api.Chat;
using CSweet.Contracts.Agents;
using CSweet.Domain.Core;
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
    [MemoryPostgresTheory]
    [InlineData("memory.runtime_reset", "memory.runtime_reset")]
    [InlineData("agent_work_failed", "turn_failed")]
    public async Task RetainedContextChatFailurePreservesResetCodeAndExistingDocumentLink(string incoming, string expected)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture);
        await SeedBrokerReadLeaseAsync(fixture);
        var router = new ResetMessageRouter(incoming);
        var protection = new EphemeralDataProtectionProvider();
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
        var worker = new ChatTurnWorker(provider.GetRequiredService<IServiceScopeFactory>(), router,
            new ChatTurnEventRouter(), Options.Create(new ChatTurnOptions()), NullLogger<ChatTurnWorker>.Instance);
        const string link = "[Created pitch](/documents/existing-pitch)";
        await using (var db = fixture.Context())
        {
            var current = await db.ChatTurns.SingleAsync(x => x.Id == turn.Id);
            current.PartialResponse = link;
            current.LeaseOwner = (string)typeof(ChatTurnWorker).GetField("_leaseOwner", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(worker)!;
            await db.SaveChangesAsync();
        }
        await using (var scope = provider.CreateAsyncScope())
        {
            var process = typeof(ChatTurnWorker).GetMethod("ProcessAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await ((Task)process.Invoke(worker, [scope.ServiceProvider, turn.Id, CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(30));
        }
        await using var verify = fixture.Context();
        var completed = await verify.ChatTurns.SingleAsync(x => x.Id == turn.Id);
        Assert.Equal(expected, completed.ErrorCode);
        Assert.Equal(ChatTurnStatus.CompletedWithWarnings, completed.Status);
        var reply = await verify.CoreConversationMessages.SingleAsync(x => x.Id == completed.AssistantMessageId);
        Assert.Contains(link, reply.Content);
        if (incoming == MemoryRuntimeResetRequiredException.FailureCode)
        {
            Assert.Contains("Documents or approvals already created may still exist", reply.Content);
            Assert.Contains("completed actions were not replayed", reply.Content);
            Assert.DoesNotContain("couldn't complete that request", reply.Content);
        }
        else Assert.Contains("couldn't complete that request", reply.Content);
        Assert.Equal(MemoryCaptureStatus.Completed, (await verify.MemoryCaptureOutbox.SingleAsync(x => x.ConversationMessageId == reply.Id)).Status);
        Assert.Contains(await verify.ChatTurnTraceEvents.Where(x => x.ChatTurnId == turn.Id).ToArrayAsync(),
            x => x.EventType == "turn.completed" && x.DetailsJson!.Contains(expected));
    }

    private sealed class ResetMessageRouter(string error) : IChatStreamRouter
    {
        public ChannelReader<ChatStreamChunk> Subscribe(Guid conversationId)
        {
            var channel = Channel.CreateUnbounded<ChatStreamChunk>();
            channel.Writer.TryWrite(new ChatStreamChunk(1, "Server work failure", true, error, "error"));
            channel.Writer.TryComplete();
            return channel.Reader;
        }
        public void Publish(Guid conversationId, ChatStreamChunk chunk) { }
        public void Complete(Guid conversationId) { }
        public void BindAlias(Guid aliasId, Guid streamId) { }
        public void UnbindAlias(Guid aliasId, Guid streamId) { }
    }

    private sealed class ResetMessageReadyRuntime : IAgentInteractiveRuntimeService
    {
        public Task<AgentRuntimeReadinessResponse> EnsureReadyAsync(Guid installationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentRuntimeReadinessResponse(installationId, Guid.NewGuid(), "Running", "Ready", null, null, null, null, true, false));
        public Task<AgentRuntimeReadinessResponse> GetStatusAsync(Guid installationId, CancellationToken cancellationToken = default) =>
            EnsureReadyAsync(installationId, cancellationToken);
    }

    private sealed class ResetMessageAudit : IAuditEventWriter
    {
        public Task WriteAsync(string eventType, string entityType, Guid? entityId, string? summary,
            string? metadataJson = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Guid> AppendAsync(AuditEventWriteRequest request, CancellationToken cancellationToken = default) => Task.FromResult(Guid.NewGuid());
    }
}
