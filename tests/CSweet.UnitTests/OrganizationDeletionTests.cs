using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Compute.Contracts;
using CSweet.Domain.Analytics;
using CSweet.Domain.Communications;
using CSweet.Domain.Compute;
using CSweet.Domain.Core;
using CSweet.Domain.PersonalAssistant;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed class OrganizationDeletionTests
{
    [Fact]
    public async Task PurgeAsync_DeletesOnboardedBusinessAndPreservesAuditHistory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CSweetDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new CSweetDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var deleted = Organization("Deleted");
        var retained = Organization("Retained");
        var onboarding = Onboarding(deleted.Id);
        var deletedProfile = BusinessProfile(deleted.Id);
        var retainedProfile = BusinessProfile(retained.Id);
        var sharedWorker = Worker(deleted.Id);
        var artifact = Artifact(deleted.Id);
        var revision = ArtifactRevision(deleted.Id, artifact.Id);
        var approval = Approval(artifact.Id, revision.Id);
        var audit = new AuditEvent
        {
            Id = Guid.NewGuid(),
            Sequence = 1,
            OrganizationId = deleted.Id,
            EventType = "organization.created",
            EntityType = nameof(Organization),
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var episodeRetry = new MemoryEpisodeRetryReceipt
        {
            Id = Guid.NewGuid(), JobId = Guid.NewGuid(), OperationId = Guid.NewGuid(), OrganizationId = deleted.Id,
            EmployeeId = Guid.NewGuid(), ActorOrganizationUserId = Guid.NewGuid(), ActorApplicationUserId = Guid.NewGuid(),
            RetryGeneration = 1, PreviousAttempts = 10, SourceHash = new string('a', 64), CreatedAt = DateTimeOffset.UtcNow
        };
        db.AddRange(deleted, retained, onboarding, deletedProfile, retainedProfile, sharedWorker, audit, episodeRetry);
        await db.SaveChangesAsync();
        // Seed these with SQL so this deletion test remains isolated from realtime
        // outbox capture, whose generated sequence is PostgreSQL-specific.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "CoreArtifacts"
                ("Id", "OrganizationId", "Type", "Title", "Content", "Version", "ApprovalStatus", "CreatedAt", "UpdatedAt", "CreatorDisplayName", "DocumentType", "DocumentStatus")
            VALUES
                ({artifact.Id}, {artifact.OrganizationId}, 'Document', {artifact.Title}, {artifact.Content}, 0, 'Pending', {artifact.CreatedAt}, {artifact.UpdatedAt}, {artifact.CreatorDisplayName}, {artifact.DocumentType}, 'Draft');
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ArtifactRevisions"
                ("Id", "OrganizationId", "ArtifactId", "Number", "Content", "ContentSha256", "Status", "CreatorDisplayName", "IdempotencyKey", "CreatedAt")
            VALUES
                ({revision.Id}, {revision.OrganizationId}, {revision.ArtifactId}, {revision.Number}, {revision.Content}, {revision.ContentSha256}, 'Draft', {revision.CreatorDisplayName}, {revision.IdempotencyKey}, {revision.CreatedAt});
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "CoreApprovals" ("Id", "ArtifactId", "ArtifactRevisionId", "Status", "CreatedAt")
            VALUES ({approval.Id}, {approval.ArtifactId}, {approval.ArtifactRevisionId}, 'Pending', {approval.CreatedAt});
            """);

        var packageId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ArtifactPackages"
                ("Id", "OrganizationId", "Name", "PackageType", "Version", "IdempotencyKey", "Status", "CreatedAt", "UpdatedAt")
            VALUES
                ({packageId}, {deleted.Id}, 'Deletion test package', 'Documents', 1, 'deletion-test', 'Draft', {deleted.CreatedAt}, {deleted.UpdatedAt});
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ArtifactPackageMembers"
                ("Id", "PackageId", "ArtifactId", "Position", "RequiredDocumentType")
            VALUES
                ({memberId}, {packageId}, {artifact.Id}, 0, 'Document');
            """);

        var definition = new BenchmarkDefinition
        {
            Id = Guid.NewGuid(), FamilyId = Guid.NewGuid(), Version = 1,
            Name = "Deletion test", CreatedBy = Guid.NewGuid()
        };
        var campaign = new BenchmarkCampaign
        {
            Id = Guid.NewGuid(), DefinitionId = definition.Id,
            CreatedBy = Guid.NewGuid(), IdempotencyKey = "deletion-test"
        };
        var trial = new BenchmarkTrial
        {
            Id = Guid.NewGuid(), CampaignId = campaign.Id, OrganizationId = deleted.Id
        };
        var assessment = new BenchmarkAssessment
        {
            Id = Guid.NewGuid(), TrialId = trial.Id, Kind = "Quality",
            CriterionKey = "deletion-test", IdempotencyKey = "deletion-test"
        };
        db.AddRange(definition, campaign, trial, assessment);
        await db.SaveChangesAsync();

        var service = new OrganizationDataPurgeService(
            db,
            new SuccessfulAgentCleanup(),
            NullLogger<OrganizationDataPurgeService>.Instance);

        await service.PurgeAsync(deleted.Id);
        Assert.Equal(episodeRetry.SourceHash, (await db.MemoryEpisodeRetryReceipts.SingleAsync(x => x.Id == episodeRetry.Id)).SourceHash);

        Assert.False(await db.CoreOrganizations.AnyAsync(x => x.Id == deleted.Id));
        Assert.True(await db.CoreOrganizations.AnyAsync(x => x.Id == retained.Id));
        Assert.False(await db.BusinessOnboardingOperations.AnyAsync(x => x.Id == onboarding.Id));
        Assert.False(await db.BusinessProfiles.AnyAsync(x => x.Id == deletedProfile.Id));
        Assert.True(await db.BusinessProfiles.AnyAsync(x => x.Id == retainedProfile.Id));
        Assert.False(await db.CoreApprovals.AnyAsync(x => x.Id == approval.Id));
        Assert.False(await db.ArtifactPackages.AnyAsync(x => x.Id == packageId));
        Assert.False(await db.ArtifactPackageMembers.AnyAsync(x => x.Id == memberId));
        Assert.False(await db.BenchmarkTrials.AnyAsync(x => x.Id == trial.Id));
        Assert.False(await db.BenchmarkAssessments.AnyAsync(x => x.Id == assessment.Id));
        Assert.True(await db.BenchmarkCampaigns.AnyAsync(x => x.Id == campaign.Id));
        Assert.Equal(deleted.Id, (await db.AuditEvents.SingleAsync(x => x.Id == audit.Id)).OrganizationId);
        Assert.Null((await db.CoreWorkers.SingleAsync(x => x.Id == sharedWorker.Id)).OrganizationId);
    }

    [Fact]
    public async Task PurgeAsync_RejectsUnconfirmedComputeTeardownBeforeQuiescingAgents()
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new CSweetDbContext(options);
        var organization = Organization("Compute owner");
        db.CoreOrganizations.Add(organization);
        db.ComputeEnvironments.Add(new ComputeEnvironment
        {
            Id = Guid.NewGuid(),
            OrganizationId = organization.Id,
            InstallationId = Guid.NewGuid(),
            WorkstreamId = Guid.NewGuid(),
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddHours(-1)
        });
        await db.SaveChangesAsync();
        var cleanup = new CountingAgentCleanup();
        var purge = new OrganizationDataPurgeService(
            db, cleanup, NullLogger<OrganizationDataPurgeService>.Instance);

        var error = await Assert.ThrowsAsync<OrganizationDeletionException>(
            () => purge.PurgeAsync(organization.Id));

        Assert.Contains("confirmed VM and disk teardown", error.Message);
        Assert.Equal(0, cleanup.Attempts);
        Assert.True(await db.CoreOrganizations.AnyAsync(x => x.Id == organization.Id));
        Assert.True(await db.ComputeEnvironments.AnyAsync(x => x.OrganizationId == organization.Id));
    }

    [Fact]
    public async Task DeleteAsync_RequestsComputeCleanupOnceAndWaitsForSignedTeardownBeforePurging()
    {
        await using var f = new ComputeResultTests.Fixture();
        await f.InitializeAsync();
        await f.Reconciler.ApplyAsync(f.Sign(), default);
        var db = f.Broker.Db;
        var environment = await db.ComputeEnvironments.SingleAsync();
        environment.LeaseExpiresAt = DateTimeOffset.MaxValue;
        await db.SaveChangesAsync();
        var cleanup = new CountingAgentCleanup();
        var audit = new TestAuditEventWriter();
        var purge = new OrganizationDataPurgeService(db, cleanup,
            NullLogger<OrganizationDataPurgeService>.Instance, f.Broker.Broker);
        var service = new CoreOrganizationService(db, audit, new RoleService(db, audit), purge);

        for (var retry = 0; retry < 2; retry++)
        {
            var result = await service.DeleteAsync(f.Broker.Organization);
            Assert.False(result.Succeeded);
            Assert.Equal("compute_cleanup_pending", result.ErrorCode);
            Assert.Equal(0, cleanup.Attempts);
            Assert.True(await db.CoreOrganizations.AnyAsync(x => x.Id == f.Broker.Organization));
            Assert.Null(environment.TeardownConfirmedAt);
        }
        var destroy = Assert.Single(await db.ComputeOperations.Where(x => x.Action == InfrastructureActions.Destroy).ToListAsync());
        Assert.Equal(ComputeDesiredState.Destroyed, environment.DesiredState);
        Assert.True(await db.ComputeProviderWakes.AnyAsync(x => x.OperationId == destroy.Id));
        Assert.Contains(await db.AuditOutbox.ToListAsync(), x => x.RequestJson.Contains("compute.lifecycle.v1"));

        destroy.Status = "Dispatching";
        await db.SaveChangesAsync();
        Assert.True(await f.Reconciler.ApplyAsync(f.Sign(f.Result with
        {
            OperationId = destroy.Id, Generation = environment.Generation,
            Action = InfrastructureActions.Destroy, State = ComputeLifecycleState.Destroyed, TeardownConfirmed = true
        }), default));

        // A retry is a fresh HTTP request, without the provider reconciler's tracked rows.
        db.ChangeTracker.Clear();
        Assert.True((await service.DeleteAsync(f.Broker.Organization)).Succeeded);
        Assert.Equal(1, cleanup.Attempts);
        Assert.False(await db.CoreOrganizations.AnyAsync(x => x.Id == f.Broker.Organization));
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Blocked")]
    public async Task PurgeAsync_RetriesFailedComputeCleanupWithANewGeneration(string status)
    {
        await using var f = new ComputeBrokerTests.Fixture();
        await f.SeedAsync();
        await f.Send();
        var cleanup = new CountingAgentCleanup();
        var purge = new OrganizationDataPurgeService(f.Db, cleanup,
            NullLogger<OrganizationDataPurgeService>.Instance, f.Broker);
        await Assert.ThrowsAsync<OrganizationDeletionException>(() => purge.PurgeAsync(f.Organization));
        var destroy = await f.Db.ComputeOperations.SingleAsync(x => x.Action == InfrastructureActions.Destroy);
        destroy.Status = status;
        await f.Db.SaveChangesAsync();

        var result = await Assert.ThrowsAsync<OrganizationDeletionException>(() => purge.PurgeAsync(f.Organization));

        Assert.Equal("compute_cleanup_pending", result.ErrorCode);
        var operations = await f.Db.ComputeOperations.Where(x => x.Action == InfrastructureActions.Destroy).ToListAsync();
        Assert.Equal(2, operations.Count);
        Assert.Contains(operations, x => x.Generation == destroy.Generation + 1 && x.Status == "Pending");
        Assert.Equal(0, cleanup.Attempts);
    }

    [Fact]
    public async Task PurgeAsync_BoundsComputeCleanupAndSkipsQueuedInstancesOnRetry()
    {
        await using var f = new ComputeBrokerTests.Fixture();
        await f.SeedAsync();
        await f.Send();
        var source = await f.Db.ComputeEnvironments.SingleAsync();
        for (var index = 0; index < 100; index++)
            f.Db.ComputeEnvironments.Add(new()
            {
                Id = Guid.NewGuid(), OrganizationId = source.OrganizationId, InstallationId = source.InstallationId,
                WorkstreamId = source.WorkstreamId, ProviderNodeId = source.ProviderNodeId, ProviderId = source.ProviderId,
                SpecificationJson = source.SpecificationJson, LeaseExpiresAt = source.LeaseExpiresAt
            });
        var otherBusiness = Guid.NewGuid();
        f.Db.ComputeEnvironments.Add(new()
        {
            Id = Guid.NewGuid(), OrganizationId = otherBusiness, InstallationId = source.InstallationId,
            WorkstreamId = source.WorkstreamId, ProviderNodeId = source.ProviderNodeId, ProviderId = source.ProviderId
        });
        await f.Db.SaveChangesAsync();
        var cleanup = new CountingAgentCleanup();
        var purge = new OrganizationDataPurgeService(f.Db, cleanup,
            NullLogger<OrganizationDataPurgeService>.Instance, f.Broker);

        await Assert.ThrowsAsync<OrganizationDeletionException>(() => purge.PurgeAsync(f.Organization));
        Assert.Equal(100, await f.Db.ComputeOperations.CountAsync(x => x.Action == InfrastructureActions.Destroy));
        await Assert.ThrowsAsync<OrganizationDeletionException>(() => purge.PurgeAsync(f.Organization));
        Assert.Equal(101, await f.Db.ComputeOperations.CountAsync(x => x.Action == InfrastructureActions.Destroy));
        Assert.False(await f.Db.ComputeOperations.AnyAsync(x => x.OrganizationId == otherBusiness));
        Assert.Equal(0, cleanup.Attempts);
    }

    [Fact]
    public async Task PurgeAsync_WhenComputeAuthorityIsUnavailableRetainsBusinessAndReportsCleanupFailure()
    {
        await using var f = new ComputeBrokerTests.Fixture();
        await f.SeedAsync();
        await f.Send();
        var installation = await f.Db.AgentInstallations.SingleAsync();
        installation.IsEnabled = false;
        await f.Db.SaveChangesAsync();
        var cleanup = new CountingAgentCleanup();
        var purge = new OrganizationDataPurgeService(f.Db, cleanup,
            NullLogger<OrganizationDataPurgeService>.Instance, f.Broker);

        var result = await Assert.ThrowsAsync<OrganizationDeletionException>(() => purge.PurgeAsync(f.Organization));

        Assert.Equal("compute_cleanup_failed", result.ErrorCode);
        Assert.Equal(0, cleanup.Attempts);
        Assert.True(await f.Db.CoreOrganizations.AnyAsync(x => x.Id == f.Organization));
        Assert.False(await f.Db.ComputeOperations.AnyAsync(x => x.Action == InfrastructureActions.Destroy));
    }

    [Fact]
    public async Task DeleteAsync_WhenCleanupFails_ReturnsStructuredFailureAndRetainsBusiness()
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new CSweetDbContext(options);
        var organization = Organization("Retryable");
        db.CoreOrganizations.Add(organization);
        await db.SaveChangesAsync();
        var audit = new TestAuditEventWriter();
        var cleanup = new FailOnceAgentCleanup();
        var purge = new OrganizationDataPurgeService(
            db,
            cleanup,
            NullLogger<OrganizationDataPurgeService>.Instance);
        var service = new CoreOrganizationService(
            db,
            audit,
            new RoleService(db, audit),
            purge);

        var failed = await service.DeleteAsync(organization.Id);

        Assert.False(failed.Succeeded);
        Assert.Equal("deletion_failed", failed.ErrorCode);
        Assert.Contains("retry", failed.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.True(await db.CoreOrganizations.AnyAsync(x => x.Id == organization.Id));

        var retried = await service.DeleteAsync(organization.Id);

        Assert.True(retried.Succeeded);
        Assert.False(await db.CoreOrganizations.AnyAsync(x => x.Id == organization.Id));
        Assert.Equal(2, cleanup.Attempts);
        var deletedEvent = Assert.Single(audit.Events, x => x.EventType == "organization.deleted");
        Assert.False(deletedEvent.UseAmbientOrganization);
    }

    [Fact]
    public async Task DeleteAsync_WhenAuditWriteFailsAfterPurge_StillReportsSuccessfulDeletion()
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new CSweetDbContext(options);
        var organization = Organization("Audited");
        db.CoreOrganizations.Add(organization);
        await db.SaveChangesAsync();
        var audit = new ThrowingAuditEventWriter();
        var service = new CoreOrganizationService(
            db,
            audit,
            new RoleService(db, new TestAuditEventWriter()));

        var result = await service.DeleteAsync(organization.Id);

        Assert.True(result.Succeeded);
        Assert.False(await db.CoreOrganizations.AnyAsync(x => x.Id == organization.Id));
    }

    [Fact]
    public void PurgeClassification_CoversAllRequiredOrganizationScopeProperties()
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new CSweetDbContext(options);
        var classified = OrganizationDataPurgeService.ScopedEntityTypes(db.Model)
            .Select(x => x.ClrType)
            .ToHashSet();
        var preserved = new[] { typeof(Organization), typeof(AuditEvent), typeof(MemoryCaptureRetryReceipt), typeof(MemoryEpisodeRetryReceipt), typeof(MemoryEpisodeReextractionReceipt), typeof(MemoryExtractionInputReceipt), typeof(MemoryReviewReceipt), typeof(MemoryErasureReceipt), typeof(MemoryTransferReceipt), typeof(AgentMemoryReadReceipt), typeof(MemoryCaptureExclusion), typeof(Worker) };
        var expected = db.Model.GetEntityTypes()
            .Where(x => x.BaseType is null && x.FindPrimaryKey() is not null && x.GetTableName() is not null)
            .Where(x => x.FindProperty("OrganizationId") is { } property &&
                        (Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType) is var type &&
                        (type == typeof(Guid) || type == typeof(string)))
            .Select(x => x.ClrType)
            .Except(preserved)
            .ToList();

        Assert.All(expected, type => Assert.Contains(type, classified));
        Assert.Contains(typeof(AgentInstallation), classified);
        Assert.Contains(typeof(ExecutionWorkloadAssignment), classified);
        Assert.DoesNotContain(typeof(AuditEvent), classified);
        Assert.DoesNotContain(typeof(MemoryCaptureRetryReceipt), classified);
        Assert.DoesNotContain(typeof(MemoryEpisodeRetryReceipt), classified);
        Assert.DoesNotContain(typeof(MemoryEpisodeReextractionReceipt), classified);
        Assert.DoesNotContain(typeof(MemoryExtractionInputReceipt), classified);
        Assert.DoesNotContain(typeof(MemoryReviewReceipt), classified);
        Assert.DoesNotContain(typeof(MemoryTransferReceipt), classified);
        Assert.DoesNotContain(typeof(MemoryCaptureExclusion), classified);
        Assert.DoesNotContain(typeof(Worker), classified);
    }

    [Fact]
    public void PurgeClassification_CoversIndirectRestrictiveDependents()
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var db = new CSweetDbContext(options);
        var scoped = OrganizationDataPurgeService.ScopedEntityTypes(db.Model).ToHashSet();
        var actual = db.Model.GetEntityTypes()
            .Where(entity => !scoped.Contains(entity) &&
                entity.GetForeignKeys().Any(foreignKey =>
                    scoped.Contains(foreignKey.PrincipalEntityType) &&
                    foreignKey.DeleteBehavior is DeleteBehavior.Restrict or DeleteBehavior.NoAction))
            .Select(entity => entity.ClrType.FullName)
            .OrderBy(name => name)
            .ToList();
        var expected = new[]
        {
            typeof(Approval), typeof(ArtifactPackageMember), typeof(BenchmarkAssessment),
            typeof(AgentCoordinationTurn), typeof(ConversationParticipant),
            // Personal Assistant installations use personal scope, not the business ID.
            // Their profiles stay with the account and must not be purged with a business.
            typeof(PersonalAssistantProfile),
            typeof(WorkExecutionAttempt), typeof(WorkItemDependency), typeof(WorkItemExecution), typeof(WorkDeliveryExecution)
        }.Select(type => type.FullName).OrderBy(name => name).ToList();

        Assert.Equal(expected, actual);
    }

    private static Organization Organization(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static BusinessOnboardingOperation Onboarding(Guid organizationId) => new()
    {
        Id = Guid.NewGuid(),
        InitiatedByApplicationUserId = Guid.NewGuid(),
        IdempotencyKey = Guid.NewGuid().ToString("N"),
        BusinessName = "Deleted",
        ChiefAgentPackageVersionId = Guid.NewGuid(),
        ChiefAgentInstallRequestJson = "{}",
        Status = BusinessOnboardingOperationStatus.Succeeded,
        ResultOrganizationId = organizationId,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        CompletedAt = DateTimeOffset.UtcNow
    };

    private static BusinessProfile BusinessProfile(Guid organizationId) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = organizationId,
        TimeZone = "UTC",
        ProvenanceJson = "{}",
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static Worker Worker(Guid organizationId) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = organizationId,
        Name = "Shared worker",
        Description = "Preserved shared definition",
        CapabilitiesJson = "[]",
        IsEnabled = true,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static Artifact Artifact(Guid organizationId) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = organizationId,
        Type = ArtifactType.Document,
        Title = "Deletion test artifact",
        Content = "Test content",
        CreatorDisplayName = "Test user",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static ArtifactRevision ArtifactRevision(Guid organizationId, Guid artifactId) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = organizationId,
        ArtifactId = artifactId,
        Number = 1,
        Content = "Test content",
        ContentSha256 = new string('0', 64),
        CreatorDisplayName = "Test user",
        IdempotencyKey = Guid.NewGuid().ToString("N"),
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static Approval Approval(Guid artifactId, Guid revisionId) => new()
    {
        Id = Guid.NewGuid(),
        ArtifactId = artifactId,
        ArtifactRevisionId = revisionId,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private sealed class CountingAgentCleanup : IBusinessAgentInstallationCleanup
    {
        public int Attempts { get; private set; }
        public Task QuiesceAsync(Guid organizationId, CancellationToken cancellationToken = default)
        {
            Attempts++;
            return Task.CompletedTask;
        }
    }

    private sealed class SuccessfulAgentCleanup : IBusinessAgentInstallationCleanup
    {
        public Task QuiesceAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FailOnceAgentCleanup : IBusinessAgentInstallationCleanup
    {
        public int Attempts { get; private set; }

        public Task QuiesceAsync(Guid organizationId, CancellationToken cancellationToken = default)
        {
            Attempts++;
            return Attempts == 1
                ? Task.FromException(new InvalidOperationException("Runtime cleanup failed."))
                : Task.CompletedTask;
        }
    }

    private sealed class ThrowingAuditEventWriter : IAuditEventWriter
    {
        public Task WriteAsync(
            string eventType,
            string entityType,
            Guid? entityId,
            string? summary,
            string? metadataJson = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("Audit unavailable."));

        public Task<Guid> AppendAsync(
            AuditEventWriteRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<Guid>(new InvalidOperationException("Audit unavailable."));
    }
}
