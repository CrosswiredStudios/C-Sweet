using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Infrastructure.Compute;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CSweet.UnitTests;

public sealed class ComputeAuditTests
{
    [Fact]
    public async Task Admission_evidence_contains_authority_and_omits_caller_keys()
    {
        await using var fixture = new ComputeBrokerTests.Fixture(); await fixture.SeedAsync();
        var environment = await fixture.Send(fixture.Request with { IdempotencyKey = "sensitive-caller-key" });
        var row = await fixture.Db.ComputeAuditOutbox.SingleAsync();
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
        Assert.Equal(1, await fixture.Db.ComputeAuditOutbox.CountAsync());
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
        Assert.Empty(await fixture.Db.ComputeAuditOutbox.ToListAsync());
    }

    [Fact]
    public async Task Retry_after_ledger_commit_preserves_one_sealed_record_and_rejects_changed_evidence()
    {
        await using var fixture = new ComputeBrokerTests.Fixture(); await fixture.SeedAsync(); await fixture.Send();
        var services = new ServiceCollection();
        services.AddScoped(_ => new CSweetDbContext(fixture.Options));
        await using var provider = services.BuildServiceProvider();
        var protection = new EphemeralDataProtectionProvider();
        var writer = new AuditEventWriter(provider.GetRequiredService<IServiceScopeFactory>(), new AuditExecutionContextAccessor(), protection);
        var interrupted = new FailAfterCommit(writer);
        await Assert.ThrowsAsync<IOException>(() => new ComputeAuditDispatcher(fixture.Db, interrupted, new ComputeBrokerTests.Clock()).DispatchAsync(default));
        Assert.Null((await fixture.Db.ComputeAuditOutbox.SingleAsync()).DeliveredAt);
        Assert.Equal(1, await fixture.Db.AuditEvents.CountAsync());
        await using var restarted = new CSweetDbContext(fixture.Options);
        Assert.Equal(1, await new ComputeAuditDispatcher(restarted, writer, new ComputeBrokerTests.Clock()).DispatchAsync(default));
        Assert.Equal(0, await new ComputeAuditDispatcher(restarted, writer, new ComputeBrokerTests.Clock()).DispatchAsync(default));
        var entry = await restarted.AuditEvents.SingleAsync();
        Assert.Equal(entry.RecordHash, AuditIntegrity.ComputeRecordHash(entry));
        Assert.Equal(entry.RecordHash, protection.CreateProtector("CSweet.SecurityAuditLedger.v1").Unprotect(entry.IntegritySeal!));
        var original = JsonSerializer.Deserialize<AuditEventWriteRequest>((await restarted.ComputeAuditOutbox.SingleAsync()).RequestJson)!;
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
}
