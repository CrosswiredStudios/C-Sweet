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
            entity.Property<long>("MemoryAudienceRevision").HasDefaultValue(1L).ValueGeneratedOnAddOrUpdate();
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.Key).HasMaxLength(12).IsRequired();
            entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(24).IsRequired();
            entity.Property(x => x.ProfileKey).HasMaxLength(200).IsRequired();
            entity.HasIndex(x => new { x.OrganizationId, x.Key }).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.Name });
            entity.HasIndex(x => x.OwnerOrganizationUserId)
                .IsUnique()
                .HasFilter("\"OwnerOrganizationUserId\" IS NOT NULL");
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
            entity.HasOne<OrganizationUser>()
                .WithMany()
                .HasForeignKey(x => x.ManagerOrganizationUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<OrganizationUser>()
                .WithMany()
                .HasForeignKey(x => x.OwnerOrganizationUserId)
                .OnDelete(DeleteBehavior.Restrict);
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
            entity.HasIndex(x => new { x.BoardId, x.Sequence })
                .IsUnique()
                .HasFilter("\"Sequence\" IS NOT NULL");
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

        modelBuilder.Entity<WorkItemDependency>(entity =>
        {
            entity.HasKey(x => new { x.WorkItemId, x.DependsOnWorkItemId });
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_WorkItemDependencies_NotSelf",
                "\"WorkItemId\" <> \"DependsOnWorkItemId\""));
            entity.HasOne(x => x.WorkItem)
                .WithMany(x => x.Dependencies)
                .HasForeignKey(x => x.WorkItemId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.DependsOnWorkItem)
                .WithMany(x => x.Dependents)
                .HasForeignKey(x => x.DependsOnWorkItemId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.DependsOnWorkItemId);
        });

        modelBuilder.Entity<WorkItemApproval>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PolicyKey).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.RequiredRoleCategory).HasMaxLength(160).IsRequired();
            entity.Property(x => x.ArtifactDigest).HasMaxLength(128);
            entity.Property(x => x.Rationale).HasMaxLength(4096);
            entity.Property(x => x.ManagerWaiverSource).HasMaxLength(300);
            entity.HasIndex(x => new { x.WorkItemId, x.PolicyKey }).IsUnique();
            entity.HasIndex(x => new { x.BoardId, x.Status, x.PolicyKey });
            entity.HasOne(x => x.WorkItem)
                .WithMany(x => x.Approvals)
                .HasForeignKey(x => x.WorkItemId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkQualityRun>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SourceCommitSha).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Verdict).HasMaxLength(24).IsRequired();
            entity.Property(x => x.ResultJson).HasColumnType("text").IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.HasIndex(x => new
            {
                x.QualityInstallationId,
                x.WorkItemId,
                x.IdempotencyKey
            }).IsUnique();
            entity.HasIndex(x => new { x.WorkItemId, x.QualityCycle });
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<WorkBoard>()
                .WithMany()
                .HasForeignKey(x => x.BoardId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<WorkTask>()
                .WithMany()
                .HasForeignKey(x => x.WorkItemId)
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
            entity.Property(x => x.Kind).HasMaxLength(80);
            entity.Property(x => x.CausationId).HasMaxLength(160);
            entity.Property(x => x.ArtifactDigest).HasMaxLength(128);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.HasIndex(x => new
            {
                x.AuthorKind,
                x.AuthorSubjectId,
                x.WorkItemId,
                x.IdempotencyKey
            }).IsUnique();
            entity.HasIndex(x => new { x.WorkItemId, x.CreatedAt });
            entity.HasIndex(x => new { x.CoordinationSessionId, x.Kind });
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

        ConfigureOrchestration(modelBuilder);
    }

    private static void ConfigureOrchestration(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkOrchestrationPolicy>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.HasIndex(x => x.BoardId).IsUnique();
            entity.HasOne(x => x.Board).WithMany(x => x.OrchestrationPolicies)
                .HasForeignKey(x => x.BoardId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkOrchestrationPolicyRevision>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.InitialStageKey).HasMaxLength(64).IsRequired();
            entity.Property(x => x.MergeMode).HasMaxLength(32).IsRequired();
            entity.HasIndex(x => new { x.PolicyId, x.Revision }).IsUnique();
            entity.HasIndex(x => new { x.BoardId, x.IsPublished });
            entity.HasOne(x => x.Policy).WithMany(x => x.Revisions)
                .HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkOrchestrationStage>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Key).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Type).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.Instructions).HasMaxLength(16384).IsRequired();
            entity.Property(x => x.InputSchemaJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.OutputSchemaJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.PlatformAction).HasMaxLength(160);
            entity.HasIndex(x => new { x.PolicyRevisionId, x.Key }).IsUnique();
            entity.HasOne(x => x.PolicyRevision).WithMany(x => x.Stages)
                .HasForeignKey(x => x.PolicyRevisionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<WorkBoardColumn>().WithMany().HasForeignKey(x => x.ColumnId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkOrchestrationTransition>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FromStageKey).HasMaxLength(64).IsRequired();
            entity.Property(x => x.OutcomeCode).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ToStageKey).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => new { x.PolicyRevisionId, x.FromStageKey, x.OutcomeCode }).IsUnique();
            entity.HasOne(x => x.PolicyRevision).WithMany(x => x.Transitions)
                .HasForeignKey(x => x.PolicyRevisionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkItemStageAssignment>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.StageKey).HasMaxLength(64).IsRequired();
            entity.Property(x => x.PrincipalKind).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.PlatformAction).HasMaxLength(160);
            entity.Property(x => x.RequirementsJson).HasColumnType("jsonb");
            entity.Property(x => x.SelectionEvidenceJson).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.WorkItemId, x.StageKey }).IsUnique();
            entity.HasOne(x => x.WorkItem).WithMany(x => x.StageAssignments)
                .HasForeignKey(x => x.WorkItemId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<OrganizationUser>().WithMany().HasForeignKey(x => x.OrganizationUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CSweet.Domain.Setup.AgentInstallation>().WithMany().HasForeignKey(x => x.AgentInstallationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkSprintExecution>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
            entity.Property(x => x.PolicySnapshotJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.AssignmentSnapshotJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => x.SprintId).IsUnique();
            entity.HasIndex(x => x.BoardId)
                .IsUnique().HasFilter("\"Status\" IN ('Active', 'Paused')");
            entity.HasOne<WorkSprint>().WithMany().HasForeignKey(x => x.SprintId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<WorkOrchestrationPolicyRevision>().WithMany().HasForeignKey(x => x.PolicyRevisionId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkItemExecution>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ItemIdentifier).HasMaxLength(32).IsRequired();
            entity.Property(x => x.CurrentStageKey).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.BlockedReason).HasMaxLength(4096);
            entity.HasIndex(x => new { x.SprintExecutionId, x.WorkItemId }).IsUnique();
            entity.HasOne(x => x.SprintExecution).WithMany(x => x.Items)
                .HasForeignKey(x => x.SprintExecutionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.WorkItem).WithMany().HasForeignKey(x => x.WorkItemId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkStageExecution>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.StageKey).HasMaxLength(64).IsRequired();
            entity.Property(x => x.StageType).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.PrincipalKind).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.PlatformAction).HasMaxLength(160);
            entity.Property(x => x.LastOutcomeCode).HasMaxLength(64);
            entity.Property(x => x.LastSummary).HasMaxLength(4096);
            entity.Property(x => x.LastError).HasMaxLength(4096);
            entity.HasIndex(x => new { x.ItemExecutionId, x.StageKey, x.Traversal }).IsUnique();
            entity.HasOne(x => x.ItemExecution).WithMany(x => x.Stages)
                .HasForeignKey(x => x.ItemExecutionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.DeliveryExecution).WithMany(x => x.Stages)
                .HasForeignKey(x => x.DeliveryExecutionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.DeliveryExecutionId, x.StageKey, x.Traversal }).IsUnique();
            entity.ToTable(t => t.HasCheckConstraint("CK_WorkStageExecution_OneOwner",
                "(\"ItemExecutionId\" IS NULL) <> (\"DeliveryExecutionId\" IS NULL)"));
        });

        modelBuilder.Entity<WorkDeliveryPlan>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.Property(x => x.EpicItemIdsJson).HasColumnType("jsonb");
            entity.Property(x => x.BranchesJson).HasColumnType("jsonb");
            entity.Property(x => x.ScopesJson).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.OrganizationId, x.WorkstreamId });
            entity.HasOne<Workstream>().WithMany().HasForeignKey(x => x.WorkstreamId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<OrganizationUser>().WithMany().HasForeignKey(x => x.ManagerOrganizationUserId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<WorkTaskIntegrationReceipt>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.PublicationId).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.WorkItemId, x.CreatedAt });
            entity.Property(x => x.SourceCommitSha).HasMaxLength(64).IsRequired();
            entity.Property(x => x.TargetCommitSha).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CandidateCommitSha).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Error).HasMaxLength(4096);
            entity.HasOne<WorkDeliveryPlan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<WorkItemExecution>().WithMany().HasForeignKey(x => x.ItemExecutionId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<WorkDeliveryFinding>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CandidateDigest).HasMaxLength(64);
            entity.Property(x => x.FindingDigest).HasMaxLength(64);
            entity.Property(x => x.Status).HasMaxLength(24);
            entity.Property(x => x.RemediationTaskIdsJson).HasColumnType("jsonb");
            entity.HasOne(x => x.Plan).WithMany(x => x.Findings).HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.ExecutionId, x.CandidateDigest, x.FindingDigest }).IsUnique();
        });
        modelBuilder.Entity<WorkDeliveryExecution>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Scope).HasMaxLength(24);
            entity.Property(x => x.Status).HasMaxLength(32);
            entity.Property(x => x.CurrentStageKey).HasMaxLength(64);
            entity.Property(x => x.BlockedReason).HasMaxLength(4096);
            entity.Property(x => x.CandidateJson).HasColumnType("jsonb");
            entity.Property(x => x.AcceptanceJson).HasColumnType("jsonb");
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasOne(x => x.Plan).WithMany(x => x.Executions).HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<WorkTask>().WithMany().HasForeignKey(x => x.WorkItemId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.PlanId, x.ScopeRevision, x.Scope, x.WorkItemId }).IsUnique().AreNullsDistinct(false);
        });
        modelBuilder.Entity<WorkDeliveryPromotion>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SourceCommitSha).HasMaxLength(64);
            entity.Property(x => x.TargetCommitSha).HasMaxLength(64);
            entity.Property(x => x.MergeCommitSha).HasMaxLength(64);
            entity.Property(x => x.Status).HasMaxLength(32);
            entity.Property(x => x.Error).HasMaxLength(4096);
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasOne(x => x.Execution).WithMany(x => x.Promotions).HasForeignKey(x => x.ExecutionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.ExecutionId, x.RepositoryId }).IsUnique();
        });
        modelBuilder.Entity<WorkDeliveryMutationReceipt>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(200);
            entity.Property(x => x.RequestDigest).HasMaxLength(64);
            entity.Property(x => x.Operation).HasMaxLength(32);
            entity.HasIndex(x => new { x.OrganizationId, x.IdempotencyKey }).IsUnique();
        });

        modelBuilder.Entity<WorkExecutionAttempt>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
            entity.Property(x => x.ResultJson).HasColumnType("jsonb");
            entity.Property(x => x.ErrorCategory).HasMaxLength(80);
            entity.Property(x => x.ErrorMessage).HasMaxLength(4096);
            entity.HasIndex(x => new { x.StageExecutionId, x.Attempt }).IsUnique();
            entity.HasIndex(x => x.StageExecutionId).IsUnique()
                .HasFilter("\"Status\" IN ('Pending', 'Running')");
            entity.HasIndex(x => x.AgentWorkItemId).IsUnique().HasFilter("\"AgentWorkItemId\" IS NOT NULL");
            entity.HasOne(x => x.StageExecution).WithMany(x => x.Attempts)
                .HasForeignKey(x => x.StageExecutionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.AgentWorkItem).WithMany().HasForeignKey(x => x.AgentWorkItemId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkOrchestrationEvent>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EventType).HasMaxLength(160).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160);
            entity.Property(x => x.DataJson).HasColumnType("jsonb").IsRequired();
            entity.HasIndex(x => new { x.SprintExecutionId, x.OccurredAt });
            entity.HasIndex(x => new { x.BoardId, x.OccurredAt });
            entity.HasIndex(x => new { x.OrganizationId, x.EventType, x.IdempotencyKey })
                .IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
        });
    }
}
