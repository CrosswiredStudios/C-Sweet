using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Persistence;

internal static class ProjectIntakeModelConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<ProjectIntake>(e => {
            e.HasKey(x => x.Id); e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasIndex(x => new { x.OrganizationId, x.DeveloperInstallationId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => new { x.DeveloperInstallationId, x.Status, x.UpdatedAt });
            e.Property(x => x.Name).HasMaxLength(160); e.Property(x => x.Goal).HasMaxLength(6000);
            e.Property(x => x.Status).HasMaxLength(40); e.Property(x => x.TicketOwner).HasMaxLength(16);
            e.Property(x => x.IdempotencyKey).HasMaxLength(200); e.Property(x => x.LastChoiceKey).HasMaxLength(200);
            e.Property(x => x.Issue).HasMaxLength(2048);
        });
        model.Entity<ProjectParticipant>(e => {
            e.HasKey(x => new { x.WorkstreamId, x.OrganizationUserId }); e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasIndex(x => new { x.OrganizationId, x.OrganizationUserId, x.RemovedAt });
            e.HasOne<Workstream>().WithMany().HasForeignKey(x => x.WorkstreamId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<OrganizationUser>().WithMany().HasForeignKey(x => x.OrganizationUserId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ProjectDeliveryBinding>(e => {
            e.HasKey(x => x.WorkstreamId); e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasIndex(x => new { x.OrganizationId, x.CreationKey }).IsUnique();
            e.HasIndex(x => x.BoardId).IsUnique(); e.Property(x => x.CreationKey).HasMaxLength(200);
            e.HasOne<Workstream>().WithMany().HasForeignKey(x => x.WorkstreamId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ProjectManagerReservation>(e => {
            e.HasKey(x => x.OrganizationUserId); e.Property(x => x.Revision).IsConcurrencyToken();
        });
        model.Entity<LegacyProjectComputeAuthorization>().HasKey(x => x.EnvironmentId);
        model.Entity<LegacyDevelopmentAuthorization>().HasKey(x => x.WorkItemId);
    }
}
