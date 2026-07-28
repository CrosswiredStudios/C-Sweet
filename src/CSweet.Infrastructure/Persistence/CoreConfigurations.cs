using CSweet.Domain.Core;
using CSweet.Domain.Communications;
using CSweet.Infrastructure.Auth;
using CSweet.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CSweet.Infrastructure.Persistence;

internal static class CoreConfigurations
{
    public static void Apply(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Organization>(ConfigureCoreOrganization);
        modelBuilder.Entity<OrganizationUser>(ConfigureOrganizationUser);
        modelBuilder.Entity<Role>(ConfigureRole);
        modelBuilder.Entity<StrategicObjective>(ConfigureStrategicObjective);
        modelBuilder.Entity<Worker>(ConfigureWorker);
        modelBuilder.Entity<WorkTask>(ConfigureWorkTask);
        modelBuilder.Entity<TaskRun>(ConfigureTaskRun);
        modelBuilder.Entity<Artifact>(ConfigureArtifact);
        modelBuilder.Entity<Approval>(ConfigureApproval);
        modelBuilder.Entity<Conversation>(ConfigureConversation);
        modelBuilder.Entity<ConversationParticipant>(ConfigureConversationParticipant);
        modelBuilder.Entity<ConversationMessage>(ConfigureConversationMessage);
        modelBuilder.Entity<SuggestedUserAction>(ConfigureSuggestedUserAction);
        modelBuilder.Entity<ChatTurn>(ConfigureChatTurn);
        modelBuilder.Entity<ChatTurnTraceEvent>(ConfigureChatTurnTraceEvent);
        modelBuilder.Entity<ExecutiveDecision>(ConfigureExecutiveDecision);
        modelBuilder.Entity<MemoryCaptureOutboxItem>(ConfigureMemoryCaptureOutbox);
        modelBuilder.Entity<AgentMemoryNamespaceRegistration>(ConfigureAgentMemoryNamespace);
        modelBuilder.Entity<AgentMemoryRecallUse>(ConfigureAgentMemoryRecallUse);
        modelBuilder.Entity<CommunicationConnection>(ConfigureCommunicationConnection);
        modelBuilder.Entity<ManagedExternalResource>(ConfigureManagedExternalResource);
        modelBuilder.Entity<ExternalIdentityLink>(ConfigureExternalIdentityLink);
        modelBuilder.Entity<ExternalIdentityLinkCode>(ConfigureExternalIdentityLinkCode);
        modelBuilder.Entity<ExternalMessageReference>(ConfigureExternalMessageReference);
        modelBuilder.Entity<CommunicationDelivery>(ConfigureCommunicationDelivery);
        modelBuilder.Entity<CommunicationIngressReceipt>(ConfigureCommunicationIngressReceipt);
        modelBuilder.Entity<ExternalIdentity>(ConfigureExternalIdentity);
        modelBuilder.Entity<UserNotification>(ConfigureUserNotification);
        modelBuilder.Entity<NotificationPreference>(ConfigureNotificationPreference);
        modelBuilder.Entity<CommunicationEventOutboxItem>(ConfigureCommunicationEventOutbox);
        modelBuilder.Entity<AgentOnboardingEventOutboxItem>(ConfigureAgentOnboardingEventOutbox);
        modelBuilder.Entity<ApplicationRealtimeOutboxItem>(ConfigureApplicationRealtimeOutbox);
        modelBuilder.Entity<AgentPlatformEventOutboxItem>(ConfigureAgentPlatformEventOutbox);
        ConfigureWorkforcePlatform(modelBuilder);
    }

    static void ConfigureOrganizationUser(EntityTypeBuilder<OrganizationUser> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.DisplayName).HasMaxLength(160).IsRequired();
        entity.Property(x => x.Email).HasMaxLength(256);
        entity.Property(x => x.EmployeeType).HasConversion<string>().HasMaxLength(16).IsRequired();
        entity.Property(x => x.PermissionLevel).HasConversion<string>().HasMaxLength(16).IsRequired();

        entity.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(x => x.ApplicationUserId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasOne(x => x.Organization)
            .WithMany()
            .HasForeignKey(x => x.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(x => x.ReportsToOrganizationUser)
            .WithMany()
            .HasForeignKey(x => x.ReportsToOrganizationUserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_CoreOrganizationUsers_Manager");

        entity.HasOne(x => x.Role)
            .WithMany()
            .HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasOne(x => x.Worker)
            .WithMany()
            .HasForeignKey(x => x.WorkerId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasOne(x => x.AgentInstallation)
            .WithMany()
            .HasForeignKey(x => x.AgentInstallationId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(x => x.AgentInstallationId)
            .IsUnique()
            .HasFilter("\"AgentInstallationId\" IS NOT NULL");
        entity.HasIndex(x => new { x.OrganizationId, x.ApplicationUserId })
            .IsUnique()
            .HasFilter("\"ApplicationUserId\" IS NOT NULL");
    }

    static void ConfigureCoreOrganization(EntityTypeBuilder<Organization> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Name).HasMaxLength(256).IsRequired();
        entity.Property(x => x.Industry).HasMaxLength(160);
        entity.Property(x => x.Mission).HasMaxLength(4096);
        entity.Property(x => x.Stage).HasMaxLength(80);
        entity.Property(x => x.PrimaryGoal).HasMaxLength(2048);
        entity.Property(x => x.ConstraintsJson).HasMaxLength(65536);
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
    }

    static void ConfigureWorkforcePlatform(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BusinessProfile>(entity =>
        {
            entity.HasKey(x => x.Id); entity.HasIndex(x => x.OrganizationId).IsUnique();
            entity.Property(x => x.BusinessType).HasMaxLength(160); entity.Property(x => x.Description).HasMaxLength(8192);
            entity.Property(x => x.TargetCustomersJson).HasColumnType("jsonb"); entity.Property(x => x.OfferingsJson).HasColumnType("jsonb");
            entity.Property(x => x.JurisdictionsJson).HasColumnType("jsonb"); entity.Property(x => x.ToolsJson).HasColumnType("jsonb");
            entity.Property(x => x.ProvenanceJson).HasColumnType("jsonb"); entity.Property(x => x.TimeZone).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasOne(x => x.Organization).WithOne().HasForeignKey<BusinessProfile>(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<FinancialOperatingProfile>(entity =>
        {
            entity.HasKey(x => x.Id); entity.HasIndex(x => x.OrganizationId).IsUnique(); entity.Property(x => x.BaseCurrency).HasMaxLength(8).IsRequired();
            entity.Property(x => x.RoutingPreference).HasMaxLength(40).IsRequired(); entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasOne(x => x.Organization).WithOne().HasForeignKey<FinancialOperatingProfile>(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<BusinessDiscoveryAssessment>(entity =>
        {
            entity.HasKey(x => x.Id); entity.HasIndex(x => x.OrganizationId).IsUnique(); entity.Property(x => x.ConfirmedFactsJson).HasColumnType("jsonb");
            entity.Property(x => x.AssumptionsJson).HasColumnType("jsonb"); entity.Property(x => x.MissingQuestionsJson).HasColumnType("jsonb");
            entity.Property(x => x.SelectedPatternsJson).HasColumnType("jsonb"); entity.Property(x => x.NextQuestion).HasMaxLength(2048); entity.Property(x => x.Revision).IsConcurrencyToken();
        });
        modelBuilder.Entity<LeadershipAssignment>(entity =>
        {
            entity.HasKey(x => x.Id); entity.Property(x => x.PositionKey).HasMaxLength(80).IsRequired();
            entity.HasIndex(x => new { x.OrganizationId, x.PositionKey }).IsUnique().HasFilter("\"EndsAt\" IS NULL");
            entity.HasOne<OrganizationUser>().WithMany().HasForeignKey(x => x.OrganizationUserId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<Workstream>(entity =>
        {
            entity.HasKey(x => x.Id); entity.Property(x => x.Name).HasMaxLength(512).IsRequired(); entity.Property(x => x.Outcome).HasMaxLength(8192).IsRequired();
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_Workstreams_ExecutionRequiresManager",
                "\"Status\" NOT IN ('Approved', 'Active') OR \"AccountableManagerOrganizationUserId\" IS NOT NULL"));
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24).IsRequired(); entity.Property(x => x.LifecycleStage).HasMaxLength(80).IsRequired();
            entity.Property(x => x.ManagerTitle).HasMaxLength(160).IsRequired(); entity.Property(x => x.SuccessCriteriaJson).HasColumnType("jsonb");
            entity.Property(x => x.RequiredCapabilitiesJson).HasColumnType("jsonb"); entity.Property(x => x.RisksJson).HasColumnType("jsonb"); entity.Property(x => x.BudgetCurrency).HasMaxLength(8);
            entity.HasIndex(x => new { x.OrganizationId, x.Status });
        });
        modelBuilder.Entity<ActionProposal>(entity =>
        {
            entity.HasKey(x => x.Id); entity.Property(x => x.ActionType).HasMaxLength(160).IsRequired(); entity.Property(x => x.Summary).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.PayloadJson).HasColumnType("jsonb"); entity.Property(x => x.RiskClass).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24).IsRequired(); entity.Property(x => x.IdempotencyKey).HasMaxLength(256).IsRequired();
            entity.HasIndex(x => new { x.OrganizationId, x.IdempotencyKey }).IsUnique();
        });
        modelBuilder.Entity<Budget>(entity =>
        {
            entity.HasKey(x => x.Id); entity.Property(x => x.ScopeType).HasConversion<string>().HasMaxLength(24).IsRequired(); entity.Property(x => x.Currency).HasMaxLength(8).IsRequired();
            entity.HasIndex(x => new { x.OrganizationId, x.ScopeType, x.ScopeId, x.PeriodStart, x.PeriodEnd });
        });
        modelBuilder.Entity<BudgetReservation>(entity =>
        {
            entity.HasKey(x => x.Id); entity.Property(x => x.Currency).HasMaxLength(8).IsRequired(); entity.Property(x => x.Purpose).HasMaxLength(1024).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24).IsRequired(); entity.Property(x => x.IdempotencyKey).HasMaxLength(256).IsRequired();
            entity.HasIndex(x => new { x.OrganizationId, x.IdempotencyKey }).IsUnique(); entity.HasOne<Budget>().WithMany().HasForeignKey(x => x.BudgetId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ManagementCycle>(entity =>
        {
            entity.HasKey(x => x.Id); entity.HasIndex(x => x.OrganizationId).IsUnique(); entity.Property(x => x.TimeZone).HasMaxLength(128).IsRequired();
            entity.Property(x => x.DailyCheckInLocalTime).HasMaxLength(5).IsRequired(); entity.Property(x => x.DailyDueLocalTime).HasMaxLength(5).IsRequired();
            entity.Property(x => x.WeeklyReviewDay).HasMaxLength(16).IsRequired(); entity.Property(x => x.WeeklyReviewLocalTime).HasMaxLength(5).IsRequired();
            entity.Property(x => x.QuietHoursStart).HasMaxLength(5).IsRequired(); entity.Property(x => x.QuietHoursEnd).HasMaxLength(5).IsRequired();
            entity.Property(x => x.ExecutiveBriefingCadence).HasMaxLength(16).IsRequired();
            entity.Property(x => x.ExecutiveBriefingWeeklyDay).HasMaxLength(16).IsRequired();
            entity.Property(x => x.ExecutiveBriefingLocalTime).HasMaxLength(5).IsRequired();
        });
        modelBuilder.Entity<BusinessPattern>(entity =>
        {
            entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.PatternKey, x.Version }).IsUnique();
            entity.Property(x => x.PatternKey).HasMaxLength(160).IsRequired(); entity.Property(x => x.Name).HasMaxLength(256).IsRequired();
            entity.Property(x => x.LifecycleStage).HasMaxLength(80).IsRequired(); entity.Property(x => x.Provenance).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ApplicableBusinessTypesJson).HasColumnType("jsonb"); entity.Property(x => x.JurisdictionsJson).HasColumnType("jsonb");
            entity.Property(x => x.WorkstreamsJson).HasColumnType("jsonb"); entity.Property(x => x.TeamRecipeJson).HasColumnType("jsonb");
            entity.Property(x => x.RisksJson).HasColumnType("jsonb"); entity.Property(x => x.FinancialConsiderationsJson).HasColumnType("jsonb");
        });
        modelBuilder.Entity<WorkforcePlan>(entity =>
        {
            entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.OrganizationId, x.Status }); entity.Property(x => x.Objective).HasMaxLength(2048).IsRequired();
            entity.HasIndex(x => new { x.OrganizationId, x.RequestingInstallationId, x.IdempotencyKey }).IsUnique();
            entity.Property(x => x.Title).HasMaxLength(256).IsRequired(); entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Priority).HasDefaultValue(50).IsRequired();
            entity.Property(x => x.RoleKey).HasMaxLength(160);
            entity.Property(x => x.Headcount).HasDefaultValue(1).IsRequired();
            entity.HasIndex(x => new { x.OrganizationId, x.RequestingInstallationId, x.RoleKey })
                .HasFilter("\"RoleKey\" IS NOT NULL");
            entity.Property(x => x.AssignmentsJson).HasColumnType("jsonb"); entity.Property(x => x.RejectedAlternativesJson).HasColumnType("jsonb");
            entity.Property(x => x.Currency).HasMaxLength(8); entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        });
        modelBuilder.Entity<WorkforceCandidate>(entity =>
        {
            entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.OrganizationId, x.Source, x.ExternalCandidateId });
            entity.Property(x => x.Source).HasMaxLength(80).IsRequired(); entity.Property(x => x.ExternalCandidateId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.DisplayName).HasMaxLength(256).IsRequired(); entity.Property(x => x.CapabilitiesJson).HasColumnType("jsonb");
            entity.Property(x => x.ExplanationJson).HasColumnType("jsonb"); entity.Property(x => x.Currency).HasMaxLength(8);
        });
        modelBuilder.Entity<ResourceNeed>(entity =>
        {
            entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.OrganizationId, x.Status }); entity.Property(x => x.RequiredCapabilitiesJson).HasColumnType("jsonb");
            entity.Property(x => x.BusinessOutcome).HasMaxLength(2048).IsRequired(); entity.Property(x => x.Urgency).HasMaxLength(32).IsRequired(); entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
        });
        modelBuilder.Entity<StaffingActionProposal>(entity =>
        {
            entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.OrganizationId, x.Status }); entity.Property(x => x.ActionType).HasMaxLength(80).IsRequired();
            entity.HasIndex(x => new { x.OrganizationId, x.RequestingInstallationId, x.IdempotencyKey }).IsUnique(); entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.CandidateSource).HasMaxLength(80).IsRequired(); entity.Property(x => x.CandidateId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.PayloadJson).HasColumnType("jsonb"); entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        });
        modelBuilder.Entity<Responsibility>(entity =>
        {
            entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.OrganizationId, x.OrganizationUserId, x.Status });
            entity.Property(x => x.Title).HasMaxLength(256).IsRequired(); entity.Property(x => x.Outcome).HasMaxLength(2048).IsRequired(); entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
        });
        modelBuilder.Entity<ManagementCheckInRequestRecord>(entity =>
        {
            entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.OrganizationId, x.Status, x.DueAt });
            entity.Property(x => x.CheckInType).HasMaxLength(64).IsRequired(); entity.Property(x => x.TopicsJson).HasColumnType("jsonb"); entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(256); entity.Property(x => x.TriggerType).HasMaxLength(32);
            entity.Property(x => x.FailureCode).HasMaxLength(80); entity.Property(x => x.FailureMessage).HasMaxLength(2048);
            entity.HasIndex(x => new { x.OrganizationId, x.IdempotencyKey }).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
        });
        modelBuilder.Entity<ManagementStatusReportRecord>(entity =>
        {
            entity.HasKey(x => x.Id); entity.HasIndex(x => x.ManagementCheckInRequestId).IsUnique(); entity.Property(x => x.Summary).HasMaxLength(4096).IsRequired();
            entity.Property(x => x.OutcomesJson).HasColumnType("jsonb"); entity.Property(x => x.BlockersJson).HasColumnType("jsonb");
            entity.Property(x => x.RisksJson).HasColumnType("jsonb"); entity.Property(x => x.DecisionsJson).HasColumnType("jsonb");
            entity.Property(x => x.Markdown).HasMaxLength(8192); entity.Property(x => x.ImmediateActionsJson).HasColumnType("jsonb");
            entity.Property(x => x.ConversationTopicsJson).HasColumnType("jsonb"); entity.Property(x => x.Severity).HasMaxLength(16).IsRequired();
        });
        modelBuilder.Entity<ExecutiveBriefingDeliveryRecord>(entity =>
        {
            entity.HasKey(x => x.Id); entity.HasIndex(x => x.ManagementCheckInRequestId).IsUnique();
            entity.HasIndex(x => new { x.Status, x.LastAttemptAt }); entity.Property(x => x.Channel).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired(); entity.Property(x => x.PayloadJson).HasColumnType("jsonb");
            entity.Property(x => x.FailureCode).HasMaxLength(80); entity.Property(x => x.FailureMessage).HasMaxLength(2048);
            entity.HasOne<ManagementCheckInRequestRecord>().WithOne().HasForeignKey<ExecutiveBriefingDeliveryRecord>(x => x.ManagementCheckInRequestId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ManagementStatusReportRecord>().WithOne().HasForeignKey<ExecutiveBriefingDeliveryRecord>(x => x.ManagementStatusReportId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<OrganizationUser>().WithMany().HasForeignKey(x => x.RecipientOrganizationUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<ConversationMessage>().WithMany().HasForeignKey(x => x.ConversationMessageId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<UserNotification>().WithMany().HasForeignKey(x => x.NotificationId).OnDelete(DeleteBehavior.SetNull);
        });
        modelBuilder.Entity<ResourceNeedReportRecord>(entity =>
        {
            entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.OrganizationId, x.Status, x.ReportedAt }); entity.Property(x => x.Capability).HasMaxLength(256).IsRequired();
            entity.Property(x => x.BusinessOutcome).HasMaxLength(2048).IsRequired(); entity.Property(x => x.Urgency).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Evidence).HasMaxLength(4096).IsRequired(); entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
        });
        modelBuilder.Entity<ResourceChangeRequestRecord>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.OrganizationId, x.RequesterInstallationId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.ManagerOrganizationUserId, x.Status });
            entity.Property(x => x.ProductGoal).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.Rationale).HasMaxLength(4096).IsRequired();
            entity.Property(x => x.AssumptionsJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.ConstraintsJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.DeliveryStatus).HasMaxLength(32).IsRequired();
            entity.Property(x => x.DecisionComment).HasMaxLength(4000);
            entity.Property(x => x.DecisionIdempotencyKey).HasMaxLength(160);
            entity.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ConversationMessage>().WithMany().HasForeignKey(x => x.ConversationMessageId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ResourceChangeRoleRecord>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ResourceChangeRequestId, x.RoleKey }).IsUnique();
            entity.Property(x => x.RoleKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Team).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Title).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Purpose).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.Timing).HasMaxLength(32).IsRequired();
            entity.Property(x => x.RequiredCapabilitiesJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.ReportsToRoleKey).HasMaxLength(160);
            entity.Property(x => x.ChangeKind).HasMaxLength(24).IsRequired();
            entity.Property(x => x.PreviousRoleJson).HasColumnType("jsonb");
            entity.HasOne(x => x.Request).WithMany(x => x.Roles).HasForeignKey(x => x.ResourceChangeRequestId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    static void ConfigureRole(EntityTypeBuilder<Role> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
        entity.Property(x => x.Description).HasMaxLength(4096).IsRequired();
        entity.Property(x => x.ResponsibilitiesJson).HasMaxLength(65536).IsRequired();
        entity.Property(x => x.AuthorityLevel).HasConversion<string>().HasMaxLength(32).IsRequired();

        entity.HasOne(x => x.Organization)
            .WithMany()
            .HasForeignKey(x => x.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique();
    }

    static void ConfigureStrategicObjective(EntityTypeBuilder<StrategicObjective> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Title).HasMaxLength(512).IsRequired();
        entity.Property(x => x.Description).HasMaxLength(8192).IsRequired();
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();

        entity.HasOne(x => x.Organization)
            .WithMany()
            .HasForeignKey(x => x.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    static void ConfigureWorker(EntityTypeBuilder<Worker> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Name).HasMaxLength(256).IsRequired();
        entity.Property(x => x.Description).HasMaxLength(4096).IsRequired();
        entity.Property(x => x.WorkerType).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(x => x.ExecutionMode).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(x => x.CapabilitiesJson).HasMaxLength(65536).IsRequired();
        entity.Property(x => x.CostModelJson).HasMaxLength(65536);
        entity.Property(x => x.EndpointConfigurationJson).HasMaxLength(65536);

        entity.HasOne(x => x.Organization)
            .WithMany()
            .HasForeignKey(x => x.OrganizationId)
            .OnDelete(DeleteBehavior.SetNull);
    }

    static void ConfigureWorkTask(EntityTypeBuilder<WorkTask> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Title).HasMaxLength(512).IsRequired();
        entity.Property(x => x.Description).HasMaxLength(8192).IsRequired();
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(x => x.Priority).HasConversion<string>().HasMaxLength(16).IsRequired();

        entity.HasOne(x => x.Organization)
            .WithMany()
            .HasForeignKey(x => x.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(x => x.StrategicObjective)
            .WithMany()
            .HasForeignKey(x => x.StrategicObjectiveId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasOne(x => x.AssignedRole)
            .WithMany()
            .HasForeignKey(x => x.AssignedRoleId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasOne(x => x.AssignedWorker)
            .WithMany()
            .HasForeignKey(x => x.AssignedWorkerId)
            .OnDelete(DeleteBehavior.SetNull);
    }

    static void ConfigureTaskRun(EntityTypeBuilder<TaskRun> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        entity.Property(x => x.InputJson).HasMaxLength(65536);
        entity.Property(x => x.OutputJson).HasMaxLength(65536);
        entity.Property(x => x.FailureMessage).HasMaxLength(4096);
        entity.Property(x => x.CostCurrency).HasMaxLength(8);

        entity.HasOne(x => x.Task)
            .WithMany()
            .HasForeignKey(x => x.TaskId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(x => x.Worker)
            .WithMany()
            .HasForeignKey(x => x.WorkerId)
            .OnDelete(DeleteBehavior.SetNull);
    }

    static void ConfigureArtifact(EntityTypeBuilder<Artifact> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Type).HasConversion<string>().HasMaxLength(16).IsRequired();
        entity.Property(x => x.Title).HasMaxLength(512).IsRequired();
        entity.Property(x => x.Content).HasMaxLength(131072).IsRequired();
        entity.Property(x => x.ApprovalStatus).HasConversion<string>().HasMaxLength(32).IsRequired();

        entity.HasOne(x => x.Organization)
            .WithMany()
            .HasForeignKey(x => x.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(x => x.Task)
            .WithMany()
            .HasForeignKey(x => x.TaskId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasOne(x => x.TaskRun)
            .WithMany()
            .HasForeignKey(x => x.TaskRunId)
            .OnDelete(DeleteBehavior.SetNull);
    }

    static void ConfigureApproval(EntityTypeBuilder<Approval> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(x => x.Comment).HasMaxLength(4096);

        entity.HasOne(x => x.Artifact)
            .WithMany()
            .HasForeignKey(x => x.ArtifactId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    static void ConfigureConversation(EntityTypeBuilder<Conversation> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Title).HasMaxLength(256);
        entity.Property(x => x.Description).HasMaxLength(2048);
        entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();

        entity.HasOne(x => x.Organization)
            .WithMany()
            .HasForeignKey(x => x.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(x => x.AgentOrganizationUser)
            .WithMany()
            .HasForeignKey(x => x.AgentOrganizationUserId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(x => new { x.OrganizationId, x.AgentOrganizationUserId });
        entity.HasIndex(x => new { x.OrganizationId, x.ArchivedAt, x.UpdatedAt });
        entity.HasIndex(x => new { x.OrganizationId, x.InitiatedByOrganizationUserId, x.AgentOrganizationUserId })
            .IsUnique()
            .HasFilter("\"IsDeletionProtected\" = TRUE AND \"AgentOrganizationUserId\" IS NOT NULL");
    }

    static void ConfigureConversationParticipant(EntityTypeBuilder<ConversationParticipant> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Role).HasConversion<string>().HasMaxLength(24).IsRequired();
        entity.Property(x => x.LastReadMessageSequence).HasDefaultValue(0L);
        entity.HasOne(x => x.Conversation).WithMany(x => x.Participants).HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(x => x.OrganizationUser).WithMany().HasForeignKey(x => x.OrganizationUserId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(x => new { x.ConversationId, x.OrganizationUserId }).IsUnique();
    }

    static void ConfigureConversationMessage(EntityTypeBuilder<ConversationMessage> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Sequence).ValueGeneratedOnAdd();
        entity.Property(x => x.Role).HasConversion<string>().HasMaxLength(16).IsRequired();
        entity.Property(x => x.Content).HasMaxLength(32768).IsRequired();
        entity.Property(x => x.DeliveryIntent).HasConversion<string>().HasMaxLength(24).IsRequired();
        entity.Property(x => x.SourceProvider).HasMaxLength(32).IsRequired();
        entity.Property(x => x.SourceChannelExternalId).HasMaxLength(128);
        entity.Property(x => x.IdempotencyKey).HasMaxLength(256);

        entity.HasOne(x => x.Conversation)
            .WithMany(x => x.Messages)
            .HasForeignKey(x => x.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(x => new { x.ConversationId, x.CreatedAt });
        entity.HasIndex(x => x.Sequence).IsUnique();
        entity.HasIndex(x => x.ChatTurnId);
        entity.HasIndex(x => x.IdempotencyKey).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
    }

    static void ConfigureSuggestedUserAction(EntityTypeBuilder<SuggestedUserAction> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.WorkflowType).HasMaxLength(160).IsRequired();
        entity.Property(x => x.Label).HasMaxLength(120).IsRequired();
        entity.Property(x => x.Description).HasMaxLength(500);
        entity.Property(x => x.ParametersJson).HasColumnType("jsonb").IsRequired();
        entity.Property(x => x.NavigationUri).HasMaxLength(2048).IsRequired();
        entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
        entity.Property(x => x.Status).HasMaxLength(24).IsRequired();
        entity.HasIndex(x => new { x.OriginatingInstallationId, x.IdempotencyKey }).IsUnique();
        entity.HasIndex(x => x.ConversationMessageId);
        entity.HasIndex(x => x.ChatTurnId);
        entity.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<ConversationMessage>().WithMany().HasForeignKey(x => x.ConversationMessageId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<ChatTurn>().WithMany().HasForeignKey(x => x.ChatTurnId).OnDelete(DeleteBehavior.Cascade);
    }

    static void ConfigureAgentPlatformEventOutbox(EntityTypeBuilder<AgentPlatformEventOutboxItem> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.EventType).HasMaxLength(160).IsRequired();
        entity.Property(x => x.DataJson).HasColumnType("jsonb").IsRequired();
        entity.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        entity.Property(x => x.LastError).HasMaxLength(2048);
        entity.HasIndex(x => new { x.OrganizationId, x.IdempotencyKey }).IsUnique();
        entity.HasIndex(x => new { x.Status, x.NextAttemptAt });
    }

    static void ConfigureChatTurn(EntityTypeBuilder<ChatTurn> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(x => x.PartialResponse).HasMaxLength(131072).IsRequired();
        entity.Property(x => x.ErrorCode).HasMaxLength(64);
        entity.Property(x => x.ErrorMessage).HasMaxLength(4096);
        entity.Property(x => x.LeaseOwner).HasMaxLength(160);
        entity.HasIndex(x => new { x.Status, x.CreatedAt });
        entity.HasIndex(x => new { x.ConversationId, x.CreatedAt });
        entity.HasIndex(x => x.UserMessageId).IsUnique();
        entity.HasOne(x => x.Conversation).WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(x => x.UserMessage).WithMany().HasForeignKey(x => x.UserMessageId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.AssistantMessage).WithMany().HasForeignKey(x => x.AssistantMessageId).OnDelete(DeleteBehavior.SetNull);
    }

    static void ConfigureChatTurnTraceEvent(EntityTypeBuilder<ChatTurnTraceEvent> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Category).HasMaxLength(32).IsRequired();
        entity.Property(x => x.EventType).HasMaxLength(64).IsRequired();
        entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
        entity.Property(x => x.Title).HasMaxLength(256).IsRequired();
        entity.Property(x => x.Summary).HasMaxLength(8192);
        entity.Property(x => x.DetailsJson).HasColumnType("jsonb");
        entity.Property(x => x.Sensitivity).HasMaxLength(32).IsRequired();
        entity.HasIndex(x => new { x.ChatTurnId, x.Sequence }).IsUnique();
        entity.HasOne(x => x.ChatTurn).WithMany(x => x.TraceEvents).HasForeignKey(x => x.ChatTurnId).OnDelete(DeleteBehavior.Cascade);
    }

    static void ConfigureExecutiveDecision(EntityTypeBuilder<ExecutiveDecision> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Prompt).HasMaxLength(2048).IsRequired();
        entity.Property(x => x.OptionsJson).HasColumnType("jsonb").IsRequired();
        entity.Property(x => x.RecommendedOptionId).HasMaxLength(80).IsRequired();
        entity.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        entity.Property(x => x.SelectedOptionId).HasMaxLength(80);
        entity.Property(x => x.FreeTextAnswer).HasMaxLength(4000);
        entity.Property(x => x.AnswerIdempotencyKey).HasMaxLength(200);
        entity.HasIndex(x => new { x.RequestingInstallationId, x.IdempotencyKey }).IsUnique();
        entity.HasIndex(x => new { x.ConversationId, x.RequestingInstallationId, x.Status })
            .IsUnique().HasFilter("\"Status\" = 'Pending'");
        entity.HasIndex(x => x.ChatTurnId);
        entity.HasOne(x => x.Conversation).WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(x => x.ChatTurn).WithMany().HasForeignKey(x => x.ChatTurnId).OnDelete(DeleteBehavior.Cascade);
    }

    static void ConfigureMemoryCaptureOutbox(EntityTypeBuilder<MemoryCaptureOutboxItem> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        entity.Property(x => x.LastError).HasMaxLength(2048);
        entity.HasIndex(x => x.ConversationMessageId).IsUnique();
        entity.HasIndex(x => new { x.Status, x.NextAttemptAt });
        entity.HasOne(x => x.ConversationMessage)
            .WithMany()
            .HasForeignKey(x => x.ConversationMessageId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    static void ConfigureAgentMemoryNamespace(EntityTypeBuilder<AgentMemoryNamespaceRegistration> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.PartitionKey).HasMaxLength(1024).IsRequired();
        entity.Property(x => x.Scope).HasMaxLength(32).IsRequired();
        entity.HasIndex(x => x.PartitionKey).IsUnique();
        entity.HasIndex(x => new { x.OrganizationId, x.EmployeeId, x.UserId });
    }

    static void ConfigureAgentMemoryRecallUse(EntityTypeBuilder<AgentMemoryRecallUse> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Layer).HasMaxLength(32).IsRequired();
        entity.HasIndex(x => new { x.OrganizationId, x.EmployeeId, x.MemoryId, x.UsedAt });
        entity.HasIndex(x => new { x.ConversationId, x.UsedAt });
    }

    static void ConfigureCommunicationConnection(EntityTypeBuilder<CommunicationConnection> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.ProviderKey).HasMaxLength(80).IsRequired();
        entity.Property(x => x.ManagedRootExternalId).HasMaxLength(128);
        entity.Property(x => x.WorkspaceMode).HasConversion<string>().HasMaxLength(24).IsRequired();
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        entity.Property(x => x.WorkspaceExternalId).HasMaxLength(128).IsRequired();
        entity.Property(x => x.ConfigurationJson).HasColumnType("jsonb").IsRequired();
        entity.HasIndex(x => new { x.PluginInstallationId, x.OrganizationId, x.ProviderKey, x.WorkspaceExternalId }).IsUnique();
        entity.HasIndex(x => new { x.OrganizationId, x.ProviderKey });
    }

    static void ConfigureManagedExternalResource(EntityTypeBuilder<ManagedExternalResource> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(24).IsRequired();
        entity.Property(x => x.Purpose).HasMaxLength(96).IsRequired();
        entity.Property(x => x.ExternalId).HasMaxLength(128).IsRequired();
        entity.Property(x => x.ParentExternalId).HasMaxLength(128);
        entity.Property(x => x.DisplayName).HasMaxLength(160).IsRequired();
        entity.Property(x => x.MetadataJson).HasColumnType("jsonb").IsRequired();
        entity.HasIndex(x => new { x.ConnectionId, x.ExternalId }).IsUnique();
        entity.HasIndex(x => new { x.ConnectionId, x.OrganizationUserId, x.Kind });
    }

    static void ConfigureExternalIdentityLink(EntityTypeBuilder<ExternalIdentityLink> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.ExternalUserId).HasMaxLength(128).IsRequired();
        entity.HasIndex(x => new { x.ConnectionId, x.ExternalUserId }).IsUnique();
        entity.HasIndex(x => new { x.ConnectionId, x.OrganizationUserId }).IsUnique();
    }

    static void ConfigureExternalIdentityLinkCode(EntityTypeBuilder<ExternalIdentityLinkCode> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.CodeHash).HasMaxLength(64).IsRequired();
        entity.HasIndex(x => x.CodeHash).IsUnique();
        entity.HasIndex(x => new { x.ConnectionId, x.ExpiresAt });
    }

    static void ConfigureExternalMessageReference(EntityTypeBuilder<ExternalMessageReference> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.ChannelExternalId).HasMaxLength(128).IsRequired();
        entity.Property(x => x.MessageExternalId).HasMaxLength(128).IsRequired();
        entity.Property(x => x.ThreadExternalId).HasMaxLength(128);
        entity.HasIndex(x => new { x.ConnectionId, x.MessageExternalId }).IsUnique();
    }

    static void ConfigureCommunicationDelivery(EntityTypeBuilder<CommunicationDelivery> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        entity.Property(x => x.IdempotencyKey).HasMaxLength(256).IsRequired();
        entity.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
        entity.Property(x => x.LeaseOwner).HasMaxLength(160);
        entity.Property(x => x.ExternalReceiptId).HasMaxLength(256);
        entity.Property(x => x.LastError).HasMaxLength(4096);
        entity.HasIndex(x => x.IdempotencyKey).IsUnique();
        entity.HasIndex(x => new { x.Status, x.NextAttemptAt });
    }

    static void ConfigureCommunicationIngressReceipt(EntityTypeBuilder<CommunicationIngressReceipt> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.ProviderKey).HasMaxLength(80).IsRequired();
        entity.Property(x => x.IdempotencyKey).HasMaxLength(256).IsRequired();
        entity.Property(x => x.ErrorCode).HasMaxLength(80);
        entity.Property(x => x.ResultMessage).HasMaxLength(2048).IsRequired();
        entity.HasIndex(x => new { x.PluginInstallationId, x.IdempotencyKey }).IsUnique();
    }

    static void ConfigureExternalIdentity(EntityTypeBuilder<ExternalIdentity> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.ProviderKey).HasMaxLength(80).IsRequired();
        entity.Property(x => x.ExternalUserId).HasMaxLength(128).IsRequired();
        entity.HasIndex(x => new { x.PluginInstallationId, x.ProviderKey, x.ExternalUserId }).IsUnique();
        entity.HasIndex(x => new { x.PluginInstallationId, x.ApplicationUserId });
    }

    static void ConfigureUserNotification(EntityTypeBuilder<UserNotification> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Severity).HasConversion<string>().HasMaxLength(16).IsRequired();
        entity.Property(x => x.Category).HasMaxLength(80).IsRequired();
        entity.Property(x => x.Title).HasMaxLength(256).IsRequired();
        entity.Property(x => x.Body).HasMaxLength(8192).IsRequired();
        entity.Property(x => x.ActionUri).HasMaxLength(2048);
        entity.Property(x => x.DeduplicationKey).HasMaxLength(256);
        entity.HasIndex(x => new { x.RecipientOrganizationUserId, x.ReadAt, x.CreatedAt });
        entity.HasIndex(x => new { x.OrganizationId, x.DeduplicationKey }).IsUnique().HasFilter("\"DeduplicationKey\" IS NOT NULL");
    }

    static void ConfigureNotificationPreference(EntityTypeBuilder<NotificationPreference> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.ProviderKey).HasMaxLength(80).IsRequired();
        entity.Property(x => x.MinimumSeverity).HasConversion<string>().HasMaxLength(16).IsRequired();
        entity.Property(x => x.QuietHoursStart).HasMaxLength(5);
        entity.Property(x => x.QuietHoursEnd).HasMaxLength(5);
        entity.Property(x => x.TimeZoneId).HasMaxLength(128).IsRequired();
        entity.HasIndex(x => new { x.OrganizationUserId, x.ProviderKey }).IsUnique();
    }

    static void ConfigureCommunicationEventOutbox(EntityTypeBuilder<CommunicationEventOutboxItem> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Sequence).ValueGeneratedOnAdd();
        entity.Property(x => x.EventType).HasMaxLength(160).IsRequired();
        entity.Property(x => x.Subject).HasMaxLength(256).IsRequired();
        entity.Property(x => x.DataJson).HasColumnType("jsonb").IsRequired();
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        entity.Property(x => x.DeliveredInstallationIdsJson).HasColumnType("jsonb").IsRequired();
        entity.Property(x => x.LastError).HasMaxLength(4096);
        entity.HasIndex(x => x.Sequence).IsUnique();
        entity.HasIndex(x => new { x.Status, x.NextAttemptAt, x.Sequence });
        entity.HasIndex(x => new { x.OrganizationId, x.ChatId, x.Sequence });
    }

    static void ConfigureAgentOnboardingEventOutbox(EntityTypeBuilder<AgentOnboardingEventOutboxItem> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        entity.Property(x => x.LastError).HasMaxLength(2048);
        entity.HasIndex(x => x.AgentOrganizationUserId).IsUnique();
        entity.HasIndex(x => new { x.Status, x.NextAttemptAt });
        entity.HasOne<OrganizationUser>().WithMany().HasForeignKey(x => x.AgentOrganizationUserId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<OrganizationUser>().WithMany().HasForeignKey(x => x.HiringOrganizationUserId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
    }

    static void ConfigureApplicationRealtimeOutbox(EntityTypeBuilder<ApplicationRealtimeOutboxItem> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Sequence).ValueGeneratedOnAdd();
        entity.Property(x => x.EventType).HasMaxLength(160).IsRequired();
        entity.Property(x => x.Subject).HasMaxLength(256).IsRequired();
        entity.Property(x => x.DataJson).HasColumnType("jsonb").IsRequired();
        entity.Property(x => x.RecipientOrganizationUserIdsJson).HasColumnType("jsonb").IsRequired();
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        entity.Property(x => x.LastError).HasMaxLength(4096);
        entity.HasIndex(x => x.Sequence).IsUnique();
        entity.HasIndex(x => new { x.Status, x.NextAttemptAt, x.Sequence });
        entity.HasIndex(x => new { x.OrganizationId, x.ChatId, x.Sequence });
        entity.HasIndex(x => new { x.RecipientOrganizationUserId, x.Sequence });
    }
}
