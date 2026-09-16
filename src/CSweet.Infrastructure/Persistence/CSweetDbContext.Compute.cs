using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Domain.Compute;
using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Persistence;

public sealed partial class CSweetDbContext
{
    public DbSet<ComputeEnvironment> ComputeEnvironments => Set<ComputeEnvironment>();
    public DbSet<ComputeOperation> ComputeOperations => Set<ComputeOperation>();
    public DbSet<ComputeProviderWake> ComputeProviderWakes => Set<ComputeProviderWake>();
    public DbSet<ComputeAdmission> ComputeAdmissions => Set<ComputeAdmission>();
    public DbSet<ComputeRequestReceipt> ComputeRequestReceipts => Set<ComputeRequestReceipt>();

    // Physical table retained for compatibility; this is the shared audit delivery queue.
    public DbSet<ComputeAuditOutbox> AuditOutbox => Set<ComputeAuditOutbox>();

    public DbSet<ComputeNodeRegistration> ComputeNodes => Set<ComputeNodeRegistration>();
    public DbSet<ComputeTemplateRegistration> ComputeTemplates => Set<ComputeTemplateRegistration>();
    public DbSet<ComputeTemplatePlacement> ComputeTemplatePlacements => Set<ComputeTemplatePlacement>();

    private void CaptureComputeEvents()
    {
        ChangeTracker.DetectChanges();
        foreach (var operation in ChangeTracker.Entries<ComputeOperation>().Where(x => x.State == EntityState.Added).Select(x => x.Entity).ToArray())
        {
            var environment = ComputeEnvironments.Local.SingleOrDefault(x => x.Id == operation.EnvironmentId)
                ?? throw new InvalidOperationException("New compute work requires its tracked environment for atomic provider notification.");
            if (environment.OrganizationId != operation.OrganizationId || environment.ProviderNodeId is not { } nodeId ||
                nodeId == Guid.Empty || string.IsNullOrWhiteSpace(environment.ProviderId))
                throw new InvalidOperationException("New compute work requires complete provider placement.");
            if (ComputeProviderWakes.Local.Any(x => x.Id == operation.Id)) continue;
            ComputeProviderWakes.Add(new()
            {
                Id = operation.Id, OperationId = operation.Id, OrganizationId = operation.OrganizationId,
                NodeId = nodeId, ProviderId = environment.ProviderId,
                CreatedAt = operation.CreatedAt, NextAttemptAt = operation.NextAttemptAt
            });
        }
        foreach (var entry in ChangeTracker.Entries<ComputeEnvironment>().Where(x => x.State is EntityState.Added or EntityState.Modified).ToArray())
        {
            if (entry.State != EntityState.Added && !new[] { nameof(ComputeEnvironment.DesiredState),
                nameof(ComputeEnvironment.State), nameof(ComputeEnvironment.Generation), nameof(ComputeEnvironment.LastFailureCode),
                nameof(ComputeEnvironment.LeaseExpiresAt), nameof(ComputeEnvironment.TeardownConfirmedAt) }
                .Any(name => !Equals(entry.OriginalValues[name], entry.CurrentValues[name]))) continue;
            var environment = entry.Entity;
            if (entry.State != EntityState.Added)
                environment.Revision = Math.Max(environment.Revision, checked(entry.Property(x => x.Revision).OriginalValue + 1));
            var key = $"compute:{environment.Id:D}:{environment.Revision}";
            var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));
            if (AgentPlatformEventOutbox.Local.Any(x => x.Id == id)) continue;
            AgentPlatformEventOutbox.Add(new()
            {
                Id = id, OrganizationId = environment.OrganizationId, TargetInstallationId = environment.InstallationId,
                EventType = "com.csweet.compute.changed.v1",
                DataJson = JsonSerializer.Serialize(new { environmentId = environment.Id, revision = environment.Revision }),
                IdempotencyKey = key, Status = AgentPlatformEventOutboxStatus.Pending,
                OccurredAt = environment.UpdatedAt, NextAttemptAt = environment.UpdatedAt
            });
        }
    }
}
