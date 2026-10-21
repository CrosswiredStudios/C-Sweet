using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Realtime;
using CSweet.Infrastructure.Compute;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed class ComputeAuditTests
{
    [Fact]
    public async Task FullAuditBatchesDrainWithoutAnIdleDelayBetweenEveryHundredReceipts()
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var writer = new CountingWriter();
        await using var services = new ServiceCollection()
            .AddScoped(_ => new CSweetDbContext(options))
            .AddSingleton<IAuditEventWriter>(writer).AddSingleton(TimeProvider.System)
            .AddScoped<AuditOutboxDispatcher>().BuildServiceProvider();
        await using (var db = new CSweetDbContext(options))
        {
            for (var i = 0; i < 205; i++) db.QueueAudit(new("test.audit"));
            await db.SaveChangesAsync();
        }
        var worker = new AuditOutboxWorker(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<AuditOutboxWorker>.Instance);
        await worker.DrainAsync(default);
        await using var verify = new CSweetDbContext(options);
        Assert.Equal(205, writer.Count);
        Assert.Equal(205, await verify.AuditOutbox.CountAsync(x => x.DeliveredAt != null));
    }

    [Fact]
    public async Task ActivityWakesCommitWithSourceReceiptsAndCoalesceStreamBurstsWithoutPayloads()
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var protection = new EphemeralDataProtectionProvider();
        var org = Guid.NewGuid(); var employee = Guid.NewGuid(); var run = Guid.NewGuid();
        await using var db = new CSweetDbContext(options, protection);
        for (var i = 0; i < 10; i++)
            db.QueueAudit(new("model.response.chunk", OrganizationId: org, EntityType: "AgentRunLog", EntityId: run,
                Employees: [new(employee)], ContentType: "application/json", Payload: JsonSerializer.SerializeToUtf8Bytes(new { text = "private response" })));
        db.QueueAudit(new("model.call.completed", OrganizationId: org, EntityType: "AgentRunLog", EntityId: run, Employees: [new(employee)]));
        db.QueueAudit(new("model.response.chunk", OrganizationId: Guid.NewGuid(), EntityType: "AgentRunLog", EntityId: Guid.NewGuid()));
        await db.SaveChangesAsync();
        await using var verify = new CSweetDbContext(options, protection);
        Assert.Equal(12, await verify.AuditOutbox.CountAsync(x => x.DeliveredAt == null));
        var wakes = await verify.ApplicationRealtimeOutbox.Where(x => x.EventType == AppRealtimeEvents.CurrentActivityChanged).ToListAsync();
        Assert.Equal(2, wakes.Count); // First chunk plus completion, regardless of stream volume.
        Assert.All(wakes, wake =>
        {
            Assert.Equal(org, wake.OrganizationId);
            Assert.DoesNotContain("private response", wake.DataJson);
            Assert.Contains(employee.ToString(), wake.DataJson);
        });
        Assert.Empty(await verify.AuditEvents.ToListAsync()); // Hints do not wait for sealed ledger delivery.
    }

    [Fact]
    public async Task Admission_evidence_contains_authority_and_omits_caller_keys()
    {
        await using var fixture = new ComputeBrokerTests.Fixture(); await fixture.SeedAsync();
        var environment = await fixture.Send(fixture.Request with { IdempotencyKey = "sensitive-caller-key" });
        var row = await fixture.Db.AuditOutbox.Where(x => x.SourceEntityType == null).SingleAsync(x => x.SourceEntityType == null);
        var request = JsonSerializer.Deserialize<AuditEventWriteRequest>(row.RequestJson)!;
        Assert.Equal(row.Id, request.EventId);
        Assert.Equal(environment.Id, request.EntityId);
        Assert.Equal(fixture.Organization, request.OrganizationId);
        Assert.Equal(fixture.Installation, request.Actor!.InstallationId);
        Assert.Equal("Accepted", request.Outcome);
        Assert.Contains("GrantId", request.MetadataJson);
        Assert.Contains("Revision", request.MetadataJson);
        Assert.DoesNotContain("sensitive-caller-key", row.RequestJson);
        await fixture.Send(fixture.Request with { IdempotencyKey = "sensitive-caller-key" });
        Assert.Equal(1, await fixture.Db.AuditOutbox.Where(x => x.SourceEntityType == null).CountAsync(x => x.SourceEntityType == null));
    }

    [Fact]
    public async Task Rejected_attempt_is_attributed_without_claiming_verified_identity_or_leaking_inputs()
    {
        await using var fixture = new ComputeBrokerTests.Fixture(); await fixture.SeedAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Send(fixture.Request with { IdempotencyKey = "\nsecret" }));
        var rejected = Assert.Single(fixture.Ledger.Events);
        Assert.Equal("Rejected", rejected.Outcome);
        Assert.Equal(fixture.Organization, rejected.OrganizationId);
        Assert.Equal(fixture.Installation, rejected.Actor!.InstallationId);
        Assert.False(rejected.Actor.IdentityVerified);
        Assert.Null(rejected.MetadataJson);
        Assert.Null(rejected.ErrorMessage);
        Assert.Empty(await fixture.Db.AuditOutbox.Where(x => x.SourceEntityType == null).ToListAsync());
    }

    [Fact]
    public async Task Retry_after_ledger_commit_preserves_one_sealed_record_and_rejects_changed_evidence()
    {
        await using var fixture = new ComputeBrokerTests.Fixture(); await fixture.SeedAsync(); await fixture.Send();
        // This test isolates the compute receipt from other platform events in the shared queue.
        fixture.Db.AuditOutbox.RemoveRange(await fixture.Db.AuditOutbox.Where(x => x.SourceEntityType != null).ToListAsync());
        await fixture.Db.SaveChangesAsync();
        var services = new ServiceCollection();
        services.AddScoped(_ => new CSweetDbContext(fixture.Options));
        await using var provider = services.BuildServiceProvider();
        var protection = new EphemeralDataProtectionProvider();
        var writer = new AuditEventWriter(provider.GetRequiredService<IServiceScopeFactory>(), new AuditExecutionContextAccessor(), protection);
        var interrupted = new FailAfterCommit(writer);
        Assert.Equal(1, await new AuditOutboxDispatcher(fixture.Db, interrupted, new ComputeBrokerTests.Clock()).DispatchAsync(default));
        Assert.Equal(1, (await fixture.Db.AuditOutbox.SingleAsync()).Attempts);
        Assert.Null((await fixture.Db.AuditOutbox.Where(x => x.SourceEntityType == null).SingleAsync(x => x.SourceEntityType == null)).DeliveredAt);
        Assert.Equal(1, await fixture.Db.AuditEvents.CountAsync());
        await using var restarted = new CSweetDbContext(fixture.Options);
        (await restarted.AuditOutbox.SingleAsync()).NextAttemptAt = null;
        await restarted.SaveChangesAsync();
        Assert.Equal(1, await new AuditOutboxDispatcher(restarted, writer, new ComputeBrokerTests.Clock()).DispatchAsync(default));
        Assert.Equal(0, await new AuditOutboxDispatcher(restarted, writer, new ComputeBrokerTests.Clock()).DispatchAsync(default));
        var entry = await restarted.AuditEvents.SingleAsync();
        Assert.Equal(entry.RecordHash, AuditIntegrity.ComputeRecordHash(entry));
        Assert.Equal(entry.RecordHash, protection.CreateProtector("CSweet.SecurityAuditLedger.v1").Unprotect(entry.IntegritySeal!));
        var original = JsonSerializer.Deserialize<AuditEventWriteRequest>((await restarted.AuditOutbox.Where(x => x.SourceEntityType == null).SingleAsync()).RequestJson)!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.AppendAsync(original with { Summary = "different evidence" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.AppendAsync(original with { Actor = original.Actor! with { InstallationId = Guid.NewGuid() } }));
        Assert.Equal(1, await restarted.AuditEvents.CountAsync());
    }

    private sealed class FailAfterCommit(IAuditEventWriter inner) : IAuditEventWriter
    {
        public Task WriteAsync(string eventType, string entityType, Guid? entityId, string? summary, string? metadataJson = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task<Guid> AppendAsync(AuditEventWriteRequest request, CancellationToken cancellationToken = default)
        {
            await inner.AppendAsync(request, cancellationToken);
            throw new IOException("Simulated lost acknowledgement after ledger commit.");
        }
    }

    private sealed class CountingWriter : IAuditEventWriter
    {
        public int Count { get; private set; }
        public Task WriteAsync(string eventType, string entityType, Guid? entityId, string? summary, string? metadataJson = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid> AppendAsync(AuditEventWriteRequest request, CancellationToken cancellationToken = default)
        { Count++; return Task.FromResult(request.EventId!.Value); }
    }
}
