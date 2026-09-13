using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Compute.Contracts;
using CSweet.Infrastructure.Compute;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CSweet.UnitTests;

public sealed class ComputeAccessAuditTests
{
    [Fact]
    public async Task Read_and_list_commit_sealed_evidence_with_current_authority_and_bounded_result_identity()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync(); var environment = await f.Send();
        var services = new ServiceCollection(); services.AddScoped(_ => new CSweetDbContext(f.Options));
        await using var provider = services.BuildServiceProvider();
        var protection = new EphemeralDataProtectionProvider();
        var writer = new AuditEventWriter(provider.GetRequiredService<IServiceScopeFactory>(), new AuditExecutionContextAccessor(), protection);
        var broker = new ComputeBroker(f.Db, f.Templates, new ComputeBrokerTests.Clock(), writer);
        Assert.Equal(environment.Id, (await broker.ReadAsync(f.Organization, f.Installation, environment.Id, default)).Id);
        Assert.Single((await broker.ListAsync(f.Organization, f.Installation, f.Workstream, null, 10, default)).Items);
        var records = await f.Db.AuditEvents.ToListAsync(); Assert.Equal(2, records.Count);
        foreach (var record in records)
        {
            Assert.Equal("compute.access.v1", record.EventType);
            Assert.Equal(record.RecordHash, AuditIntegrity.ComputeRecordHash(record));
            Assert.Equal(record.RecordHash, protection.CreateProtector("CSweet.SecurityAuditLedger.v1").Unprotect(record.IntegritySeal!));
            using var metadata = JsonDocument.Parse(record.MetadataJson!);
            var action = metadata.RootElement.GetProperty("action").GetString();
            Assert.Contains(action, new[] { InfrastructureActions.Read, InfrastructureActions.List });
            var grant = Assert.Single(metadata.RootElement.GetProperty("grants").EnumerateArray());
            Assert.Equal(action, grant.GetProperty("Action").GetString());
            Assert.Equal((await f.Db.ScopedActionGrants.SingleAsync(x => x.Action == action)).Id, grant.GetProperty("GrantId").GetGuid());
            var item = Assert.Single(metadata.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(environment.Id, item.GetProperty("Id").GetGuid());
            Assert.DoesNotContain("test-provider", record.MetadataJson);
            Assert.DoesNotContain(f.Request.IdempotencyKey, record.MetadataJson);
        }
        Assert.Equal(environment.Revision, (await f.Db.ComputeEnvironments.SingleAsync()).Revision);
    }

    [Fact]
    public async Task Rejected_cross_scope_read_records_unverified_attempt_without_target_details()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync(); var environment = await f.Send();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Broker.ReadAsync(Guid.NewGuid(), f.Installation, environment.Id, default));
        var rejected = Assert.Single(f.Ledger.Events);
        Assert.Equal("compute.access.rejected.v1", rejected.EventType);
        Assert.Null(rejected.OrganizationId); Assert.Null(rejected.EntityId);
        Assert.False(rejected.Actor!.IdentityVerified); Assert.False(rejected.UseAmbientOrganization);
        Assert.DoesNotContain(environment.Id.ToString(), rejected.MetadataJson);
        Assert.Null(rejected.ErrorMessage);
    }

    [Fact]
    public async Task Ledger_failure_prevents_both_read_and_list_responses_without_changing_compute_state()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync(); var environment = await f.Send();
        var broker = new ComputeBroker(f.Db, f.Templates, new ComputeBrokerTests.Clock(), new UnavailableLedger());
        await Assert.ThrowsAsync<IOException>(() => broker.ReadAsync(f.Organization, f.Installation, environment.Id, default));
        await Assert.ThrowsAsync<IOException>(() => broker.ListAsync(f.Organization, f.Installation, f.Workstream, null, 10, default));
        Assert.Equal(environment.Revision, (await f.Db.ComputeEnvironments.SingleAsync()).Revision);
        Assert.Single(await f.Db.ComputeOperations.ToListAsync());
    }

    [Fact]
    public async Task Invalid_pagination_and_revoked_read_grant_are_audited_as_rejections()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync(); var environment = await f.Send();
        await Assert.ThrowsAsync<ArgumentException>(() => f.Broker.ListAsync(f.Organization, f.Installation, f.Workstream, null, 101, default));
        (await f.Db.ScopedActionGrants.SingleAsync(x => x.Action == InfrastructureActions.Read)).RevokedAt = DateTimeOffset.UtcNow;
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Broker.ReadAsync(f.Organization, f.Installation, environment.Id, default));
        Assert.Equal(new[] { "request_rejected", "authority_denied" }, f.Ledger.Events.Select(x => x.ErrorCode));
    }

    private sealed class UnavailableLedger : IAuditEventWriter
    {
        public Task WriteAsync(string eventType, string entityType, Guid? entityId, string? summary, string? metadataJson = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid> AppendAsync(AuditEventWriteRequest request, CancellationToken cancellationToken = default) => throw new IOException("Ledger unavailable.");
    }
}
