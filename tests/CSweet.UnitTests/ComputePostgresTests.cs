using CSweet.Compute.Contracts;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using CSweet.Application.Compute;
using CSweet.Infrastructure.Compute;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace CSweet.UnitTests;

// Explicitly opt in with a disposable, loopback PostgreSQL server. Each test creates
// and removes its own database; no existing database is migrated or cleared.
public sealed class ComputePostgresTests
{
    [PostgresTheory]
    [InlineData(0)]
    [InlineData(600)]
    public async Task Lifetime_roundtrips_without_until_release_becoming_due_for_cleanup(int lifetime)
    {
        await using var database = new Database(); await database.CreateAsync();
        await using var f = new ComputeBrokerTests.Fixture(database.Options()); await f.SeedAsync();
        if (lifetime == 0)
        {
            foreach (var grant in f.Db.ScopedActionGrants)
            {
                var constraints = JsonSerializer.Deserialize<ComputeGrantConstraints>(grant.ConstraintsJson, ComputeProtocol.Json)!;
                grant.ConstraintsJson = JsonSerializer.Serialize(constraints with { MaximumLifetimeSeconds = 0 }, ComputeProtocol.Json);
                grant.ExpiresAt = DateTimeOffset.MaxValue;
            }
            await f.Db.SaveChangesAsync();
        }
        var request = f.Request with { Specification = f.Request.Specification with { LifetimeSeconds = lifetime } };
        var result = await f.Broker.RequestAsync(f.Organization, f.Installation, request, default);
        f.Db.ChangeTracker.Clear();
        var stored = await f.Db.ComputeEnvironments.SingleAsync();
        Assert.Equal(result.LeaseExpiresAt, stored.LeaseExpiresAt);
        var future = new ComputeBrokerTests.Clock().GetUtcNow().AddYears(1);
        Assert.Equal(lifetime == 0 ? 0 : 1, await f.Db.ComputeEnvironments.CountAsync(x => x.LeaseExpiresAt <= future));
        var released = await f.Broker.ChangeLifecycleAsync(f.Organization, f.Installation,
            new(stored.Id, stored.Generation, InfrastructureActions.Destroy, "release"), default);
        Assert.Equal(CSweet.Domain.Compute.ComputeDesiredState.Destroyed, released.DesiredState);
    }
    private sealed class PostgresTheoryAttribute : TheoryAttribute
    {
        public PostgresTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE")))
                Skip = "Set CSWEET_COMPUTE_TEST_DATABASE to a disposable loopback PostgreSQL server.";
        }
    }

    private sealed class Database : IAsyncDisposable
    {
        private readonly string name = "compute_test_" + Guid.NewGuid().ToString("N");
        private readonly NpgsqlConnectionStringBuilder connection = new(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE"));
        public DbContextOptions<CSweetDbContext> Options(params IInterceptor[] interceptors) =>
            new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(connection.ConnectionString).AddInterceptors(interceptors).Options;

        public async Task CreateAsync()
        {
            if (connection.Host is not ("127.0.0.1" or "localhost" or "::1"))
                throw new InvalidOperationException("Compute tests require a disposable loopback PostgreSQL server.");
            connection.Database = name;
            connection.Pooling = false;
            await using var db = new CSweetDbContext(Options());
            await db.Database.EnsureCreatedAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (connection.Database != name) return;
            await using var db = new CSweetDbContext(Options());
            await db.Database.EnsureDeletedAsync();
        }
    }

    private sealed class GateCatalog : IComputeTemplateCatalog
    {
        private int arrivals;
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<RegisteredComputeTemplate?> ResolveAsync(Guid organizationId, string templateId, CancellationToken token)
        {
            if (Interlocked.Increment(ref arrivals) == 2) gate.TrySetResult();
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
            return await new ComputeBrokerTests.Catalog().ResolveAsync(organizationId, templateId, token);
        }
    }

    [PostgresTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Concurrent_triggers_retry_to_one_reservation(bool differentEnvironment, bool existingAdmission)
    {
        await using var database = new Database(); await database.CreateAsync();
        await using var fixture = new ComputeBrokerTests.Fixture(database.Options()); await fixture.SeedAsync();
        if (existingAdmission)
        {
            fixture.Db.ComputeAdmissions.Add(new() { OrganizationId = fixture.Organization, InstallationId = fixture.Installation });
            await fixture.Db.SaveChangesAsync();
            fixture.Db.ChangeTracker.Clear();
        }
        var catalog = new GateCatalog();
        var conflicts = 0;
        async Task<ComputeEnvironmentView?> Send(RequestComputeEnvironment request)
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                await using var db = new CSweetDbContext(database.Options());
                try
                {
                    return await new ComputeBroker(db, catalog, new ComputeBrokerTests.Clock(), fixture.Ledger)
                        .RequestAsync(fixture.Organization, fixture.Installation, request, default);
                }
                catch (Exception error) when (IsConflict(error)) { Interlocked.Increment(ref conflicts); }
                catch (UnauthorizedAccessException) when (differentEnvironment) { return null; }
            }
            throw new InvalidOperationException("Admission did not converge within four attempts.");
        }
        var second = fixture.Request with { IdempotencyKey = "heartbeat",
            DesiredEnvironmentKey = differentEnvironment ? "another-environment" : fixture.Request.DesiredEnvironmentKey };
        var results = await Task.WhenAll(Send(fixture.Request), Send(second));
        Assert.True(conflicts > 0, "Both requests must overlap before the first commit.");
        if (differentEnvironment) Assert.Single(results, x => x is not null);
        else Assert.Equal(results[0]!.Id, results[1]!.Id);
        await using var verify = new CSweetDbContext(database.Options());
        Assert.Equal(1, await verify.ComputeEnvironments.CountAsync());
        Assert.Equal(1, await verify.ComputeOperations.CountAsync());
        Assert.Equal(1, await verify.ComputeProviderWakes.CountAsync());
        Assert.Equal(differentEnvironment ? 1 : 2, await verify.ComputeRequestReceipts.CountAsync());
        Assert.Equal(1, await verify.AgentPlatformEventOutbox.CountAsync(x => x.EventType == "com.csweet.compute.changed.v1"));
    }

    [PostgresTheory]
    [InlineData(true)]
    public async Task Independent_ledger_writers_deliver_one_sealed_event(bool _)
    {
        await using var database = new Database(); await database.CreateAsync();
        await using var fixture = new ComputeBrokerTests.Fixture(database.Options()); await fixture.SeedAsync(); await fixture.Send();
        var row = await fixture.Db.AuditOutbox.Where(x => x.SourceEntityType == null).SingleAsync();
        var request = JsonSerializer.Deserialize<AuditEventWriteRequest>(row.RequestJson)!;
        var services = new ServiceCollection();
        services.AddScoped(_ => new CSweetDbContext(database.Options()));
        await using var provider = services.BuildServiceProvider();
        var protection = new EphemeralDataProtectionProvider();
        AuditEventWriter Writer() => new(provider.GetRequiredService<IServiceScopeFactory>(), new AuditExecutionContextAccessor(), protection);
        var results = await Task.WhenAll(Writer().AppendAsync(request), Writer().AppendAsync(request));
        Assert.Equal(results[0], results[1]);
        await using var verify = new CSweetDbContext(database.Options());
        var entry = await verify.AuditEvents.SingleAsync();
        Assert.Equal(row.Id, entry.Id);
        Assert.Equal(entry.RecordHash, AuditIntegrity.ComputeRecordHash(entry));
        Assert.Equal(entry.RecordHash, protection.CreateProtector("CSweet.SecurityAuditLedger.v1").Unprotect(entry.IntegritySeal!));
        Assert.True(await new AuditOutboxDispatcher(verify, Writer(), new ComputeBrokerTests.Clock()).DispatchAsync(default) >= 1);
        Assert.Equal(1, await verify.AuditEvents.CountAsync(x => x.Id == row.Id));
    }

    private sealed class SaveGate : SaveChangesInterceptor
    {
        private int arrivals;
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref arrivals) == 2) gate.TrySetResult();
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            return result;
        }
    }

    [PostgresTheory]
    [InlineData(true)]
    public async Task Concurrent_destroy_retries_converge_on_one_generation_and_operation(bool _)
    {
        await using var database = new Database(); await database.CreateAsync();
        await using var fixture = new ComputeBrokerTests.Fixture(database.Options()); await fixture.SeedAsync();
        var initial = await fixture.Send();
        var gate = new SaveGate();
        var conflicts = 0;
        async Task<ComputeEnvironmentView> Destroy()
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                await using var db = new CSweetDbContext(database.Options(gate));
                try
                {
                    return await new ComputeBroker(db, fixture.Templates, new ComputeBrokerTests.Clock(), fixture.Ledger)
                        .ChangeLifecycleAsync(fixture.Organization, fixture.Installation,
                            new(initial.Id, 1, InfrastructureActions.Destroy, "concurrent-destroy"), default);
                }
                catch (Exception error) when (IsConflict(error)) { Interlocked.Increment(ref conflicts); }
            }
            throw new InvalidOperationException("Lifecycle requests did not converge.");
        }
        var results = await Task.WhenAll(Destroy(), Destroy());
        Assert.True(conflicts > 0);
        Assert.Equal(results[0], results[1]);
        Assert.Equal(2, results[0].Generation);
        await using var verify = new CSweetDbContext(database.Options());
        Assert.Equal(1, await verify.ComputeOperations.CountAsync(x => x.Action == InfrastructureActions.Destroy));
        Assert.Equal(2, await verify.AuditOutbox.Where(x => x.SourceEntityType == null).CountAsync());
        Assert.Equal(2, await verify.AgentPlatformEventOutbox.CountAsync(x => x.EventType == "com.csweet.compute.changed.v1"));
        Assert.Null((await verify.ComputeEnvironments.SingleAsync()).TeardownConfirmedAt);
    }

    [PostgresTheory]
    [InlineData(true)]
    public async Task Result_rollback_preserves_receipt_state_audit_and_wake_for_safe_retry(bool _)
    {
        await using var database = new Database(); await database.CreateAsync();
        await using var fixture = new ComputeResultTests.Fixture(database.Options()); await fixture.InitializeAsync();
        var envelope = fixture.Sign();
        await using (var failing = new CSweetDbContext(database.Options(new FailAfterSave())))
        {
            var reconciler = new ComputeResultReconciler(failing, new(fixture.Enrollment, new ComputeBrokerTests.Clock()),
                new ComputeBrokerTests.Clock(), fixture.Broker.Ledger);
            await Assert.ThrowsAsync<InjectedFailure>(() => reconciler.ApplyAsync(envelope, default));
        }
        await using var verify = new CSweetDbContext(database.Options());
        Assert.Equal(CSweet.Domain.Compute.ComputeLifecycleState.Requested, (await verify.ComputeEnvironments.SingleAsync()).State);
        Assert.Equal(0, (await verify.ComputeOperations.SingleAsync()).LastResultSequence);
        Assert.Equal(1, await verify.AuditOutbox.Where(x => x.SourceEntityType == null).CountAsync());
        Assert.Equal(1, await verify.AgentPlatformEventOutbox.CountAsync(x => x.EventType == "com.csweet.compute.changed.v1"));
        Assert.True(await fixture.Reconciler.ApplyAsync(envelope, default));
        Assert.False(await fixture.Reconciler.ApplyAsync(envelope, default));
    }

    [PostgresTheory]
    [InlineData(true)]
    public async Task Registry_survives_restart_and_database_rejects_cross_organization_placement(bool _)
    {
        await using var database = new Database(); await database.CreateAsync();
        await using var fixture = new ComputeBrokerTests.Fixture(database.Options()); await fixture.SeedAsync();
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var registry = new ComputeRegistry(fixture.Db, new ComputeBrokerTests.Clock(), new AuditExecutionContextAccessor());
        var node = await registry.PutNodeAsync(fixture.Organization, Guid.NewGuid(), 0, "Test node", "hyper-v", "test-key",
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), true, default);
        var template = await registry.PutTemplateAsync(fixture.Organization,
            new("ubuntu-clean", "linux", "x64", "sha256:" + new string('a', 64), []), 0, default);
        await registry.PutPlacementAsync(fixture.Organization, template.Id, node.Id, 0, true, default);
        await using var restarted = new CSweetDbContext(database.Options());
        var recovered = new ComputeRegistry(restarted, new ComputeBrokerTests.Clock(), new AuditExecutionContextAccessor());
        Assert.Equal(node.Id, (await recovered.ResolveAsync(fixture.Organization, "ubuntu-clean", default))!.NodeId);
        Assert.NotNull(await recovered.ResolveAsync(fixture.Organization, node.Id, "test-key", default));
        var foreignOrg = Guid.NewGuid();
        restarted.CoreOrganizations.Add(new() { Id = foreignOrg, Name = "Other organization" });
        await restarted.SaveChangesAsync();
        var foreignNode = await recovered.PutNodeAsync(foreignOrg, Guid.NewGuid(), 0, "Other node", "kvm", "other-key",
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), true, default);
        restarted.ComputeTemplatePlacements.Add(new() { Id = Guid.NewGuid(), OrganizationId = fixture.Organization,
            TemplateRegistrationId = template.Id, NodeId = foreignNode.Id, Enabled = true });
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => restarted.SaveChangesAsync());
        Assert.Equal("23503", Assert.IsType<PostgresException>(failure.InnerException).SqlState);
        restarted.ChangeTracker.Clear();
        Assert.Equal(1, await restarted.ComputeTemplatePlacements.CountAsync());
    }

    [PostgresTheory]
    [InlineData(true)]
    public async Task Concurrent_dispatch_claims_return_one_executable_packet(bool _)
    {
        await using var database = new Database(); await database.CreateAsync();
        await using var fixture = new ComputeDispatchTests.Fixture(database.Options()); await fixture.InitializeAsync();
        var gate = new SaveGate();
        var conflicts = 0;
        async Task<ComputeDispatchPacket?> Claim()
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                await using var db = new CSweetDbContext(database.Options(gate));
                try { return await fixture.Authorizer(db).ClaimAsync(fixture.OperationId, default); }
                catch (Exception error) when (IsConflict(error)) { Interlocked.Increment(ref conflicts); }
            }
            throw new InvalidOperationException("Dispatch claims did not converge.");
        }
        var results = await Task.WhenAll(Claim(), Claim());
        Assert.True(conflicts > 0);
        var packet = Assert.Single(results, x => x is not null)!;
        var claim = JsonSerializer.Deserialize<ComputeDispatchAuthorization>(packet.Authorization.PayloadJson, ComputeBroker.Json)!;
        await using var verify = new CSweetDbContext(database.Options());
        var operation = await verify.ComputeOperations.SingleAsync();
        Assert.Equal(claim.DispatchId, operation.DispatchLeaseId);
        Assert.Equal(1, operation.Attempts);
        Assert.Equal(5, await verify.AuditOutbox.Where(x => x.SourceEntityType == null).CountAsync());
    }

    [PostgresTheory]
    [InlineData(true)]
    public async Task Provider_discovery_cursor_and_expired_receipts_execute_with_relational_scope_filters(bool _)
    {
        await using var database = new Database(); await database.CreateAsync();
        await using var f = new ComputeDispatchTests.Fixture(database.Options()); await f.InitializeAsync();
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var environment = await f.Broker.Db.ComputeEnvironments.SingleAsync();
        var node = new ComputeNodeVerificationKey("node", environment.OrganizationId, environment.ProviderNodeId!.Value, environment.ProviderId!,
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        var trust = new ComputeResultTests.Trust(node);
        var service = new ComputeProviderWorkService(f.Broker.Db, new(trust, f.Time), f.Authorizer(), f.Broker.Ledger, f.Time);
        var query = new ComputeProviderWorkRequest(node.OrganizationId, node.NodeId, node.ProviderId, Guid.NewGuid(), f.Time.Now, f.Time.Now.AddMinutes(1));
        SignedComputeProviderWorkRequest Sign(ComputeProviderWorkRequest value)
        {
            var json = JsonSerializer.Serialize(value, ComputeProtocol.Json);
            return new("node", json, Convert.ToBase64String(key.SignData(ComputeProtocol.WorkRequestPayload(json), System.Security.Cryptography.HashAlgorithmName.SHA256)));
        }
        Assert.Equal(f.OperationId, Assert.Single((await service.DiscoverAsync(Sign(query), default)).OperationIds));
        Assert.Empty((await service.DiscoverAsync(Sign(query with { AfterOperationId = f.OperationId }), default)).OperationIds);
        var other = query with { NodeId = Guid.NewGuid() };
        var otherService = new ComputeProviderWorkService(f.Broker.Db, new(new ComputeResultTests.Trust(node with { NodeId = other.NodeId }), f.Time), f.Authorizer(), f.Broker.Ledger, f.Time);
        Assert.Empty((await otherService.DiscoverAsync(Sign(other), default)).OperationIds);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => otherService.ClaimAsync(Sign(other with { OperationId = f.OperationId }), default));
        Assert.NotNull(await service.ClaimAsync(Sign(query with { OperationId = f.OperationId }), default));
        Assert.Empty((await service.DiscoverAsync(Sign(query), default)).OperationIds);
        var result = new ComputeProviderResult(f.OperationId, environment.Id, environment.OrganizationId, environment.InstallationId,
            node.ProviderId, node.NodeId, environment.Generation, InfrastructureActions.Provision, ComputeProtocol.Digest(environment.SpecificationJson),
            1, "vm-1", CSweet.Domain.Compute.ComputeLifecycleState.Ready, false, f.Time.Now, f.Time.Now.AddMinutes(2));
        var payload = JsonSerializer.Serialize(result, ComputeProtocol.Json);
        var envelope = new SignedComputeProviderResult("node", payload, Convert.ToBase64String(key.SignData(ComputeProtocol.ResultPayload(payload), System.Security.Cryptography.HashAlgorithmName.SHA256)));
        Assert.True(await new ComputeResultReconciler(f.Broker.Db, new(trust, f.Time), f.Time, f.Broker.Ledger).ApplyAsync(envelope, default));
        f.Time.Now = result.ExpiresAt.AddMinutes(1);
        var receiptQuery = query with { RequestId = Guid.NewGuid(), IssuedAt = f.Time.Now, ExpiresAt = f.Time.Now.AddMinutes(1),
            OperationId = f.OperationId, ResultSequence = 1, ResultDigest = ComputeProtocol.Digest(payload) };
        var receipt = await service.ReadResultReceiptAsync(Sign(receiptQuery), default);
        Assert.NotNull(receipt); Assert.False(receipt.Applied);
        var completedReceipt = await service.ReadResultReceiptAsync(Sign(receiptQuery with { ResultSequence = 2 }), default);
        Assert.NotNull(completedReceipt); Assert.Equal(ComputeResultDisposition.Completed, completedReceipt.Disposition);
        await f.Broker.Broker.ChangeLifecycleAsync(f.Broker.Organization, f.Broker.Installation,
            new(environment.Id, 1, InfrastructureActions.Destroy, "supersede-for-sql-receipt"), default);
        var supersededReceipt = await service.ReadResultReceiptAsync(Sign(receiptQuery with { ResultSequence = 3 }), default);
        Assert.NotNull(supersededReceipt); Assert.Equal(ComputeResultDisposition.Superseded, supersededReceipt.Disposition);
        Assert.True((await f.Broker.Db.ComputeEnvironments.SingleAsync()).HoldsReservation);
    }
    private sealed class WakePublisher : IComputeProviderWakePublisher
    {
        public int Calls { get; private set; }
        public Task<bool> PublishAsync(Guid organizationId, Guid nodeId, string providerId, Guid eventId, CancellationToken token)
        { Calls++; return Task.FromResult(true); }
    }

    [PostgresTheory]
    [InlineData(true)]
    public async Task Provider_wake_uniqueness_and_publication_receipts_are_relational(bool _)
    {
        await using var database = new Database(); await database.CreateAsync();
        // Only this newly created disposable database is changed. Exercise the scaffolded Up operations.
        await using (var schema = new CSweetDbContext(database.Options()))
        {
            await schema.Database.ExecuteSqlRawAsync("DROP TABLE \"ComputeProviderWakes\"");
            var migration = new CSweet.Infrastructure.Persistence.Migrations.AddComputeProviderWakeOutbox();
            var generator = schema.GetService<IMigrationsSqlGenerator>();
            foreach (var command in generator.Generate(migration.UpOperations, schema.Model))
                await schema.Database.ExecuteSqlRawAsync(command.CommandText);
        }
        await using var f = new ComputeBrokerTests.Fixture(database.Options()); await f.SeedAsync(); await f.Send();
        var wake = await f.Db.ComputeProviderWakes.SingleAsync();
        f.Db.ComputeProviderWakes.Add(new()
        {
            Id = Guid.NewGuid(), OperationId = wake.OperationId, OrganizationId = wake.OrganizationId,
            NodeId = wake.NodeId, ProviderId = wake.ProviderId, CreatedAt = wake.CreatedAt, NextAttemptAt = wake.NextAttemptAt
        });
        var conflict = await Assert.ThrowsAsync<DbUpdateException>(() => f.Db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(conflict.InnerException).SqlState);
        f.Db.ChangeTracker.Clear();
        var publisher = new WakePublisher();
        var dispatcher = new ComputeProviderWakeDispatcher(f.Db, publisher, new ComputeBrokerTests.Clock());
        Assert.Equal(new ComputeProviderWakeDispatchPass(1, 1, 0), await dispatcher.DispatchAsync(default));
        Assert.Equal(new ComputeProviderWakeDispatchPass(0, 0, 0), await dispatcher.DispatchAsync(default));
        Assert.Equal(1, publisher.Calls);
        await using var verify = new CSweetDbContext(database.Options());
        var recorded = await verify.ComputeProviderWakes.SingleAsync();
        Assert.NotNull(recorded.PublishedAt); Assert.Equal(1, recorded.Attempts); Assert.Equal(2, recorded.Revision);
        Assert.Equal("Pending", (await verify.ComputeOperations.SingleAsync()).Status);
        Assert.True((await verify.ComputeEnvironments.SingleAsync()).HoldsReservation);
    }
    private static bool IsConflict(Exception error) => error is DbUpdateConcurrencyException ||
        error is PostgresException { SqlState: "40001" or "23505" or "40P01" } ||
        error.InnerException is { } inner && IsConflict(inner);

    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default) => throw new InjectedFailure();
    }
    private sealed class InjectedFailure : Exception;

    [PostgresTheory]
    [InlineData(true)]
    public async Task Failure_after_sql_save_rolls_back_reservation_operation_receipt_and_wake(bool _)
    {
        await using var database = new Database(); await database.CreateAsync();
        await using var fixture = new ComputeBrokerTests.Fixture(database.Options()); await fixture.SeedAsync();
        await using (var failing = new CSweetDbContext(database.Options(new FailAfterSave())))
            await Assert.ThrowsAsync<InjectedFailure>(() => new ComputeBroker(failing, fixture.Templates, new ComputeBrokerTests.Clock(), fixture.Ledger)
                .RequestAsync(fixture.Organization, fixture.Installation, fixture.Request, default));
        await using var verify = new CSweetDbContext(database.Options());
        Assert.Equal(0, await verify.ComputeEnvironments.CountAsync());
        Assert.Equal(0, await verify.ComputeAdmissions.CountAsync());
        Assert.Equal(0, await verify.AuditOutbox.Where(x => x.SourceEntityType == null).CountAsync());
        Assert.Equal(0, await verify.ComputeOperations.CountAsync());
        Assert.Equal(0, await verify.ComputeProviderWakes.CountAsync());
        Assert.Equal(0, await verify.ComputeRequestReceipts.CountAsync());
        Assert.Equal(0, await verify.AgentPlatformEventOutbox.CountAsync(x => x.EventType == "com.csweet.compute.changed.v1"));
        var recovered = await fixture.Send();
        Assert.Equal(recovered.Id, (await fixture.Send()).Id);
    }
}
