using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.WebHost.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Persistence;

public sealed partial class CSweetDbContext
{
    private void CaptureWebPreviewEvents()
    {
        ChangeTracker.DetectChanges();
        var finishedTests = ChangeTracker.Entries<WebHostCommandRecord>().Where(x => x.State == EntityState.Modified &&
            x.Entity.Action == "test" && x.Entity.Status is "Completed" or "Cancelled" &&
            x.Property(y => y.Status).OriginalValue != x.Entity.Status).Select(x => x.Entity.PreviewId).ToHashSet();
        foreach (var entry in ChangeTracker.Entries<WebPreviewJobRecord>().Where(x => x.State is EntityState.Added or EntityState.Modified).ToArray())
        {
            // Transport, access and telemetry updates must not wake an agent on every request.
            if (entry.State != EntityState.Added && !finishedTests.Contains(entry.Entity.Id) && !new[] { nameof(WebPreviewJobRecord.Phase), nameof(WebPreviewJobRecord.FailureCode),
                nameof(WebPreviewJobRecord.ExpiresAt), nameof(WebPreviewJobRecord.TeardownConfirmedAt) }
                .Any(name => !Equals(entry.OriginalValues[name], entry.CurrentValues[name]))) continue;
            var job = entry.Entity;
            if (entry.State != EntityState.Added)
                job.Revision = Math.Max(job.Revision, checked(entry.Property(x => x.Revision).OriginalValue + 1));
            var key = $"web-preview:{job.Id:D}:{job.Revision}";
            var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));
            // Repeated SaveChanges after a failed transaction must not add another event.
            if (AgentPlatformEventOutbox.Local.Any(x => x.Id == id)) continue;
            var occurred = job.UpdatedAt == default ? job.CreatedAt : job.UpdatedAt;
            AgentPlatformEventOutbox.Add(new()
            {
                Id = id, OrganizationId = job.OrganizationId, TargetInstallationId = job.InstallationId,
                EventType = WebPreviewEvents.Changed, DataJson = JsonSerializer.Serialize(new PreviewChangedEvent(job.Id, job.Revision), PreviewJson.Options),
                IdempotencyKey = key, Status = AgentPlatformEventOutboxStatus.Pending, OccurredAt = occurred, NextAttemptAt = occurred
            });
        }
    }
}


