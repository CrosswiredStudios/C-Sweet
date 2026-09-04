using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
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
        db.AddRange(deleted, retained, onboarding, deletedProfile, retainedProfile, sharedWorker, audit);
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

        var service = new OrganizationDataPurgeService(
            db,
            new SuccessfulAgentCleanup(),
            NullLogger<OrganizationDataPurgeService>.Instance);

        await service.PurgeAsync(deleted.Id);

        Assert.False(await db.CoreOrganizations.AnyAsync(x => x.Id == deleted.Id));
        Assert.True(await db.CoreOrganizations.AnyAsync(x => x.Id == retained.Id));
        Assert.False(await db.BusinessOnboardingOperations.AnyAsync(x => x.Id == onboarding.Id));
        Assert.False(await db.BusinessProfiles.AnyAsync(x => x.Id == deletedProfile.Id));
        Assert.True(await db.BusinessProfiles.AnyAsync(x => x.Id == retainedProfile.Id));
        Assert.False(await db.CoreApprovals.AnyAsync(x => x.Id == approval.Id));
        Assert.Equal(deleted.Id, (await db.AuditEvents.SingleAsync(x => x.Id == audit.Id)).OrganizationId);
        Assert.Null((await db.CoreWorkers.SingleAsync(x => x.Id == sharedWorker.Id)).OrganizationId);
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
        var preserved = new[] { typeof(Organization), typeof(AuditEvent), typeof(Worker) };
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
        Assert.DoesNotContain(typeof(Worker), classified);
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
