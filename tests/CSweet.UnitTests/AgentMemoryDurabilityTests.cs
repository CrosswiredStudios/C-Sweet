using CSweet.AI.Providers;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using CSweet.Infrastructure.Persistence.Migrations;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresFact]
    public async Task ExplicitEnrichmentCreatesMissingJobAndDoesNotReopenCompletedJob()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context();
        await db.MemoryCaptureOutbox.ExecuteDeleteAsync();
        var factory = new ScriptedProviderFactory((_, _) => Task.CompletedTask);
        var service = fixture.Service(db, factory);
        await service.CaptureMessageAsync(fixture.MessageId, enrich: true);
        await service.CaptureMessageAsync(fixture.MessageId, enrich: true);
        Assert.Equal(1, factory.Calls);
        Assert.Equal(MemoryCaptureStatus.Completed, (await db.MemoryCaptureOutbox.SingleAsync()).Status);
        Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Claims);
        Assert.All((await fixture.Store.ExportAsync(fixture.Partition)).Entities,
            entity => Assert.Contains(fixture.MessageId, entity.SourceEpisodeIds));
    }

    [MemoryPostgresFact]
    public async Task AdditiveMigrationPreservesLegacyJobsAndRecoversAbandonedProcessing()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context();
        var migration = new DurableMemoryEnrichment();
        var generator = db.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate(migration.DownOperations))
            await db.Database.ExecuteSqlRawAsync(command.CommandText);
        await db.Database.ExecuteSqlRawAsync("UPDATE \"MemoryCaptureOutbox\" SET \"Status\" = 'Processing', \"Attempts\" = 1");
        foreach (var command in generator.Generate(migration.UpOperations))
            await db.Database.ExecuteSqlRawAsync(command.CommandText);
        var item = await db.MemoryCaptureOutbox.SingleAsync();
        Assert.Equal(fixture.MessageId, item.ConversationMessageId);
        Assert.Null(item.LeaseToken);
        Assert.Null(item.AcceptedExtractionJson);
        Assert.Equal(1, await fixture.Service(db, new UsageProviderFactory()).ProcessPendingAsync());
        Assert.Equal(MemoryCaptureStatus.Completed, item.Status);
        Assert.Equal(2, item.Attempts);
    }

    [MemoryPostgresFact]
    public async Task LeaseTokenRejectsSimultaneousCompareAndSwapLoser()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var first = fixture.Context();
        await using var second = fixture.Context();
        var winner = await first.MemoryCaptureOutbox.SingleAsync();
        var loser = await second.MemoryCaptureOutbox.SingleAsync();
        winner.LeaseToken = Guid.NewGuid();
        loser.LeaseToken = Guid.NewGuid();
        await first.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task TerminalFailureIsNotRetriedOrResetByForegroundCapture()
    {
        await using var fixture = await DurabilityFixture.CreateAsync();
        var factory = new ScriptedProviderFactory((_, _) => throw new InvalidOperationException("sensitive provider detail"));
        await using var db = fixture.Context();
        var item = await db.MemoryCaptureOutbox.SingleAsync();
        item.Attempts = 9;
        await db.SaveChangesAsync();
        var service = fixture.Service(db, factory);

        Assert.Equal(0, await service.ProcessPendingAsync());
        await db.Entry(item).ReloadAsync();
        Assert.Equal(MemoryCaptureStatus.Failed, item.Status);
        Assert.Equal(10, item.Attempts);
        Assert.Equal("memory_enrichment_failed", item.LastError);
        item.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        await service.CaptureMessageAsync(fixture.MessageId);
        Assert.Equal(0, await service.ProcessPendingAsync());
        Assert.Equal(MemoryCaptureStatus.Failed, item.Status);
        Assert.Equal(1, factory.Calls);
        var summary = await service.GetSummaryAsync(fixture.OrganizationId, fixture.EmployeeId);
        Assert.Equal("Needs attention", summary!.Health);
    }

    [Fact]
    public async Task InteractiveCancellationReleasesOwnershipWithoutSpendingFailureBudget()
    {
        await using var fixture = await DurabilityFixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new ScriptedProviderFactory(async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });
        await using var db = fixture.Context();
        using var cancellation = new CancellationTokenSource();
        var processing = fixture.Service(db, factory).ProcessPendingAsync(cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        var item = await db.MemoryCaptureOutbox.SingleAsync();
        Assert.Equal(MemoryCaptureStatus.Pending, item.Status);
        Assert.Equal(0, item.Attempts);
        Assert.Null(item.LeaseToken);
        Assert.Null(item.LeaseExpiresAt);
    }

    [MemoryPostgresFact]
    public async Task ConcurrentWorkerCannotClaimAnUnexpiredLease()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new ScriptedProviderFactory(async (_, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        });
        await using var first = fixture.Context();
        await using var second = fixture.Context();
        var processing = fixture.Service(first, factory).ProcessPendingAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await fixture.Service(second, factory).CaptureMessageAsync(fixture.MessageId);
            Assert.Equal(0, await fixture.Service(second, factory).ProcessPendingAsync());
            Assert.Equal(1, factory.Calls);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(1, await processing);
        await fixture.Service(second, factory).CaptureMessageAsync(fixture.MessageId);
        Assert.Equal(0, await fixture.Service(second, factory).ProcessPendingAsync());
        second.ChangeTracker.Clear();
        Assert.Equal(MemoryCaptureStatus.Completed, (await second.MemoryCaptureOutbox.SingleAsync()).Status);
        Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Claims);
    }

    [MemoryPostgresFact]
    public async Task ExpiredWorkerCannotAcceptOrApplyAfterAnotherWorkerFinishes()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new ScriptedProviderFactory(async (call, ct) =>
        {
            if (call != 1) return;
            entered.SetResult();
            await release.Task.WaitAsync(ct);
        });
        await using var first = fixture.Context();
        await using var second = fixture.Context();
        var obsolete = fixture.Service(first, factory).ProcessPendingAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await second.MemoryCaptureOutbox.ExecuteUpdateAsync(set => set
                .SetProperty(x => x.LeaseExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
            Assert.Equal(1, await fixture.Service(second, factory).ProcessPendingAsync());
        }
        finally { release.TrySetResult(); }
        Assert.Equal(0, await obsolete);
        Assert.Equal(2, factory.Calls);
        second.ChangeTracker.Clear();
        var item = await second.MemoryCaptureOutbox.SingleAsync();
        Assert.Equal(MemoryCaptureStatus.Completed, item.Status);
        Assert.Equal(2, item.Attempts);
        Assert.Null(item.LeaseToken);
        Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Claims);
    }

    [MemoryPostgresFact]
    public async Task FailedApplyRollsBackAndReplaysAcceptedExtractionWithoutCallingProviderAgain()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var factory = new ScriptedProviderFactory((call, _) => call == 1 ? Task.CompletedTask
            : throw new InvalidOperationException("Accepted output must be reused."));
        await using var db = fixture.Context();
        await fixture.Store.InitializeAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_memory_claim() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected apply failure'; END $$;
            CREATE TRIGGER reject_memory_claim BEFORE INSERT ON csweet_memory_claims
                FOR EACH ROW EXECUTE FUNCTION reject_memory_claim();
            """);
        Assert.Equal(0, await fixture.Service(db, factory).ProcessPendingAsync());
        var item = await db.MemoryCaptureOutbox.SingleAsync();
        Assert.NotNull(item.AcceptedExtractionJson);
        Assert.NotNull(item.ExtractionAcceptedAt);
        var accepted = item.AcceptedExtractionJson;
        var partial = await fixture.Store.ExportAsync(fixture.Partition);
        Assert.Empty(partial.Entities);
        Assert.Empty(partial.Claims);
        var historyCount = await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM csweet_memory_revisions WHERE kind IN (1,2)").SingleAsync();
        Assert.Equal(0, historyCount);
        Assert.Equal(MemoryCaptureStatus.Pending, item.Status);

        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_memory_claim ON csweet_memory_claims; DROP FUNCTION reject_memory_claim();");
        item.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
        // A fresh context simulates process restart after accepted output was persisted.
        await using var restarted = fixture.Context();
        Assert.Equal(1, await fixture.Service(restarted, factory).ProcessPendingAsync());
        var completed = await restarted.MemoryCaptureOutbox.SingleAsync();
        Assert.Equal(accepted, completed.AcceptedExtractionJson);
        Assert.Equal(1, factory.Calls);
        Assert.Equal(MemoryCaptureStatus.Completed, completed.Status);
        var appliedClaim = Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Claims);
        var revisions = await ((IMemoryRevisionReader)fixture.Store).ReadRevisionsAsync(fixture.Partition, MemoryRecordKind.Claim, appliedClaim.Id);
        Assert.Equal(MemoryRevisionOperation.Insert, Assert.Single(revisions.Items).Operation);
    }

    private sealed class ScriptedProviderFactory(Func<int, CancellationToken, Task> beforeResponse) : ILlmProviderFactory
    {
        private int calls;
        public int Calls => calls;
        public Task<IChatClient> CreateChatClientAsync(Guid id, CancellationToken ct = default) => CreateChatClientAsync(id, "test", ct);
        public Task<IChatClient> CreateChatClientAsync(Guid id, string? model, CancellationToken ct = default) =>
            Task.FromResult<IChatClient>(new ScriptedChatClient(async token => await beforeResponse(Interlocked.Increment(ref calls), token)));
    }

    private sealed class ScriptedChatClient(Func<CancellationToken, Task> beforeResponse) : DelegatingChatClient(new UsageChatClient())
    {
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            await beforeResponse(cancellationToken);
            return await base.GetResponseAsync(messages, options, cancellationToken);
        }
    }

    private sealed class DurabilityFixture : IAsyncDisposable
    {
        private readonly DbContextOptions<CSweetDbContext> options;
        private readonly string? adminConnection;
        private readonly string? databaseName;
        private readonly string? sqlitePath;
        private readonly NpgsqlDataSource? contextDataSource;
        public Guid OrganizationId { get; } = Guid.NewGuid();
        public Guid EmployeeId { get; } = Guid.NewGuid();
        public Guid HumanId { get; } = Guid.NewGuid();
        public Guid InstallationId { get; } = Guid.NewGuid();
        public Guid ProviderId { get; } = Guid.NewGuid();
        public Guid MessageId { get; } = Guid.NewGuid();
        public IMemoryStore Store { get; }
        public MemoryPartition Partition => EmployeeMemoryNamespaces.UserRelationship(OrganizationId.ToString("D"),
            EmployeeId.ToString("D"), HumanId.ToString("D"), "csweet").Partition;

        private DurabilityFixture(DbContextOptions<CSweetDbContext> options, IMemoryStore store, string? adminConnection,
            string? databaseName, string? sqlitePath, NpgsqlDataSource? contextDataSource = null)
        {
            this.options = options; Store = store; this.adminConnection = adminConnection;
            this.databaseName = databaseName; this.sqlitePath = sqlitePath;
            this.contextDataSource = contextDataSource;
        }

        public CSweetDbContext Context(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) =>
            new(new DbContextOptionsBuilder<CSweetDbContext>(options).AddInterceptors(interceptors).Options);

        public NpgsqlConnection IndependentConnection() => contextDataSource?.CreateConnection() ?? throw new NotSupportedException();
        public AgentMemoryService Service(CSweetDbContext db, ILlmProviderFactory factory) => new(db, Store, factory,
            new StaticInstallationConfigurationService(InstallationId, ProviderId, "test-model"), NullLogger<AgentMemoryService>.Instance);

        public static async Task<DurabilityFixture> CreateAsync(bool postgres = false)
        {
            var builder = new DbContextOptionsBuilder<CSweetDbContext>();
            var admin = postgres ? Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES")! : null;
            var databaseName = postgres ? "memory_durability_" + Guid.NewGuid().ToString("N") : null;
            string? path = null;
            NpgsqlDataSource? contextDataSource = null;
            IMemoryStore store;
            if (postgres)
            {
                await using var connection = new NpgsqlConnection(admin);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand($"CREATE DATABASE {databaseName}", connection);
                await command.ExecuteNonQueryAsync();
                // Bounded owned pools avoid exhausting Windows ephemeral ports in large
                // suites. Dispose both pools before dropping this fixture's database.
                var connectionString = new NpgsqlConnectionStringBuilder(admin) { Database = databaseName, Pooling = true, MaxPoolSize = 16 }.ConnectionString;
                contextDataSource = NpgsqlDataSource.Create(connectionString);
                builder.UseNpgsql(contextDataSource);
                store = new PostgreSqlMemoryStore(connectionString);
            }
            else
            {
                builder.UseInMemoryDatabase(Guid.NewGuid().ToString());
                path = Path.Combine(Path.GetTempPath(), $"memory-durability-{Guid.NewGuid():N}.db");
                store = new SqliteMemoryStore(path);
            }
            var fixture = new DurabilityFixture(builder.Options, store, admin, databaseName, path, contextDataSource);
            try
            {
                await using var db = fixture.Context();
                await db.Database.EnsureCreatedAsync();
                if (postgres)
                {
                    await using var authority = await db.Database.BeginTransactionAsync();
                    await db.Database.ExecuteSqlRawAsync(CSweet.Infrastructure.Persistence.Migrations.MemoryAccessAuthority.AuthorityTriggers);
                    await authority.CommitAsync();
                }
                var now = DateTimeOffset.UtcNow;
                var organization = new Organization { Id = fixture.OrganizationId, Name = "Memory test", CreatedAt = now, UpdatedAt = now };
                var package = new AgentPackageVersion { Id = Guid.NewGuid(), AgentId = "memory.test", AgentName = "Test", Version = "1.0.0",
                    ManifestJson = "{}", PackageSource = new AgentPackageSource { Id = Guid.NewGuid(), RepositoryUrl = "https://example.test/memory", CreatedAt = now, UpdatedAt = now } };
                var installation = new AgentInstallation { Id = fixture.InstallationId, PackageVersion = package,
                    PackageVersionId = package.Id, BusinessId = fixture.OrganizationId.ToString("D"), IsEnabled = true };
                var employee = new OrganizationUser { Id = fixture.EmployeeId, OrganizationId = organization.Id,
                    EmployeeType = EmployeeType.Agent, AgentInstallation = installation, AgentInstallationId = installation.Id, CreatedAt = now };
                var human = new OrganizationUser { Id = fixture.HumanId, OrganizationId = organization.Id,
                    EmployeeType = EmployeeType.Human, CreatedAt = now };
                var conversation = new Conversation { Id = Guid.NewGuid(), OrganizationId = organization.Id,
                    AgentOrganizationUser = employee, AgentOrganizationUserId = employee.Id, InitiatedByOrganizationUserId = human.Id,
                    CreatedAt = now, UpdatedAt = now };
                db.CoreOrganizations.Add(organization);
                db.CoreOrganizationUsers.AddRange(employee, human);
                db.CoreConversationMessages.Add(new ConversationMessage { Id = fixture.MessageId, Conversation = conversation,
                    ConversationId = conversation.Id, Role = ConversationRole.User, Content = "My name is Alice.", CreatedAt = now });
                db.MemoryCaptureOutbox.Add(new MemoryCaptureOutboxItem { Id = Guid.NewGuid(), ConversationMessageId = fixture.MessageId,
                    CreatedAt = now, NextAttemptAt = now, Status = MemoryCaptureStatus.Pending });
                db.LlmProviderProfiles.Add(new LlmProviderProfile { Id = fixture.ProviderId, Name = "Test", ProviderType = LlmProviderType.LmStudio,
                    BaseUrl = "http://test-provider/v1", IsEnabled = true, CreatedAt = now, UpdatedAt = now });
                await db.SaveChangesAsync();
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            if (contextDataSource is not null) await contextDataSource.DisposeAsync();
            if (databaseName is not null)
            {
                await using var connection = new NpgsqlConnection(adminConnection);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand($"DROP DATABASE {databaseName} WITH (FORCE)", connection);
                await command.ExecuteNonQueryAsync();
            }
            if (sqlitePath is not null)
                foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(sqlitePath + suffix);
        }
    }
}

public sealed class MemoryPostgresFactAttribute : FactAttribute
{
    public MemoryPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES")))
            Skip = "Set CSWEET_MEMORY_TEST_POSTGRES to an isolated PostgreSQL server with database creation permission.";
    }
}
