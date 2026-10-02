using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Persistence;

public sealed partial class CSweetDbContext
{
    public DbSet<ProjectHealthState> ProjectHealthStates => Set<ProjectHealthState>();
    public DbSet<ProjectHealthSignal> ProjectHealthSignals => Set<ProjectHealthSignal>();
    public DbSet<ProjectIncident> ProjectIncidents => Set<ProjectIncident>();
    public DbSet<ProjectIncidentReceipt> ProjectIncidentReceipts => Set<ProjectIncidentReceipt>();
    public DbSet<ProjectIncidentDelivery> ProjectIncidentDeliveries => Set<ProjectIncidentDelivery>();

    private static void ConfigureProjectHealth(ModelBuilder model)
    {
        model.Entity<ProjectHealthState>(e => {
            e.HasKey(x => x.WorkstreamId);
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasIndex(x => x.NextReviewAt);
        });
        model.Entity<ProjectHealthSignal>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ProcessedAt, x.OccurredAt });
            e.HasIndex(x => new { x.WorkstreamId, x.OccurredAt });
        });
        model.Entity<ProjectIncident>(e => {
            e.HasKey(x => x.Id);
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasIndex(x => new { x.OrganizationId, x.WorkstreamId, x.Fingerprint }).IsUnique().HasFilter("\"Status\" = 'Open'");
            e.HasIndex(x => new { x.Status, x.EscalateAt });
            e.HasIndex(x => new { x.Status, x.ReviewAt });
            e.HasIndex(x => new { x.OrganizationId, x.CurrentRecipientId, x.DetectedAt });
        });
        model.Entity<ProjectIncidentReceipt>().HasKey(x => new { x.IncidentId, x.ActorId, x.IdempotencyKey });
        model.Entity<ProjectIncidentDelivery>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.IncidentId, x.RecipientId, x.Revision }).IsUnique();
            e.HasIndex(x => new { x.DeliveredAt, x.NextAttemptAt });
        });
    }
}
