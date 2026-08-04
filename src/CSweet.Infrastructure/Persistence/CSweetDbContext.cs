using CSweet.Domain.Core;
using CSweet.Domain.Communications;
using CSweet.Domain.Planning;
using CSweet.Domain.Setup;
using CSweet.Contracts.Communications;
using CSweet.Contracts.Realtime;
using CSweet.Domain.Notifications;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Auth;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CSweet.Infrastructure.Persistence;

public sealed class CSweetDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>, IDataProtectionKeyContext
{
    private static readonly JsonSerializerOptions EventJsonOptions = new(JsonSerializerDefaults.Web);

    public CSweetDbContext(DbContextOptions<CSweetDbContext> options)
        : base(options)
    {
    }

    // Setup entities
    public DbSet<SystemConfiguration> SystemConfigurations => Set<SystemConfiguration>();
    public DbSet<LlmProviderProfile> LlmProviderProfiles => Set<LlmProviderProfile>();
    public DbSet<ModelCapabilityTest> ModelCapabilityTests => Set<ModelCapabilityTest>();
    public DbSet<GenAiProviderProfile> GenAiProviderProfiles => Set<GenAiProviderProfile>();
    public DbSet<GenAiOperationConfiguration> GenAiOperationConfigurations => Set<GenAiOperationConfiguration>();
    public DbSet<GenAiOperationDefault> GenAiOperationDefaults => Set<GenAiOperationDefault>();
    public DbSet<GenAiJob> GenAiJobs => Set<GenAiJob>();
    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();
    public DbSet<OnboardingStep> OnboardingSteps => Set<OnboardingStep>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<AgentRunLog> AgentRunLogs => Set<AgentRunLog>();
    public DbSet<AgentRuntimeGlobalSettings> AgentRuntimeGlobalSettings => Set<AgentRuntimeGlobalSettings>();
    public DbSet<AgentPackageSource> AgentPackageSources => Set<AgentPackageSource>();
    public DbSet<AgentPackageVersion> AgentPackageVersions => Set<AgentPackageVersion>();
    public DbSet<AgentInstallation> AgentInstallations => Set<AgentInstallation>();
    public DbSet<AgentInstallationGrant> AgentInstallationGrants => Set<AgentInstallationGrant>();
    public DbSet<AgentInstallationConfiguration> AgentInstallationConfigurations => Set<AgentInstallationConfiguration>();
    public DbSet<AgentSchedule> AgentSchedules => Set<AgentSchedule>();
    public DbSet<AgentBuildJob> AgentBuildJobs => Set<AgentBuildJob>();
    public DbSet<AgentRuntimeInstance> AgentRuntimeInstances => Set<AgentRuntimeInstance>();
    public DbSet<AgentRuntimeEvent> AgentRuntimeEvents => Set<AgentRuntimeEvent>();
    public DbSet<McpAgentSession> McpAgentSessions => Set<McpAgentSession>();
    public DbSet<AgentWorkItem> AgentWorkItems => Set<AgentWorkItem>();
    public DbSet<AgentWorkAttempt> AgentWorkAttempts => Set<AgentWorkAttempt>();
    public DbSet<AgentWorkProgress> AgentWorkProgress => Set<AgentWorkProgress>();
    public DbSet<AgentCapabilityBinding> AgentCapabilityBindings => Set<AgentCapabilityBinding>();
    public DbSet<PluginOrganizationGrant> PluginOrganizationGrants => Set<PluginOrganizationGrant>();
    public DbSet<PluginSecret> PluginSecrets => Set<PluginSecret>();
    public DbSet<SourceControlConnection> SourceControlConnections => Set<SourceControlConnection>();
    public DbSet<SourceControlCredential> SourceControlCredentials => Set<SourceControlCredential>();
    public DbSet<SourceControlRepository> SourceControlRepositories => Set<SourceControlRepository>();
    public DbSet<RepositoryProvisioningPolicy> RepositoryProvisioningPolicies => Set<RepositoryProvisioningPolicy>();
    public DbSet<SourceControlRepositoryTemplate> SourceControlRepositoryTemplates => Set<SourceControlRepositoryTemplate>();
    public DbSet<RepositoryProvisioningRequest> RepositoryProvisioningRequests => Set<RepositoryProvisioningRequest>();
    public DbSet<SourceControlApproval> SourceControlApprovals => Set<SourceControlApproval>();
    public DbSet<TeamRepositoryPolicy> TeamRepositoryPolicies => Set<TeamRepositoryPolicy>();
    public DbSet<SourceControlOnboardingSession> SourceControlOnboardingSessions => Set<SourceControlOnboardingSession>();
    public DbSet<SourceControlWorkspace> SourceControlWorkspaces => Set<SourceControlWorkspace>();
    public DbSet<SourceControlPublication> SourceControlPublications => Set<SourceControlPublication>();
    public DbSet<SourceControlValidation> SourceControlValidations => Set<SourceControlValidation>();
    public DbSet<SourceControlMergeAuthorization> SourceControlMergeAuthorizations => Set<SourceControlMergeAuthorization>();
    public DbSet<SourceControlMergeJob> SourceControlMergeJobs => Set<SourceControlMergeJob>();
    public DbSet<PlatformGitHubAppCredential> PlatformGitHubAppCredentials => Set<PlatformGitHubAppCredential>();
    public DbSet<PlatformSourceControlSetupSession> PlatformSourceControlSetupSessions => Set<PlatformSourceControlSetupSession>();

    // Planning entities
    public DbSet<PlanningTask> PlanningTasks => Set<PlanningTask>();
    public DbSet<PlanningDocument> PlanningDocuments => Set<PlanningDocument>();
    public DbSet<PlanningWorkflow> PlanningWorkflows => Set<PlanningWorkflow>();

    // Core business domain entities
    public DbSet<Domain.Core.Organization> CoreOrganizations => Set<Domain.Core.Organization>();
    public DbSet<OrganizationUser> CoreOrganizationUsers => Set<OrganizationUser>();
    public DbSet<OrganizationTeam> OrganizationTeams => Set<OrganizationTeam>();
    public DbSet<TeamMembership> TeamMemberships => Set<TeamMembership>();
    public DbSet<Role> CoreRoles => Set<Role>();
    public DbSet<StrategicObjective> CoreStrategicObjectives => Set<StrategicObjective>();
    public DbSet<Worker> CoreWorkers => Set<Worker>();
    public DbSet<WorkTask> CoreWorkTasks => Set<WorkTask>();
    public DbSet<WorkBoard> WorkBoards => Set<WorkBoard>();
    public DbSet<WorkBoardColumn> WorkBoardColumns => Set<WorkBoardColumn>();
    public DbSet<WorkSprint> WorkSprints => Set<WorkSprint>();
    public DbSet<WorkSprintSnapshot> WorkSprintSnapshots => Set<WorkSprintSnapshot>();
    public DbSet<WorkSprintMetricPoint> WorkSprintMetricPoints =>
        Set<WorkSprintMetricPoint>();
    public DbSet<WorkSprintMutationReceipt> WorkSprintMutationReceipts =>
        Set<WorkSprintMutationReceipt>();
    public DbSet<WorkBoardUserPreference> WorkBoardUserPreferences => Set<WorkBoardUserPreference>();
    public DbSet<WorkItemMutationReceipt> WorkItemMutationReceipts => Set<WorkItemMutationReceipt>();
    public DbSet<WorkItemComment> WorkItemComments => Set<WorkItemComment>();
    public DbSet<WorkItemActivity> WorkItemActivities => Set<WorkItemActivity>();
    public DbSet<WorkItemDependency> WorkItemDependencies => Set<WorkItemDependency>();
    public DbSet<WorkQualityRun> WorkQualityRuns => Set<WorkQualityRun>();
    public DbSet<WorkOrchestrationPolicy> WorkOrchestrationPolicies => Set<WorkOrchestrationPolicy>();
    public DbSet<WorkOrchestrationPolicyRevision> WorkOrchestrationPolicyRevisions => Set<WorkOrchestrationPolicyRevision>();
    public DbSet<WorkOrchestrationStage> WorkOrchestrationStages => Set<WorkOrchestrationStage>();
    public DbSet<WorkOrchestrationTransition> WorkOrchestrationTransitions => Set<WorkOrchestrationTransition>();
    public DbSet<WorkItemStageAssignment> WorkItemStageAssignments => Set<WorkItemStageAssignment>();
    public DbSet<WorkSprintExecution> WorkSprintExecutions => Set<WorkSprintExecution>();
    public DbSet<WorkItemExecution> WorkItemExecutions => Set<WorkItemExecution>();
    public DbSet<WorkStageExecution> WorkStageExecutions => Set<WorkStageExecution>();
    public DbSet<WorkExecutionAttempt> WorkExecutionAttempts => Set<WorkExecutionAttempt>();
    public DbSet<WorkOrchestrationEvent> WorkOrchestrationEvents => Set<WorkOrchestrationEvent>();
    public DbSet<ScopedActionGrant> ScopedActionGrants => Set<ScopedActionGrant>();
    public DbSet<TaskRun> CoreTaskRuns => Set<TaskRun>();
    public DbSet<Artifact> CoreArtifacts => Set<Artifact>();
    public DbSet<Approval> CoreApprovals => Set<Approval>();
    public DbSet<BusinessProfile> BusinessProfiles => Set<BusinessProfile>();
    public DbSet<FinancialOperatingProfile> FinancialOperatingProfiles => Set<FinancialOperatingProfile>();
    public DbSet<BusinessDiscoveryAssessment> BusinessDiscoveryAssessments => Set<BusinessDiscoveryAssessment>();
    public DbSet<LeadershipAssignment> LeadershipAssignments => Set<LeadershipAssignment>();
    public DbSet<Workstream> Workstreams => Set<Workstream>();
    public DbSet<ActionProposal> ActionProposals => Set<ActionProposal>();
    public DbSet<Budget> Budgets => Set<Budget>();
    public DbSet<BudgetReservation> BudgetReservations => Set<BudgetReservation>();
    public DbSet<ManagementCycle> ManagementCycles => Set<ManagementCycle>();
    public DbSet<BusinessPattern> BusinessPatterns => Set<BusinessPattern>();
    public DbSet<WorkforcePlan> WorkforcePlans => Set<WorkforcePlan>();
    public DbSet<HiringRecommendationFulfillment> HiringRecommendationFulfillments => Set<HiringRecommendationFulfillment>();
    public DbSet<WorkforceCandidate> WorkforceCandidates => Set<WorkforceCandidate>();
    public DbSet<ResourceNeed> ResourceNeeds => Set<ResourceNeed>();
    public DbSet<StaffingActionProposal> StaffingActionProposals => Set<StaffingActionProposal>();
    public DbSet<Responsibility> Responsibilities => Set<Responsibility>();
    public DbSet<ManagementCheckInRequestRecord> ManagementCheckInRequests => Set<ManagementCheckInRequestRecord>();
    public DbSet<ManagementStatusReportRecord> ManagementStatusReports => Set<ManagementStatusReportRecord>();
    public DbSet<ResourceNeedReportRecord> ResourceNeedReports => Set<ResourceNeedReportRecord>();
    public DbSet<ResourceChangeRequestRecord> ResourceChangeRequests => Set<ResourceChangeRequestRecord>();
    public DbSet<ResourceChangeRoleRecord> ResourceChangeRoles => Set<ResourceChangeRoleRecord>();
    public DbSet<ExecutiveBriefingDeliveryRecord> ExecutiveBriefingDeliveries => Set<ExecutiveBriefingDeliveryRecord>();

    // Conversation entities
    public DbSet<Conversation> CoreConversations => Set<Conversation>();
    public DbSet<ConversationMessage> CoreConversationMessages => Set<ConversationMessage>();
    public DbSet<SuggestedUserAction> SuggestedUserActions => Set<SuggestedUserAction>();
    public DbSet<ChatTurn> ChatTurns => Set<ChatTurn>();
    public DbSet<ChatTurnTraceEvent> ChatTurnTraceEvents => Set<ChatTurnTraceEvent>();
    public DbSet<ExecutiveDecision> ExecutiveDecisions => Set<ExecutiveDecision>();
    public DbSet<ConversationParticipant> ConversationParticipants => Set<ConversationParticipant>();
    public DbSet<Domain.Communications.AgentCoordinationSession> AgentCoordinationSessions => Set<Domain.Communications.AgentCoordinationSession>();
    public DbSet<Domain.Communications.AgentCoordinationTurn> AgentCoordinationTurns => Set<Domain.Communications.AgentCoordinationTurn>();
    public DbSet<CommunicationConnection> CommunicationConnections => Set<CommunicationConnection>();
    public DbSet<ManagedExternalResource> ManagedExternalResources => Set<ManagedExternalResource>();
    public DbSet<ExternalIdentityLink> ExternalIdentityLinks => Set<ExternalIdentityLink>();
    public DbSet<ExternalIdentityLinkCode> ExternalIdentityLinkCodes => Set<ExternalIdentityLinkCode>();
    public DbSet<ExternalMessageReference> ExternalMessageReferences => Set<ExternalMessageReference>();
    public DbSet<CommunicationDelivery> CommunicationDeliveries => Set<CommunicationDelivery>();
    public DbSet<CommunicationIngressReceipt> CommunicationIngressReceipts => Set<CommunicationIngressReceipt>();
    public DbSet<ExternalIdentity> ExternalIdentities => Set<ExternalIdentity>();
    public DbSet<UserNotification> UserNotifications => Set<UserNotification>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    public DbSet<CommunicationEventOutboxItem> CommunicationEventOutbox => Set<CommunicationEventOutboxItem>();
    public DbSet<AgentOnboardingEventOutboxItem> AgentOnboardingEventOutbox => Set<AgentOnboardingEventOutboxItem>();
    public DbSet<AgentPlatformEventOutboxItem> AgentPlatformEventOutbox => Set<AgentPlatformEventOutboxItem>();
    public DbSet<ApplicationRealtimeOutboxItem> ApplicationRealtimeOutbox => Set<ApplicationRealtimeOutboxItem>();
    public DbSet<MemoryCaptureOutboxItem> MemoryCaptureOutbox => Set<MemoryCaptureOutboxItem>();
    public DbSet<AgentMemoryNamespaceRegistration> AgentMemoryNamespaces => Set<AgentMemoryNamespaceRegistration>();
    public DbSet<AgentMemoryRecallUse> AgentMemoryRecallUses => Set<AgentMemoryRecallUse>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<RootRecoveryCode> RootRecoveryCodes => Set<RootRecoveryCode>();
    public DbSet<EmailDeliveryProfile> EmailDeliveryProfiles => Set<EmailDeliveryProfile>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceAppendOnlyAuditLedger();
        AssignInMemoryMessageSequences();
        CaptureCommunicationEvents();
        CaptureApplicationNotificationEvents();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceAppendOnlyAuditLedger();
        AssignInMemoryMessageSequences();
        CaptureCommunicationEvents();
        CaptureApplicationNotificationEvents();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void EnforceAppendOnlyAuditLedger()
    {
        ChangeTracker.DetectChanges();
        if (ChangeTracker.Entries<AuditEvent>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Security audit ledger records are append-only.");
    }

    private void CaptureCommunicationEvents()
    {
        ChangeTracker.DetectChanges();
        var conversations = ChangeTracker.Entries<Conversation>()
            .Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList();
        var participants = ChangeTracker.Entries<ConversationParticipant>()
            .Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList();
        var messages = ChangeTracker.Entries<ConversationMessage>()
            .Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList();

        foreach (var entry in conversations)
        {
            var chat = entry.Entity;
            var eventType = entry.State switch
            {
                EntityState.Added => CommunicationEvents.ChatCreated,
                EntityState.Deleted => CommunicationEvents.ChatDeleted,
                EntityState.Modified when entry.Property(x => x.ArchivedAt).IsModified && chat.ArchivedAt.HasValue
                    => CommunicationEvents.ChatArchived,
                EntityState.Modified when HasConversationStateChange(entry) => CommunicationEvents.ChatUpdated,
                _ => null
            };
            if (eventType is not null) QueueCommunicationEvent(chat.OrganizationId, chat.Id, eventType,
                new CommunicationChatEvent(chat.Id, chat.OrganizationId, chat.Kind.ToString(),
                    chat.InitiatedByOrganizationUserId, chat.AgentOrganizationUserId, chat.TeamId, chat.ProjectId,
                    chat.Title, chat.Description, chat.IsPrivate, chat.IsDeletionProtected,
                    chat.CreatedAt, chat.UpdatedAt, chat.ArchivedAt));
        }

        foreach (var entry in participants)
        {
            var participant = entry.Entity;
            var eventType = entry.State switch
            {
                EntityState.Added => CommunicationEvents.ParticipantAdded,
                EntityState.Deleted => CommunicationEvents.ParticipantRemoved,
                EntityState.Modified when entry.Property(x => x.LeftAt).IsModified && participant.LeftAt.HasValue
                    => CommunicationEvents.ParticipantRemoved,
                EntityState.Modified when entry.Property(x => x.LeftAt).IsModified && !participant.LeftAt.HasValue
                    => CommunicationEvents.ParticipantAdded,
                EntityState.Modified when entry.Property(x => x.Role).IsModified
                    => CommunicationEvents.ParticipantUpdated,
                EntityState.Modified when entry.Property(x => x.LastReadMessageSequence).IsModified
                    => CommunicationEvents.ReadUpdated,
                _ => null
            };
            if (eventType is null) continue;
            var organizationId = ResolveConversationOrganizationId(participant.ConversationId, participant.Conversation);
            if (!organizationId.HasValue) continue;
            if (eventType == CommunicationEvents.ReadUpdated)
                QueueCommunicationEvent(organizationId.Value, participant.ConversationId, eventType,
                    new CommunicationReadEvent(participant.ConversationId, participant.OrganizationUserId,
                        participant.LastReadMessageSequence, DateTimeOffset.UtcNow));
            else
                QueueCommunicationEvent(organizationId.Value, participant.ConversationId, eventType,
                    new CommunicationParticipantEvent(participant.Id, participant.ConversationId,
                        participant.OrganizationUserId, participant.Role.ToString(), participant.JoinedAt, participant.LeftAt));
        }

        foreach (var entry in messages)
        {
            var message = entry.Entity;
            var eventType = entry.State switch
            {
                EntityState.Added => CommunicationEvents.MessageCreated,
                EntityState.Deleted => CommunicationEvents.MessageDeleted,
                EntityState.Modified when HasMessageStateChange(entry) => CommunicationEvents.MessageUpdated,
                _ => null
            };
            if (eventType is null) continue;
            var organizationId = ResolveConversationOrganizationId(message.ConversationId, message.Conversation);
            if (!organizationId.HasValue) continue;
            QueueCommunicationEvent(organizationId.Value, message.ConversationId, eventType,
                new CommunicationMessageEvent(message.Id, message.ConversationId, message.SenderOrganizationUserId,
                    message.ReplyToMessageId, message.Role.ToString(), message.Content, message.CorrelationId,
                    message.CausationId, message.DeliveryIntent.ToString(), message.SourceProvider,
                    message.SourceChannelExternalId, message.CreatedAt));
        }
    }

    private void AssignInMemoryMessageSequences()
    {
        if (Database.ProviderName != "Microsoft.EntityFrameworkCore.InMemory") return;
        var added = ChangeTracker.Entries<ConversationMessage>()
            .Where(x => x.State == EntityState.Added && x.Entity.Sequence == 0)
            .Select(x => x.Entity).ToList();
        if (added.Count == 0) return;
        var next = CoreConversationMessages.AsNoTracking().Select(x => (long?)x.Sequence).Max() ?? 0;
        foreach (var message in added) message.Sequence = ++next;
    }

    private static bool HasConversationStateChange(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<Conversation> entry) =>
        entry.Property(x => x.Kind).IsModified || entry.Property(x => x.AgentOrganizationUserId).IsModified ||
        entry.Property(x => x.TeamId).IsModified || entry.Property(x => x.ProjectId).IsModified ||
        entry.Property(x => x.Title).IsModified || entry.Property(x => x.Description).IsModified ||
        entry.Property(x => x.IsPrivate).IsModified || entry.Property(x => x.IsDeletionProtected).IsModified ||
        entry.Property(x => x.ArchivedAt).IsModified;

    private static bool HasMessageStateChange(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<ConversationMessage> entry) =>
        entry.Property(x => x.SenderOrganizationUserId).IsModified || entry.Property(x => x.ReplyToMessageId).IsModified ||
        entry.Property(x => x.Role).IsModified || entry.Property(x => x.Content).IsModified ||
        entry.Property(x => x.DeliveryIntent).IsModified || entry.Property(x => x.SourceProvider).IsModified ||
        entry.Property(x => x.SourceChannelExternalId).IsModified;

    private Guid? ResolveConversationOrganizationId(Guid chatId, Conversation? navigation)
    {
        if (navigation is not null) return navigation.OrganizationId;
        var local = CoreConversations.Local.FirstOrDefault(x => x.Id == chatId);
        if (local is not null) return local.OrganizationId;
        return CoreConversations.AsNoTracking().Where(x => x.Id == chatId).Select(x => (Guid?)x.OrganizationId).SingleOrDefault();
    }

    private void QueueCommunicationEvent<T>(Guid organizationId, Guid chatId, string eventType, T data)
    {
        var now = DateTimeOffset.UtcNow;
        var dataJson = JsonSerializer.Serialize(data, EventJsonOptions);
        CommunicationEventOutbox.Add(new CommunicationEventOutboxItem
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, ChatId = chatId,
            EventType = eventType, Subject = CommunicationEvents.Subject(organizationId, chatId),
            DataJson = dataJson, Status = CommunicationEventOutboxStatus.Pending,
            NextAttemptAt = now, OccurredAt = now
        });
        QueueApplicationRealtimeEvent(organizationId, null, chatId, eventType,
            CommunicationEvents.Subject(organizationId, chatId), dataJson, now,
            ResolveRealtimeRecipients(chatId));
    }

    private void CaptureApplicationNotificationEvents()
    {
        var notifications = ChangeTracker.Entries<UserNotification>()
            .Where(x => x.State == EntityState.Added ||
                (x.State == EntityState.Modified && (x.Property(y => y.ReadAt).IsModified || x.Property(y => y.DismissedAt).IsModified)))
            .ToList();
        foreach (var entry in notifications)
        {
            var item = entry.Entity;
            var eventType = entry.State == EntityState.Added
                ? AppRealtimeEvents.NotificationCreated : AppRealtimeEvents.NotificationUpdated;
            var data = new AppNotificationEvent(item.Id, item.OrganizationId, item.RecipientOrganizationUserId,
                item.OriginatingAgentOrganizationUserId, item.Severity.ToString(), item.Category, item.Title,
                item.Body, item.ActionUri, item.CreatedAt, item.ReadAt, item.DismissedAt);
            QueueApplicationRealtimeEvent(item.OrganizationId, item.RecipientOrganizationUserId, null, eventType,
                $"organizations/{item.OrganizationId:D}/notifications/{item.Id:D}",
                JsonSerializer.Serialize(data, EventJsonOptions), DateTimeOffset.UtcNow);
        }
    }

    private void QueueApplicationRealtimeEvent(Guid? organizationId, Guid? recipientOrganizationUserId,
        Guid? chatId, string eventType, string subject, string dataJson, DateTimeOffset occurredAt,
        IReadOnlyCollection<Guid>? recipientOrganizationUserIds = null)
    {
        ApplicationRealtimeOutbox.Add(new ApplicationRealtimeOutboxItem
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId,
            RecipientOrganizationUserId = recipientOrganizationUserId,
            RecipientOrganizationUserIdsJson = JsonSerializer.Serialize(recipientOrganizationUserIds ?? [], EventJsonOptions),
            ChatId = chatId,
            EventType = eventType, Subject = subject, DataJson = dataJson,
            Status = ApplicationRealtimeOutboxStatus.Pending, NextAttemptAt = occurredAt, OccurredAt = occurredAt
        });
    }

    private IReadOnlyCollection<Guid> ResolveRealtimeRecipients(Guid chatId)
    {
        var recipients = ConversationParticipants.AsNoTracking()
            .Where(x => x.ConversationId == chatId && x.LeftAt == null)
            .Select(x => x.OrganizationUserId).ToHashSet();
        foreach (var entry in ChangeTracker.Entries<ConversationParticipant>().Where(x => x.Entity.ConversationId == chatId))
        {
            if (entry.State == EntityState.Added && entry.Entity.LeftAt == null)
                recipients.Add(entry.Entity.OrganizationUserId);
            else if (entry.State is EntityState.Modified or EntityState.Deleted)
                recipients.Add(entry.Entity.OrganizationUserId);
        }
        return recipients;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        SourceControlModelConfiguration.Configure(modelBuilder);

        modelBuilder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(x => x.DisplayName).HasMaxLength(160).IsRequired();
            entity.Property(x => x.CreatedAt).IsRequired();
            entity.HasIndex(x => x.IsInitialAdministrator)
                .IsUnique()
                .HasFilter("\"IsInitialAdministrator\" = TRUE");
        });

        modelBuilder.Entity<RootRecoveryCode>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CodeHash).HasMaxLength(1024).IsRequired();
            entity.Property(x => x.ConcurrencyStamp).HasMaxLength(64).IsConcurrencyToken();
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(x => x.ApplicationUserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.ApplicationUserId, x.UsedAt });
        });

        modelBuilder.Entity<EmailDeliveryProfile>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.ProviderKey).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Host).HasMaxLength(253).IsRequired();
            entity.Property(x => x.UserName).HasMaxLength(320);
            entity.Property(x => x.EncryptedPassword).HasMaxLength(4096);
            entity.Property(x => x.FromAddress).HasMaxLength(320).IsRequired();
            entity.Property(x => x.FromName).HasMaxLength(160).IsRequired();
            entity.Property(x => x.PublicAppUrl).HasMaxLength(2048).IsRequired();
            entity.HasIndex(x => x.IsDefault)
                .IsUnique()
                .HasFilter("\"IsDefault\" = TRUE");
        });

        // Apply planning entity configurations
        PlanningConfigurations.Apply(modelBuilder);
        
        // Apply core business domain entity configurations
        CoreConfigurations.Apply(modelBuilder);
        WorkManagementConfigurations.Apply(modelBuilder);
        modelBuilder.Entity<SystemConfiguration>(entity =>
        {
            entity.HasKey(x => x.Id);
        });

        modelBuilder.Entity<LlmProviderProfile>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.ProviderType).HasConversion<string>().HasMaxLength(64).IsRequired();
            entity.Property(x => x.BaseUrl).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.ApiKeySecretName).HasMaxLength(256);
            entity.Property(x => x.DefaultChatModel).HasMaxLength(256).IsRequired();
            entity.Property(x => x.DefaultEmbeddingModel).HasMaxLength(256);
        });

        modelBuilder.Entity<ModelCapabilityTest>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FailureMessage).HasMaxLength(2048);
            entity.HasOne(x => x.ProviderProfile)
                .WithMany()
                .HasForeignKey(x => x.ProviderProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GenAiProviderProfile>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.ProviderType).HasConversion<string>().HasMaxLength(40).IsRequired();
            entity.Property(x => x.BaseUrl).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.ApiKeySecretName).HasMaxLength(256);
        });

        modelBuilder.Entity<GenAiOperationConfiguration>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OperationType).HasConversion<string>().HasMaxLength(40).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.ModelId).HasMaxLength(512);
            entity.Property(x => x.TemplateJson).HasColumnType("text");
            entity.Property(x => x.OutputSelector).HasMaxLength(512);
            entity.Property(x => x.DefaultsJson).HasColumnType("text");
            entity.HasOne(x => x.ProviderProfile).WithMany().HasForeignKey(x => x.ProviderProfileId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.ProviderProfileId, x.OperationType, x.Name }).IsUnique();
        });

        modelBuilder.Entity<GenAiOperationDefault>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OperationType).HasConversion<string>().HasMaxLength(40).IsRequired();
            entity.HasIndex(x => x.OperationType).IsUnique();
            entity.HasOne(x => x.OperationConfiguration).WithMany().HasForeignKey(x => x.OperationConfigurationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GenAiJob>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OperationType).HasConversion<string>().HasMaxLength(40).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
            entity.Property(x => x.PromptHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.RequestJson).HasColumnType("text").IsRequired();
            entity.Property(x => x.ProviderJobId).HasMaxLength(512);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(200);
            entity.Property(x => x.ErrorCode).HasMaxLength(160);
            entity.Property(x => x.ErrorMessage).HasMaxLength(2048);
            entity.Property(x => x.LeaseOwner).HasMaxLength(64);
            entity.HasOne(x => x.OperationConfiguration).WithMany().HasForeignKey(x => x.OperationConfigurationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.AgentInstallationId, x.OperationType, x.IdempotencyKey })
                .IsUnique()
                .HasFilter("\"IdempotencyKey\" IS NOT NULL");
            entity.HasIndex(x => new { x.Status, x.CreatedAt });
        });

        modelBuilder.Entity<MediaAsset>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FileName).HasMaxLength(255).IsRequired();
            entity.Property(x => x.ContentType).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            entity.Property(x => x.StorageKey).HasMaxLength(512).IsRequired();
            entity.HasIndex(x => x.StorageKey).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
            entity.HasOne(x => x.GenAiJob).WithMany().HasForeignKey(x => x.GenAiJobId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<OnboardingStep>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Key).HasMaxLength(80).IsRequired();
            entity.Property(x => x.DisplayName).HasMaxLength(160).IsRequired();
            entity.HasIndex(x => x.Key).IsUnique();
        });

        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Sequence).ValueGeneratedOnAdd();
            entity.Property(x => x.Category).HasMaxLength(48).IsRequired();
            entity.Property(x => x.Direction).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Outcome).HasMaxLength(48).IsRequired();
            entity.Property(x => x.EventType).HasMaxLength(160).IsRequired();
            entity.Property(x => x.EntityType).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Summary).HasMaxLength(1024);
            entity.Property(x => x.ExternalMessageId).HasMaxLength(200);
            entity.Property(x => x.ExternalRequestId).HasMaxLength(200);
            entity.Property(x => x.CorrelationId).HasMaxLength(200);
            entity.Property(x => x.ActorKind).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ActorDisplayName).HasMaxLength(256);
            entity.Property(x => x.ActorAgentId).HasMaxLength(256);
            entity.Property(x => x.ActorSessionId).HasMaxLength(128);
            entity.Property(x => x.ActorPackageId).HasMaxLength(256);
            entity.Property(x => x.ActorPackageVersion).HasMaxLength(80);
            entity.Property(x => x.RemotePeer).HasMaxLength(256);
            entity.Property(x => x.TargetKind).HasMaxLength(32);
            entity.Property(x => x.TargetDisplayName).HasMaxLength(256);
            entity.Property(x => x.TargetAgentId).HasMaxLength(256);
            entity.Property(x => x.TargetSessionId).HasMaxLength(128);
            entity.Property(x => x.ContentType).HasMaxLength(160);
            entity.Property(x => x.ErrorCode).HasMaxLength(160);
            entity.Property(x => x.ErrorMessage).HasMaxLength(2048);
            entity.Property(x => x.PayloadSha256).HasMaxLength(64);
            entity.Property(x => x.PreviousRecordHash).HasMaxLength(64);
            entity.Property(x => x.RecordHash).HasMaxLength(64);
            entity.HasIndex(x => x.Sequence).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.Sequence });
            entity.HasIndex(x => new { x.OrganizationId, x.Category, x.Sequence });
            entity.HasIndex(x => x.TraceId);
            entity.HasIndex(x => x.ParentEventId);
            entity.HasIndex(x => x.CorrelationId);
        });

        modelBuilder.Entity<AgentRunLog>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AgentKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Model).HasMaxLength(512);
            entity.Property(x => x.Status).HasMaxLength(80).IsRequired();
            entity.Property(x => x.PromptHash).HasMaxLength(512).IsRequired();
            entity.Property(x => x.FailureMessage).HasMaxLength(2048);
            entity.HasIndex(x => new { x.OrganizationId, x.StartedAt });
            entity.HasIndex(x => new { x.OrganizationId, x.EmployeeId, x.StartedAt });
        });

        modelBuilder.Entity<AgentRuntimeGlobalSettings>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DefaultActivationMode).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(x => x.DefaultOverlapPolicy).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(x => x.DefaultRestartPolicy).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(x => x.DefaultNetworkPolicy).HasMaxLength(64).IsRequired();
            entity.Property(x => x.AllowedPackageFeedHosts).HasMaxLength(2048);
            entity.Property(x => x.BlockedNetworkCidrs).HasMaxLength(2048);
            entity.Property(x => x.AgentSourceRootPath).HasMaxLength(1024);
            entity.Property(x => x.AgentPackageCachePath).HasMaxLength(1024);
            entity.Property(x => x.DotNetBuilderImage).HasMaxLength(256).IsRequired();
            entity.Property(x => x.DotNetRuntimeBaseImage).HasMaxLength(256).IsRequired();
        });

        modelBuilder.Entity<AgentPackageSource>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RepositoryUrl).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.Host).HasMaxLength(253).IsRequired();
            entity.Property(x => x.RepositoryOwner).HasMaxLength(100).IsRequired();
            entity.Property(x => x.RepositoryName).HasMaxLength(100).IsRequired();
            entity.Property(x => x.DefaultBranch).HasMaxLength(255).IsRequired();
            entity.Property(x => x.SourceType).HasMaxLength(32).IsRequired();
            entity.Property(x => x.SourceArchivePath).HasMaxLength(2048);
            entity.HasIndex(x => x.RepositoryUrl).IsUnique();
        });

        modelBuilder.Entity<AgentPackageVersion>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CommitSha).HasMaxLength(40).IsRequired();
            entity.Property(x => x.ManifestDigest).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CapabilityDescriptorsDigest).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ManifestJson).HasColumnType("text").IsRequired();
            entity.Property(x => x.PluginKind).HasConversion<string>().HasMaxLength(40).IsRequired();
            entity.Property(x => x.ManifestFileName).HasMaxLength(80).IsRequired();
            entity.Property(x => x.AgentId).HasMaxLength(200).IsRequired();
            entity.Property(x => x.AgentName).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Version).HasMaxLength(64).IsRequired();
            entity.Property(x => x.PublisherId).HasMaxLength(200).IsRequired();
            entity.Property(x => x.PublisherName).HasMaxLength(200).IsRequired();
            entity.Property(x => x.RuntimeType).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ProjectPath).HasMaxLength(1024);
            entity.Property(x => x.TargetFramework).HasMaxLength(64);
            entity.Property(x => x.DefaultActivationMode).HasMaxLength(32);
            entity.Property(x => x.WarningsJson).HasColumnType("text").IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.PackageDigest).HasMaxLength(64);
            entity.Property(x => x.PackagePath).HasMaxLength(2048);
            entity.HasIndex(x => new { x.PackageSourceId, x.CommitSha, x.ManifestDigest }).IsUnique();
            entity.HasOne(x => x.PackageSource)
                .WithMany()
                .HasForeignKey(x => x.PackageSourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentInstallation>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RevisionStatus).HasConversion<string>().HasMaxLength(24).IsRequired();
            entity.HasIndex(x => new { x.InstallationKey, x.RevisionNumber }).IsUnique();
            entity.Property(x => x.BusinessId).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Scope).HasConversion<string>().HasMaxLength(24).IsRequired();
            entity.HasIndex(x => new { x.PackageVersionId, x.BusinessId });
            entity.HasOne(x => x.PackageVersion)
                .WithMany()
                .HasForeignKey(x => x.PackageVersionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AgentInstallationGrant>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.NetworkAccessJson).HasColumnType("text").IsRequired();
            entity.Property(x => x.ProvidedCapabilitiesJson).HasColumnType("text").IsRequired();
            entity.Property(x => x.RequiredCapabilitiesJson).HasColumnType("text").IsRequired();
            entity.Property(x => x.EventSubscriptionsJson).HasColumnType("text").IsRequired();
            entity.Property(x => x.ResourceLimitsJson).HasColumnType("text").IsRequired();
            entity.HasIndex(x => x.AgentInstallationId).IsUnique();
            entity.HasOne(x => x.AgentInstallation)
                .WithOne(x => x.Grant)
                .HasForeignKey<AgentInstallationGrant>(x => x.AgentInstallationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PluginOrganizationGrant>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.PluginInstallationId, x.OrganizationId }).IsUnique();
            entity.HasOne(x => x.PluginInstallation).WithMany()
                .HasForeignKey(x => x.PluginInstallationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PluginSecret>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Key).HasMaxLength(160).IsRequired();
            entity.Property(x => x.ProtectedValue).HasMaxLength(8192).IsRequired();
            entity.HasIndex(x => new { x.PluginInstallationId, x.Key }).IsUnique();
            entity.HasOne(x => x.PluginInstallation).WithMany()
                .HasForeignKey(x => x.PluginInstallationId).OnDelete(DeleteBehavior.Cascade);
        });

            modelBuilder.Entity<AgentInstallationConfiguration>(entity =>
            {
                entity.HasKey(x => x.Id);
                entity.Property(x => x.SchemaVersion).HasMaxLength(64).IsRequired();
                entity.Property(x => x.SettingsJson).HasColumnType("text").IsRequired();
                entity.HasIndex(x => x.AgentInstallationId).IsUnique();
                entity.HasOne(x => x.AgentInstallation)
                .WithOne(x => x.Configuration)
                .HasForeignKey<AgentInstallationConfiguration>(x => x.AgentInstallationId)
                .OnDelete(DeleteBehavior.Cascade);
            });

        modelBuilder.Entity<AgentSchedule>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ActivationMode).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(x => x.OverlapPolicy).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(x => x.NextTickAt).IsConcurrencyToken();
            entity.HasIndex(x => x.AgentInstallationId).IsUnique();
            entity.HasOne(x => x.AgentInstallation)
                .WithOne(x => x.Schedule)
                .HasForeignKey<AgentSchedule>(x => x.AgentInstallationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentBuildJob>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.SourceWorkspacePath).HasMaxLength(2048);
            entity.Property(x => x.PackagePath).HasMaxLength(2048);
            entity.Property(x => x.PackageDigest).HasMaxLength(64);
            entity.Property(x => x.LogPath).HasMaxLength(2048);
            entity.Property(x => x.FailureMessage).HasMaxLength(2048);
            entity.Property(x => x.StepsJson).HasColumnType("text").IsRequired();
            entity.HasIndex(x => new { x.PackageVersionId, x.Attempt }).IsUnique();
            entity.HasIndex(x => new { x.Status, x.QueuedAt });
            entity.HasOne(x => x.PackageVersion)
                .WithMany(x => x.BuildJobs)
                .HasForeignKey(x => x.PackageVersionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentRuntimeInstance>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(48).IsRequired();
            entity.Property(x => x.WorkloadTokenHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ContainerId).HasMaxLength(128);
            entity.Property(x => x.ContainerName).HasMaxLength(128);
            entity.Property(x => x.Reason).HasMaxLength(2048);
            entity.Property(x => x.LogExcerpt).HasColumnType("text");
            entity.HasIndex(x => x.TickId).IsUnique();
            entity.HasIndex(x => new { x.AgentInstallationId, x.Status });
            entity.HasIndex(x => x.AgentInstallationId)
                .HasDatabaseName("UX_AgentRuntimeInstances_ActiveInstallation")
                .IsUnique()
                .HasFilter("\"Status\" IN ('Queued', 'Starting', 'WaitingForMcpSession', 'Running', 'CompletionReported', 'Stopping')");
            entity.HasOne(x => x.AgentInstallation)
                .WithMany(x => x.RuntimeInstances)
                .HasForeignKey(x => x.AgentInstallationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentRuntimeEvent>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(48).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(2048);
            entity.Property(x => x.PayloadJson).HasColumnType("text");
            entity.HasIndex(x => new { x.AgentRuntimeInstanceId, x.OccurredAt });
            entity.HasOne(x => x.AgentRuntimeInstance)
                .WithMany(x => x.Events)
                .HasForeignKey(x => x.AgentRuntimeInstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<McpAgentSession>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OrganizationId).HasMaxLength(200).IsRequired();
            entity.Property(x => x.PackageDigest).HasMaxLength(128).IsRequired();
            entity.Property(x => x.AccessTokenHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.PreviousAccessTokenHash).HasMaxLength(64);
            entity.Property(x => x.RevocationReason).HasMaxLength(1024);
            entity.HasIndex(x => x.AccessTokenHash).IsUnique();
            entity.HasIndex(x => new { x.RuntimeInstanceId, x.RevokedAt });
            entity.HasOne(x => x.RuntimeInstance).WithMany()
                .HasForeignKey(x => x.RuntimeInstanceId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.AgentInstallation).WithMany()
                .HasForeignKey(x => x.AgentInstallationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Domain.Communications.AgentCoordinationSession>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Subject).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Objective).HasMaxLength(4096).IsRequired();
            entity.Property(x => x.SuccessCriteriaJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
            entity.Property(x => x.FinalSummary).HasMaxLength(32768);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.OrganizationId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.ConversationId, x.Status });
            entity.HasIndex(x => new { x.OrganizationId, x.SourceConversationId, x.Status });
            entity.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Conversation>().WithMany().HasForeignKey(x => x.SourceConversationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ChatTurn>().WithMany().HasForeignKey(x => x.SourceChatTurnId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ConversationMessage>().WithMany().HasForeignKey(x => x.SourceMessageId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<OrganizationUser>().WithMany().HasForeignKey(x => x.InitiatorOrganizationUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<OrganizationUser>().WithMany().HasForeignKey(x => x.TargetOrganizationUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AgentInstallation>().WithMany().HasForeignKey(x => x.InitiatorInstallationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AgentInstallation>().WithMany().HasForeignKey(x => x.TargetInstallationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<OrganizationUser>().WithMany().HasForeignKey(x => x.CurrentOrganizationUserId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<AgentWorkItem>().WithMany().HasForeignKey(x => x.CurrentAgentWorkItemId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Domain.Communications.AgentCoordinationTurn>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Disposition).HasMaxLength(24).IsRequired();
            entity.Property(x => x.Content).HasMaxLength(32768).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.HasIndex(x => new { x.SessionId, x.Ordinal }).IsUnique();
            entity.HasIndex(x => new { x.SessionId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => x.EventId).IsUnique();
            entity.HasOne(x => x.Session).WithMany(x => x.Turns)
                .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<OrganizationUser>().WithMany().HasForeignKey(x => x.SpeakerOrganizationUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AgentWorkItem>().WithMany().HasForeignKey(x => x.AgentWorkItemId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<ConversationMessage>().WithMany().HasForeignKey(x => x.ConversationMessageId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AgentWorkItem>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OrganizationId).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(24).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(300).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.CausationId).HasMaxLength(128);
            entity.Property(x => x.SourceType).HasMaxLength(80);
            entity.Property(x => x.SourceId).HasMaxLength(200);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
            entity.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ResultHash).HasMaxLength(64);
            entity.Property(x => x.LastError).HasMaxLength(2048);
            entity.HasIndex(x => new { x.AgentInstallationId, x.Status, x.AvailableAt });
            entity.HasIndex(x => new { x.AgentInstallationId, x.IdempotencyKey }).IsUnique();
            entity.HasOne(x => x.AgentInstallation).WithMany()
                .HasForeignKey(x => x.AgentInstallationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentWorkAttempt>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.LeaseTokenHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CompletionHash).HasMaxLength(64);
            entity.Property(x => x.Error).HasMaxLength(2048);
            entity.HasIndex(x => new { x.AgentWorkItemId, x.Attempt }).IsUnique();
            entity.HasIndex(x => new { x.RuntimeInstanceId, x.LeaseExpiresAt });
            entity.HasOne(x => x.AgentWorkItem).WithMany(x => x.Attempts)
                .HasForeignKey(x => x.AgentWorkItemId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.RuntimeInstance).WithMany()
                .HasForeignKey(x => x.RuntimeInstanceId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AgentWorkProgress>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.AgentWorkAttemptId, x.Sequence }).IsUnique();
            entity.HasOne(x => x.AgentWorkItem).WithMany(x => x.Progress)
                .HasForeignKey(x => x.AgentWorkItemId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.AgentWorkAttempt).WithMany()
                .HasForeignKey(x => x.AgentWorkAttemptId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentCapabilityBinding>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OrganizationId).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Capability).HasMaxLength(300).IsRequired();
            entity.HasIndex(x => new { x.RequesterInstallationId, x.Capability })
                .IsUnique().HasFilter("\"RevokedAt\" IS NULL");
            entity.HasOne(x => x.RequesterInstallation).WithMany()
                .HasForeignKey(x => x.RequesterInstallationId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.ProviderInstallation).WithMany()
                .HasForeignKey(x => x.ProviderInstallationId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
