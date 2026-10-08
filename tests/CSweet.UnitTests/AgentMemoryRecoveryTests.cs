using System.Text.Json;
using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Auth;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Setup;
using CSweet.Application.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using CSweet.Infrastructure.Persistence.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [Fact]
    public async Task RecoveryCannotTargetAJobFromAnotherEmployeeOrOrganization()
    {
        await using var fixture = await DurabilityFixture.CreateAsync();
        var (actorId, _) = await SeedRecoveryAsync(fixture);
        await using var db = fixture.Context();
        var otherOrganization = new Organization { Id = Guid.NewGuid(), Name = "Other" };
        var otherEmployee = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = otherOrganization.Id, EmployeeType = EmployeeType.Agent };
        var otherConversation = new Conversation { Id = Guid.NewGuid(), OrganizationId = otherOrganization.Id,
            AgentOrganizationUserId = otherEmployee.Id, InitiatedByOrganizationUserId = otherEmployee.Id };
        var otherMessage = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = otherConversation.Id, Content = "private-memory" };
        var otherJob = new MemoryCaptureOutboxItem { Id = Guid.NewGuid(), ConversationMessageId = otherMessage.Id, Status = MemoryCaptureStatus.Failed };
        db.AddRange(otherOrganization, otherEmployee, otherConversation, otherMessage, otherJob);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => new AgentMemoryRecoveryService(db, TimeProvider.System)
            .RetryAsync(fixture.OrganizationId, fixture.EmployeeId, otherJob.Id, actorId, new(Guid.NewGuid(), 0)));
        Assert.Empty(await db.MemoryCaptureRetryReceipts.ToListAsync());
        Assert.Equal(MemoryCaptureStatus.Failed, (await db.MemoryCaptureOutbox.SingleAsync(x => x.Id == otherJob.Id)).Status);
    }

    [Theory]
    [InlineData("lease")]
    [InlineData("completed")]
    [InlineData("enriched")]
    public async Task RecoveryRejectsInconsistentTerminalState(string marker)
    {
        await using var fixture = await DurabilityFixture.CreateAsync();
        var (actorId, jobId) = await SeedRecoveryAsync(fixture);
        await using var db = fixture.Context();
        var job = await db.MemoryCaptureOutbox.SingleAsync();
        if (marker == "lease") job.LeaseToken = Guid.NewGuid();
        if (marker == "completed") job.CompletedAt = DateTimeOffset.UtcNow;
        if (marker == "enriched") job.EnrichedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => new AgentMemoryRecoveryService(db, TimeProvider.System)
            .RetryAsync(fixture.OrganizationId, fixture.EmployeeId, jobId, actorId, new(Guid.NewGuid(), 0)));
        Assert.Empty(await db.MemoryCaptureRetryReceipts.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task RecoveryMigrationPreservesAcceptedWorkAndReceiptsAreAppendOnly()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actorId, jobId) = await SeedRecoveryAsync(fixture);
        await using var db = fixture.Context();
        var migration = new MemoryEnrichmentRecovery();
        var generator = db.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate(migration.DownOperations))
            await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in generator.Generate(migration.UpOperations))
            await db.Database.ExecuteSqlRawAsync(command.CommandText);
        var original = await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync();
        Assert.Equal(0, original.RetryGeneration);
        Assert.NotNull(original.AcceptedExtractionJson);
        await new AgentMemoryRecoveryService(db, TimeProvider.System).RetryAsync(fixture.OrganizationId,
            fixture.EmployeeId, jobId, actorId, new(Guid.NewGuid(), 0));
        Assert.Equal(original.AcceptedExtractionJson, (await db.MemoryCaptureOutbox.SingleAsync()).AcceptedExtractionJson);
        var receipt = await db.MemoryCaptureRetryReceipts.SingleAsync();
        receipt.PreviousAttempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.DoesNotContain(OrganizationDataPurgeService.ScopedEntityTypes(db.Model), x => x.ClrType == typeof(MemoryCaptureRetryReceipt));
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("archived")]
    [InlineData("agent")]
    [InlineData("unrelated")]
    [InlineData("foreign")]
    [InlineData("cycle")]
    [InlineData("archived_manager")]
    public async Task RecoveryRequiresCurrentHumanAuthority(string denial)
    {
        await using var fixture = await DurabilityFixture.CreateAsync();
        var (actorId, jobId) = await SeedRecoveryAsync(fixture);
        await using var db = fixture.Context();
        var actor = await db.CoreOrganizationUsers.SingleAsync(x => x.Id == fixture.HumanId);
        var employee = await db.CoreOrganizationUsers.SingleAsync(x => x.Id == fixture.EmployeeId);
        switch (denial)
        {
            case "inactive": actor.IsActive = false; break;
            case "archived": actor.ArchivedAt = DateTimeOffset.UtcNow; break;
            case "agent": actor.EmployeeType = EmployeeType.Agent; break;
            case "unrelated": employee.ReportsToOrganizationUserId = null; break;
            case "foreign": actor.OrganizationId = Guid.NewGuid(); break;
            case "cycle": actor.ReportsToOrganizationUserId = employee.Id; break;
            case "archived_manager":
                var manager = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
                    ReportsToOrganizationUserId = actor.Id, ArchivedAt = DateTimeOffset.UtcNow };
                db.CoreOrganizationUsers.Add(manager);
                employee.ReportsToOrganizationUserId = manager.Id;
                break;
        }
        await db.SaveChangesAsync();
        var service = new AgentMemoryRecoveryService(db, TimeProvider.System);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListFailuresAsync(fixture.OrganizationId, fixture.EmployeeId, actorId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RetryAsync(fixture.OrganizationId, fixture.EmployeeId,
            jobId, actorId, new(Guid.NewGuid(), 0)));
        Assert.Empty(await db.MemoryCaptureRetryReceipts.ToListAsync());
    }

    [Theory]
    [InlineData(MemoryCaptureStatus.Pending)]
    [InlineData(MemoryCaptureStatus.Processing)]
    [InlineData(MemoryCaptureStatus.Completed)]
    public async Task RecoveryNeverResetsJobsOutsideTerminalFailure(MemoryCaptureStatus status)
    {
        await using var fixture = await DurabilityFixture.CreateAsync();
        var (actorId, jobId) = await SeedRecoveryAsync(fixture);
        await using var db = fixture.Context();
        var job = await db.MemoryCaptureOutbox.SingleAsync();
        job.Status = status;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => new AgentMemoryRecoveryService(db, TimeProvider.System)
            .RetryAsync(fixture.OrganizationId, fixture.EmployeeId, jobId, actorId, new(Guid.NewGuid(), 0)));
        Assert.Equal(status, (await db.MemoryCaptureOutbox.SingleAsync()).Status);
        Assert.Empty(await db.MemoryCaptureRetryReceipts.ToListAsync());
    }

    [Theory]
    [InlineData("participant")]
    [InlineData("installation")]
    public async Task RecoveryRejectsUnavailableSource(string disabled)
    {
        await using var fixture = await DurabilityFixture.CreateAsync();
        var (actorId, jobId) = await SeedRecoveryAsync(fixture);
        await using var db = fixture.Context();
        if (disabled == "installation") (await db.AgentInstallations.SingleAsync()).IsEnabled = false;
        else
        {
            // Keep the manager active while disabling a different source participant.
            var participant = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, IsActive = false };
            db.CoreOrganizationUsers.Add(participant);
            (await db.CoreConversations.SingleAsync()).InitiatedByOrganizationUserId = participant.Id;
        }
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AgentMemoryRecoveryService(db, TimeProvider.System)
            .RetryAsync(fixture.OrganizationId, fixture.EmployeeId, jobId, actorId, new(Guid.NewGuid(), 0)));
        Assert.Equal(MemoryCaptureStatus.Failed, (await db.MemoryCaptureOutbox.SingleAsync()).Status);
    }

    [MemoryPostgresFact]
    public async Task RecoveryReplaysReceiptPreservesAcceptedOutputAndDeliversOneAuditedAction()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actorId, jobId) = await SeedRecoveryAsync(fixture);
        await using var db = fixture.Context();
        var service = new AgentMemoryRecoveryService(db, TimeProvider.System);
        var request = new RetryMemoryEnrichmentRequest(Guid.NewGuid(), 0);
        var before = await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync();
        var response = await service.RetryAsync(fixture.OrganizationId, fixture.EmployeeId, jobId, actorId, request);
        Assert.False(response.Replayed);
        Assert.Equal("Pending", response.Job.Status);
        Assert.Equal(1, response.Job.RetryGeneration);
        var item = await db.MemoryCaptureOutbox.SingleAsync();
        Assert.Equal(before.AcceptedExtractionJson, item.AcceptedExtractionJson);
        Assert.Equal(before.ExtractionAcceptedAt, item.ExtractionAcceptedAt);
        Assert.Equal(0, item.Attempts);
        var receipt = Assert.Single(await db.MemoryCaptureRetryReceipts.ToListAsync());
        Assert.Equal(actorId, receipt.ActorApplicationUserId);
        Assert.Equal(fixture.HumanId, receipt.ActorOrganizationUserId);
        Assert.Equal(10, receipt.PreviousAttempts);
        Assert.True(receipt.ReusesAcceptedExtraction);
        Assert.True((await service.RetryAsync(fixture.OrganizationId, fixture.EmployeeId, jobId, actorId, request)).Replayed);

        // A lost-response replay after another failed processing cycle must not grant ten more attempts.
        item.Status = MemoryCaptureStatus.Failed;
        item.Attempts = 10;
        await db.SaveChangesAsync();
        Assert.True((await service.RetryAsync(fixture.OrganizationId, fixture.EmployeeId, jobId, actorId, request)).Replayed);
        Assert.Equal(10, item.Attempts);
        Assert.Equal(MemoryCaptureStatus.Failed, item.Status);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.RetryAsync(fixture.OrganizationId,
            fixture.EmployeeId, jobId, actorId, new(Guid.NewGuid(), 0)));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.RetryAsync(fixture.OrganizationId,
            fixture.EmployeeId, jobId, actorId, new(request.OperationId, 1)));
        Assert.Single(await db.MemoryCaptureRetryReceipts.ToListAsync());

        await using var services = new ServiceCollection().AddScoped(_ => fixture.Context()).BuildServiceProvider();
        var writer = new AuditEventWriter(services.GetRequiredService<IServiceScopeFactory>(), new AuditExecutionContextAccessor(), new EphemeralDataProtectionProvider());
        var dispatcher = new AuditOutboxDispatcher(db, writer, TimeProvider.System);
        await dispatcher.DispatchAsync(default);
        await dispatcher.DispatchAsync(default);
        var audit = Assert.Single(await db.AuditEvents.Where(x => x.EventType == "memory.enrichment.retry-requested.v1").ToListAsync());
        Assert.Equal(receipt.Id, audit.Id);
        Assert.Equal(actorId, audit.ActorApplicationUserId);
        Assert.Equal(fixture.HumanId, audit.ActorOrganizationUserId);
        Assert.DoesNotContain("private-memory", JsonSerializer.Serialize(audit));
        Assert.Contains(await db.ApplicationRealtimeOutbox.ToListAsync(), x => x.Subject == $"audit/{receipt.Id:D}");
        // Replayed receipts still require current authorization.
        (await db.CoreOrganizationUsers.SingleAsync(x => x.Id == fixture.HumanId)).IsActive = false;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RetryAsync(fixture.OrganizationId, fixture.EmployeeId, jobId, actorId, request));
    }

    [MemoryPostgresFact]
    public async Task RecoveryRollsBackIfAuditEvidenceCannotBePersisted()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actorId, jobId) = await SeedRecoveryAsync(fixture);
        await using var db = fixture.Context();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_memory_retry_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected audit failure'; END $$;
            CREATE TRIGGER reject_memory_retry_audit BEFORE INSERT ON "ComputeAuditOutbox"
                FOR EACH ROW EXECUTE FUNCTION reject_memory_retry_audit();
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => new AgentMemoryRecoveryService(db, TimeProvider.System)
            .RetryAsync(fixture.OrganizationId, fixture.EmployeeId, jobId, actorId, new(Guid.NewGuid(), 0)));
        var item = await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync();
        Assert.Equal(MemoryCaptureStatus.Failed, item.Status);
        Assert.Equal(10, item.Attempts);
        Assert.Equal(0, item.RetryGeneration);
        Assert.NotNull(item.AcceptedExtractionJson);
        Assert.Empty(await db.MemoryCaptureRetryReceipts.ToListAsync());
        Assert.Empty(await db.AuditOutbox.Where(x => x.SourceEntityType == nameof(MemoryCaptureOutboxItem)).ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task ConcurrentRecoveryRequestsProduceOneRetryAndOneAuditReceipt()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actorId, jobId) = await SeedRecoveryAsync(fixture);
        async Task<bool> RetryAsync()
        {
            await using var context = fixture.Context();
            try
            {
                await new AgentMemoryRecoveryService(context, TimeProvider.System).RetryAsync(fixture.OrganizationId,
                    fixture.EmployeeId, jobId, actorId, new(Guid.NewGuid(), 0));
                return true;
            }
            catch (DbUpdateConcurrencyException) { return false; }
        }
        var outcomes = await Task.WhenAll(RetryAsync(), RetryAsync());
        Assert.Single(outcomes, x => x);
        await using var db = fixture.Context();
        Assert.Single(await db.MemoryCaptureRetryReceipts.ToListAsync());
        Assert.Single(await db.AuditOutbox.Where(x => x.SourceEntityType == nameof(MemoryCaptureOutboxItem)).ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task FailureListingIsBoundedScopedAndContainsNoSourceOrLegacyErrorText()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actorId, _) = await SeedRecoveryAsync(fixture);
        await using var db = fixture.Context();
        var original = await db.CoreConversationMessages.SingleAsync();
        for (var i = 0; i < 3; i++)
        {
            var message = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = original.ConversationId,
                Content = "private-memory", Role = ConversationRole.User, CreatedAt = original.CreatedAt };
            db.CoreConversationMessages.Add(message);
            db.MemoryCaptureOutbox.Add(new MemoryCaptureOutboxItem { Id = Guid.NewGuid(), ConversationMessageId = message.Id,
                Status = i == 2 ? MemoryCaptureStatus.Completed : MemoryCaptureStatus.Failed, CreatedAt = original.CreatedAt,
                NextAttemptAt = original.CreatedAt, LastError = "private-memory-provider-error" });
        }
        await db.SaveChangesAsync();
        var service = new AgentMemoryRecoveryService(db, TimeProvider.System);
        var page = await service.ListFailuresAsync(fixture.OrganizationId, fixture.EmployeeId, actorId, limit: 1);
        var ids = new List<Guid>();
        while (true)
        {
            ids.Add(Assert.Single(page.Items).Id);
            Assert.DoesNotContain("private-memory", JsonSerializer.Serialize(page));
            if (page.NextCursor is null) break;
            page = await service.ListFailuresAsync(fixture.OrganizationId, fixture.EmployeeId, actorId, page.NextCursor, 1);
        }
        Assert.Equal(3, ids.Distinct().Count());
        await Assert.ThrowsAsync<ArgumentException>(() => service.ListFailuresAsync(fixture.OrganizationId, fixture.EmployeeId, actorId, "invalid"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListFailuresAsync(Guid.NewGuid(), fixture.EmployeeId, actorId));
    }

    private static async Task<(Guid ActorId, Guid JobId)> SeedRecoveryAsync(DurabilityFixture fixture)
    {
        await using var db = fixture.Context();
        var applicationUser = new ApplicationUser { Id = Guid.NewGuid(), UserName = "memory-manager", CreatedAt = DateTimeOffset.UtcNow };
        db.Users.Add(applicationUser);
        (await db.CoreOrganizationUsers.SingleAsync(x => x.Id == fixture.HumanId)).ApplicationUserId = applicationUser.Id;
        (await db.CoreOrganizationUsers.SingleAsync(x => x.Id == fixture.EmployeeId)).ReportsToOrganizationUserId = fixture.HumanId;
        var item = await db.MemoryCaptureOutbox.SingleAsync();
        item.Status = MemoryCaptureStatus.Failed;
        item.Attempts = 10;
        item.LastError = "private-memory-provider-error";
        item.AcceptedExtractionJson = "{\"evidence\":\"private-memory\"}";
        item.ExtractionAcceptedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return (applicationUser.Id, item.Id);
    }
}
