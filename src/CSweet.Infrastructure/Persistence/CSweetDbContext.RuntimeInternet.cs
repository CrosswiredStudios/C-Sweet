using CSweet.Domain.Setup;
using CSweet.Application.Setup;
using CSweet.Contracts.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace CSweet.Infrastructure.Persistence;

public sealed partial class CSweetDbContext
{
    private EntityEntry<AgentInstallationGrant>[] RuntimeInternetGrantChanges()
    {
        ChangeTracker.DetectChanges();
        return ChangeTracker.Entries<AgentInstallationGrant>().Where(x =>
            x.State is EntityState.Added or EntityState.Deleted || x.State == EntityState.Modified &&
            (x.Property(y => y.GrantRevision).IsModified || x.Property(y => y.RequiredCapabilitiesJson).IsModified)).ToArray();
    }

    private bool HasRuntimeGrantChanges() => RuntimeInternetGrantChanges().Length > 0;

    private async Task CaptureRuntimeInternetRevocationsAsync(CancellationToken token = default)
    {
        foreach (var entry in RuntimeInternetGrantChanges())
        {
            var grant = entry.Entity;
            var installation = AgentInstallations.Local.SingleOrDefault(x => x.Id == grant.AgentInstallationId) ??
                await AgentInstallations.Include(x => x.PackageVersion).SingleOrDefaultAsync(x => x.Id == grant.AgentInstallationId, token);
            if (installation is null) continue;
            var package = installation.PackageVersion ?? AgentPackageVersions.Local.SingleOrDefault(x => x.Id == installation.PackageVersionId) ??
                await AgentPackageVersions.SingleOrDefaultAsync(x => x.Id == installation.PackageVersionId, token);
            if (package?.ManifestJson.Contains("runtime.internet.public.v1", StringComparison.Ordinal) != true &&
                !grant.RequiredCapabilitiesJson.Contains("runtime.internet.public.v1", StringComparison.Ordinal) &&
                (entry.State == EntityState.Added || !entry.OriginalValues.GetValue<string>(nameof(AgentInstallationGrant.RequiredCapabilitiesJson))
                    .Contains("runtime.internet.public.v1", StringComparison.Ordinal))) continue;
            // Share the runtime-row lock with assignment admission, so a delayed launch cannot
            // create a network assignment between revocation discovery and its atomic commit.
            if (Database.IsNpgsql())
                await AgentRuntimeInstances.FromSqlInterpolated(
                    $"SELECT * FROM \"AgentRuntimeInstances\" WHERE \"AgentInstallationId\" = {grant.AgentInstallationId} FOR UPDATE")
                    .ToListAsync(token);
            var assignments = await ExecutionWorkloadAssignments.Include(x => x.AgentRuntimeInstance)
                .Where(x => x.WorkloadKind == ExecutionWorkloadKind.Runtime && x.AgentRuntimeInstance != null &&
                    x.AgentRuntimeInstance.AgentInstallationId == grant.AgentInstallationId &&
                    (x.Status == ExecutionAssignmentStatus.Pending || x.Status == ExecutionAssignmentStatus.Assigned ||
                     x.Status == ExecutionAssignmentStatus.Starting || x.Status == ExecutionAssignmentStatus.Running))
                .ToListAsync(token);
            var now = DateTimeOffset.UtcNow;
            foreach (var assignment in assignments)
            {
                if (!assignment.IsActive) continue;
                assignment.Status = ExecutionAssignmentStatus.Fenced;
                assignment.FencingEpoch++;
                assignment.LeaseExpiresAt = null;
                assignment.CompletedAt = now;
                assignment.FailureCode = "runtime-internet-grant-changed";
                assignment.SanitizedFailure = "The installation grant changed; restart with current permissions.";
                var runtime = assignment.AgentRuntimeInstance!;
                if (AgentRuntimeInstance.IsActive(runtime.Status)) runtime.TransitionTo(AgentRuntimeStatus.Stopping, now, assignment.SanitizedFailure);
            }
            var employeeId = CoreOrganizationUsers.Local.FirstOrDefault(x => x.AgentInstallationId == installation.Id)?.Id ??
                await CoreOrganizationUsers.Where(x => x.AgentInstallationId == installation.Id).Select(x => x.Id).FirstOrDefaultAsync(token);
            var auditId = AuditMutationId($"runtime-internet:{installation.Id}:{grant.GrantRevision}");
            if (AuditOutbox.Local.Any(x => x.Id == auditId)) continue;
            QueueAudit(new("runtime.internet.grant-changed", Category: "Security", EntityType: nameof(AgentInstallation), EntityId: installation.Id,
                Summary: "The installation internet grant changed; affected runtimes were fenced.",
                MetadataJson: System.Text.Json.JsonSerializer.Serialize(new { grant.GrantRevision,
                    RequiredCapabilities = grant.RequiredCapabilitiesJson, Deleted = entry.State == EntityState.Deleted }),
                OrganizationId: Guid.TryParse(installation.BusinessId, out var org) ? org : null, EventId: auditId,
                Employees: employeeId == Guid.Empty ? [] : [new AuditEmployeeAssociation(employeeId, "Affected")]));
            if (Guid.TryParse(installation.BusinessId, out var organization))
                QueueApplicationRealtimeEvent(organization, null, null, AppRealtimeEvents.EmployeeDirectoryChanged,
                    $"installations/{installation.Id:D}", System.Text.Json.JsonSerializer.Serialize(new EmployeeDirectoryChangedEvent(organization, employeeId, "AccessChanged")), now, ResolveActiveOrganizationRecipients(organization));
        }
    }
}
