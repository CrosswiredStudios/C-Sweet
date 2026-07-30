using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Persistence;

internal static class WorkManagementConfigurations
{
    public static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkBoard>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(2048).IsRequired();
            entity.HasIndex(x => new { x.OrganizationId, x.Name });
            entity.HasIndex(x => new { x.OrganizationId, x.IsDefault })
                .IsUnique()
                .HasFilter("\"IsDefault\" = TRUE AND \"ArchivedAt\" IS NULL");
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<OrganizationTeam>()
                .WithMany()
                .HasForeignKey(x => x.TeamId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<Workstream>()
                .WithMany()
                .HasForeignKey(x => x.WorkstreamId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<WorkBoardColumn>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Category).HasConversion<string>().HasMaxLength(24).IsRequired();
            entity.Property(x => x.WipPolicy).HasConversion<string>().HasMaxLength(24).IsRequired();
            entity.HasIndex(x => new { x.BoardId, x.Position }).IsUnique();
            entity.HasOne(x => x.Board)
                .WithMany(x => x.Columns)
                .HasForeignKey(x => x.BoardId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkBoardUserPreference>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.BoardId, x.OrganizationUserId }).IsUnique();
            entity.HasOne(x => x.Board)
                .WithMany()
                .HasForeignKey(x => x.BoardId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<OrganizationUser>()
                .WithMany()
                .HasForeignKey(x => x.OrganizationUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkSprint>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Goal).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
            entity.Property(x => x.CapacityPoints).HasPrecision(8, 2);
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.BoardId, x.Name });
            entity.HasIndex(x => new { x.BoardId, x.Status })
                .IsUnique()
                .HasFilter("\"Status\" = 'Active'");
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Board)
                .WithMany(x => x.Sprints)
                .HasForeignKey(x => x.BoardId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkSprintSnapshot>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SprintName).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Goal).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.CapacityPoints).HasPrecision(8, 2);
            entity.Property(x => x.CommittedPoints).HasPrecision(10, 2);
            entity.Property(x => x.CompletedPoints).HasPrecision(10, 2);
            entity.Property(x => x.ScopeJson).HasColumnType("text").IsRequired();
            entity.HasIndex(x => x.SprintId).IsUnique();
            entity.HasIndex(x => new { x.BoardId, x.CompletedAt });
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<WorkBoard>()
                .WithMany()
                .HasForeignKey(x => x.BoardId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<WorkSprint>()
                .WithMany()
                .HasForeignKey(x => x.SprintId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkSprintMetricPoint>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Reason).HasMaxLength(160).IsRequired();
            entity.Property(x => x.ScopePoints).HasPrecision(10, 2);
            entity.Property(x => x.CompletedPoints).HasPrecision(10, 2);
            entity.Property(x => x.RemainingPoints).HasPrecision(10, 2);
            entity.HasIndex(x => new { x.SprintId, x.OccurredAt });
            entity.HasIndex(x => new { x.BoardId, x.OccurredAt });
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<WorkBoard>()
                .WithMany()
                .HasForeignKey(x => x.BoardId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<WorkSprint>()
                .WithMany()
                .HasForeignKey(x => x.SprintId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkAutomationRule>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.TriggerEventType).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Action).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.BoardId, x.IsEnabled });
            entity.HasIndex(x => x.AutomationIdentityId).IsUnique();
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Board)
                .WithMany(x => x.AutomationRules)
                .HasForeignKey(x => x.BoardId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<WorkBoardColumn>()
                .WithMany()
                .HasForeignKey(x => x.ConditionColumnId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<WorkBoardColumn>()
                .WithMany()
                .HasForeignKey(x => x.TargetColumnId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkAutomationExecution>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
            entity.Property(x => x.RequiredAction).HasMaxLength(160).IsRequired();
            entity.Property(x => x.ErrorCode).HasMaxLength(160);
            entity.Property(x => x.ErrorMessage).HasMaxLength(2048);
            entity.HasIndex(x => new { x.RuleId, x.SourceActivityId }).IsUnique();
            entity.HasIndex(x => new { x.BoardId, x.CompletedAt });
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<WorkBoard>()
                .WithMany()
                .HasForeignKey(x => x.BoardId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Rule)
                .WithMany()
                .HasForeignKey(x => x.RuleId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<WorkItemActivity>()
                .WithMany()
                .HasForeignKey(x => x.SourceActivityId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ScopedActionGrant>()
                .WithMany()
                .HasForeignKey(x => x.AuthorizingGrantId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<WorkSprintMutationReceipt>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ActorKind).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.Action).HasMaxLength(160).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.ResultJson).HasColumnType("text").IsRequired();
            entity.HasIndex(x => new
            {
                x.ActorKind,
                x.ActorSubjectId,
                x.Action,
                x.IdempotencyKey
            }).IsUnique();
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ScopedActionGrant>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SubjectKind).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.Action).HasMaxLength(160).IsRequired();
            entity.Property(x => x.ScopeKind).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.GrantedBySubjectKind).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new
            {
                x.OrganizationId,
                x.SubjectKind,
                x.SubjectId,
                x.Action,
                x.ScopeKind,
                x.ScopeId
            });
            entity.HasIndex(x => new { x.OrganizationId, x.Action, x.RevokedAt });
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.ParentGrant)
                .WithMany()
                .HasForeignKey(x => x.ParentGrantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkItemMutationReceipt>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Action).HasMaxLength(160).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.ResultJson).HasColumnType("text").IsRequired();
            entity.HasIndex(x => new
            {
                x.AgentInstallationId,
                x.Action,
                x.IdempotencyKey
            }).IsUnique();
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<CSweet.Domain.Setup.AgentInstallation>()
                .WithMany()
                .HasForeignKey(x => x.AgentInstallationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkItemComment>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AuthorKind).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.AuthorDisplayName).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Body).HasMaxLength(8192).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.HasIndex(x => new
            {
                x.AuthorKind,
                x.AuthorSubjectId,
                x.WorkItemId,
                x.IdempotencyKey
            }).IsUnique();
            entity.HasIndex(x => new { x.WorkItemId, x.CreatedAt });
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<WorkTask>()
                .WithMany()
                .HasForeignKey(x => x.WorkItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkItemActivity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EventType).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Action).HasMaxLength(160).IsRequired();
            entity.Property(x => x.ActorKind).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.ActorDisplayName).HasMaxLength(160).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160);
            entity.Property(x => x.DataJson).HasColumnType("text").IsRequired();
            entity.HasIndex(x => new { x.WorkItemId, x.OccurredAt });
            entity.HasIndex(x => new { x.BoardId, x.OccurredAt });
            entity.HasIndex(x => new { x.BoardId, x.EventType, x.OccurredAt });
            entity.HasIndex(x => new
            {
                x.ActorKind,
                x.ActorSubjectId,
                x.Action,
                x.IdempotencyKey
            }).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<WorkTask>()
                .WithMany()
                .HasForeignKey(x => x.WorkItemId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<WorkBoard>()
                .WithMany()
                .HasForeignKey(x => x.BoardId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ScopedActionGrant>()
                .WithMany()
                .HasForeignKey(x => x.AuthorizingGrantId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
