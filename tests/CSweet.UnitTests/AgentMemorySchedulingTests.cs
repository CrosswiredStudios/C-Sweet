using System.Reflection;
using CSweet.Application.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using CSweet.Infrastructure.Persistence.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresFact]
    public async Task SchedulingUpgradePreservesJobsAndRecoversOrphanedProviderReservation()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context();
        var original = await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync();
        var migration = new FairMemoryEnrichmentScheduling();
        var generator = db.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate(migration.DownOperations))
            await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in generator.Generate(migration.UpOperations))
            await db.Database.ExecuteSqlRawAsync(command.CommandText);
        var restored = await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync();
        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(original.Status, restored.Status);
        Assert.Null(restored.LastAttemptAt);
        // A process can die between releasing its job and releasing the provider slot.
        db.MemoryEnrichmentProviderLeases.Add(new() { ProviderId = fixture.ProviderId, JobId = Guid.NewGuid(),
            LeaseToken = Guid.NewGuid(), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(4) });
        await db.SaveChangesAsync();
        var factory = new ScriptedProviderFactory((_, _) => Task.CompletedTask);
        Assert.Equal(1, await fixture.Service(db, factory).ProcessPendingAsync(limit: 1));
        Assert.Equal(1, factory.Calls);
        Assert.NotNull((await db.MemoryCaptureOutbox.SingleAsync()).LastAttemptAt);
        Assert.Empty(await db.MemoryEnrichmentProviderLeases.AsNoTracking().ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task BusyProviderDefersAnotherJobWithoutSpendingAttempts()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new ScriptedProviderFactory(async (call, token) =>
        {
            if (call == 1) { entered.SetResult(); await release.Task.WaitAsync(token); }
        });
        await using var first = fixture.Context();
        var processing = fixture.Service(first, factory).ProcessPendingAsync(limit: 1);
        Guid secondId;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await using var second = fixture.Context();
            var source = await second.CoreConversationMessages.SingleAsync();
            secondId = Guid.NewGuid();
            second.Add(new ConversationMessage { Id = secondId, ConversationId = source.ConversationId,
                // Keep this source eligible even when both workers finish in under a
                // second. Future occurrence times are intentionally withheld by provenance.
                Role = ConversationRole.User, Content = "Another request", CreatedAt = DateTimeOffset.UtcNow });
            await second.SaveChangesAsync();
            Assert.Equal(0, await fixture.Service(second, factory).ProcessPendingAsync(limit: 1));
            var waiting = await second.MemoryCaptureOutbox.SingleAsync(x => x.ConversationMessageId == secondId);
            Assert.Equal(0, waiting.Attempts);
            Assert.Equal(MemoryCaptureStatus.Pending, waiting.Status);
            Assert.Equal("memory_provider_busy", waiting.LastError);
            Assert.Equal(1, factory.Calls);
            waiting.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await second.SaveChangesAsync();
        }
        finally { release.TrySetResult(); }
        Assert.Equal(1, await processing);
        await using var restarted = fixture.Context();
        Assert.Equal(1, await fixture.Service(restarted, factory).ProcessPendingAsync(limit: 1));
        Assert.Equal(2, factory.Calls);
        Assert.Empty(await restarted.MemoryEnrichmentProviderLeases.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task AnOldTenantBacklogDoesNotStarveAnotherTenant()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context();
        var source = await db.CoreConversationMessages.SingleAsync();
        source.Role = ConversationRole.Assistant;
        for (var index = 1; index <= 4; index++)
            db.Add(new ConversationMessage { Id = Guid.NewGuid(), ConversationId = source.ConversationId,
                Role = ConversationRole.Assistant, Content = "Backlog", CreatedAt = source.CreatedAt.AddDays(-index) });
        var organization = new Organization { Id = Guid.NewGuid(), Name = "Other tenant" };
        var originalInstall = await db.AgentInstallations.SingleAsync();
        var installation = new AgentInstallation { Id = Guid.NewGuid(), PackageVersionId = originalInstall.PackageVersionId,
            InstallationKey = Guid.NewGuid(), BusinessId = organization.Id.ToString(), IsEnabled = true };
        var agent = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organization.Id, EmployeeType = EmployeeType.Agent,
            AgentInstallationId = installation.Id, AgentInstallation = installation };
        var human = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organization.Id, EmployeeType = EmployeeType.Human };
        var conversation = new Conversation { Id = Guid.NewGuid(), OrganizationId = organization.Id, AgentOrganizationUserId = agent.Id,
            AgentOrganizationUser = agent, InitiatedByOrganizationUserId = human.Id };
        var other = new ConversationMessage { Id = Guid.NewGuid(), Conversation = conversation, ConversationId = conversation.Id,
            Role = ConversationRole.Assistant, Content = "New tenant", CreatedAt = source.CreatedAt.AddDays(1) };
        db.AddRange(organization, agent, human, other);
        await db.SaveChangesAsync();
        var service = fixture.Service(db, new UsageProviderFactory());
        Assert.Equal(1, await service.ProcessPendingAsync(limit: 1));
        Assert.Equal(1, await service.ProcessPendingAsync(limit: 1));
        Assert.Equal(MemoryCaptureStatus.Completed, (await db.MemoryCaptureOutbox.SingleAsync(x => x.ConversationMessageId == other.Id)).Status);
        Assert.True(await db.MemoryCaptureOutbox.AnyAsync(x => x.Status == MemoryCaptureStatus.Pending));
    }

    [Fact]
    public async Task WorkerDoesNotGloballyPauseForInteractiveTraffic()
    {
        var memory = DispatchProxy.Create<IAgentMemoryService, RecordingMemoryWorkerProxy>();
        var proxy = (RecordingMemoryWorkerProxy)(object)memory;
        // No chat-state service is supplied: background capacity is independent of global chat activity.
        await using var services = new ServiceCollection().AddSingleton(memory).BuildServiceProvider();
        using var worker = new MemoryCaptureWorker(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<MemoryCaptureWorker>.Instance);
        await worker.StartAsync(default);
        try { Assert.Equal(1, await proxy.Started.Task.WaitAsync(TimeSpan.FromSeconds(5))); }
        finally { await worker.StopAsync(default); }
    }

    public class RecordingMemoryWorkerProxy : DispatchProxy
    {
        public TaskCompletionSource<int> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name != nameof(IAgentMemoryService.ProcessPendingAsync)) throw new NotSupportedException();
            Started.TrySetResult((int)args![0]!);
            return Task.FromResult(0);
        }
    }
}
