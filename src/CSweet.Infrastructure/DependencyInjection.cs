using CSweet.Application.Core;
using CSweet.Application.Agents;
using CSweet.Application.Communications;
using CSweet.Application.Notifications;
using CSweet.Application.Auth;
using CSweet.Application.BusinessOnboarding;
using CSweet.Application.Llm;
using CSweet.Application.Planning;
using CSweet.Application.Setup;
using CSweet.AI.AgentFramework;
using CSweet.AI.Providers;
using CSweet.Infrastructure.BusinessOnboarding;
using CSweet.Infrastructure.Auth;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Notifications;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Planning;
using CSweet.Infrastructure.Setup;
using CSweet.Infrastructure.GenAi;
using CSweet.Application.GenAi;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.Marketplace;
using CSweet.Infrastructure.Agents;
using CSweet.Application.Security;
using CSweet.Application.Marketplace;
using CSweet.Application.WorkManagement;
using CSweet.Application.SourceControl;
using CSweet.Application.Analytics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using CSweet.Memory;
using CSweet.Communications.Abstractions;
using CSweet.Infrastructure.WorkManagement;
using CSweet.Infrastructure.SourceControl;
using CSweet.Infrastructure.Analytics;
using CSweet.TrustedServices;
using CSweet.AgentBroker;
using CSweet.ExecutionArtifacts;

namespace CSweet.Infrastructure;

public static class DependencyInjection
{
    public static IHostApplicationBuilder AddCSweetInfrastructure(this IHostApplicationBuilder builder)
    {
        var connectionString = builder.Configuration.GetConnectionString("Postgres")
            ?? builder.Configuration.GetConnectionString("csweet");

        builder.Services.AddDbContext<CSweetDbContext>(options =>
        {
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                options.UseNpgsql(connectionString);
                return;
            }

            throw new InvalidOperationException("ConnectionStrings:Postgres or ConnectionStrings:csweet must be configured.");
        });

        builder.Services.AddDataProtection()
            .SetApplicationName("CSweet")
            .PersistKeysToDbContext<CSweetDbContext>();

        // Identity's SignInManager depends on the authentication scheme provider even in
        // non-web hosts such as CSweet.Migrator. The API adds the cookie schemes separately.
        builder.Services.AddAuthentication();

        builder.Services
            .AddIdentityCore<ApplicationUser>(options =>
            {
                options.SignIn.RequireConfirmedAccount = true;
                options.User.RequireUniqueEmail = true;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddSignInManager()
            .AddEntityFrameworkStores<CSweetDbContext>()
            .AddDefaultTokenProviders();

        builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.SectionName));
        builder.Services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);
        builder.Services.AddScoped<IUserConfirmation<ApplicationUser>, RootUserConfirmation>();
        builder.Services.AddScoped<IEmailDeliveryConfigurationProvider, EmailDeliveryConfigurationProvider>();
        builder.Services.AddScoped<IAccountEmailSender, SmtpAccountEmailSender>();
        builder.Services.AddScoped<IEmailDeliveryProfileService, EmailDeliveryProfileService>();
        builder.Services.AddScoped<IAuthenticationService, AuthenticationService>();

        builder.Services.AddScoped<ISetupService, SetupService>();
        builder.Services.AddScoped<IExecutionFleetService, ExecutionFleetService>();
        builder.Services.AddScoped<IExecutionPoolAdministrationService, ExecutionPoolAdministrationService>();
        builder.Services.AddOptions<ExecutionFleetOptions>()
            .Bind(builder.Configuration.GetSection(ExecutionFleetOptions.SectionName));
        builder.Services.AddOptions<ExecutionNodeCertificateAuthorityOptions>()
            .Bind(builder.Configuration.GetSection(ExecutionNodeCertificateAuthorityOptions.SectionName));
        builder.Services.AddSingleton<IExecutionNodeCertificateAuthority, ExecutionNodeCertificateAuthority>();
        builder.Services.AddScoped<IExecutionWorkloadOrchestrator, ExecutionWorkloadOrchestrator>();
        builder.Services.AddScoped<ExecutionArtifactGrantLeaseService>();
        builder.Services.AddScoped<IExecutionBrokerSessionRunner, ExecutionBrokerSessionRunner>();
        builder.Services.AddSingleton<IAuditExecutionContextAccessor, AuditExecutionContextAccessor>();
        builder.Services.AddSingleton<IAuditEventWriter, AuditEventWriter>();
        builder.Services.AddScoped<ISecurityAuditService, SecurityAuditService>();
        builder.Services.AddScoped<IScopedActionAuthorizationService, ScopedActionAuthorizationService>();
        builder.Services.AddScoped<IAgentRuntimeSettingsService, AgentRuntimeSettingsService>();
        var artifactRoot = ResolveAgentRuntimePath(
            builder.Configuration,
            "CSweet:AgentRuntime:Artifacts:RootPath",
            "artifacts");
        var artifactStore = builder.Configuration.GetSection(ArtifactStoreOptions.SectionName)
            .Get<ArtifactStoreOptions>() ?? new ArtifactStoreOptions();
        artifactStore.RootPath = artifactRoot;
        var artifactStoreProvider = artifactStore.ValidatedProvider();
        builder.Services.AddSingleton(artifactStore);
        builder.Services.AddSingleton<IAgentArtifactSigner, DataProtectionAgentArtifactSigner>();
        builder.Services.AddSingleton<FileSystemAgentArtifactStore>();
        if (artifactStoreProvider == "s3")
        {
            var s3 = builder.Configuration.GetSection(S3ArtifactStoreOptions.SectionName)
                .Get<S3ArtifactStoreOptions>() ?? new S3ArtifactStoreOptions();
            s3.Validate();
            builder.Services.AddSingleton(s3);
            builder.Services.AddSingleton<IS3ArtifactObjectClient, AmazonS3ArtifactObjectClient>();
            builder.Services.AddSingleton<IAgentArtifactStore, S3AgentArtifactStore>();
        }
        else
        {
            builder.Services.AddSingleton<IAgentArtifactStore>(services =>
                services.GetRequiredService<FileSystemAgentArtifactStore>());
        }
        var agentHostBroker = builder.Configuration.GetSection(AgentHostBrokerOptions.SectionName)
            .Get<AgentHostBrokerOptions>() ?? new AgentHostBrokerOptions();
        // Aspire gives each named endpoint a concrete, host-reachable address. Prefer that
        // address when available so runtime broker traffic never falls through to DNS for
        // the logical resource name (which is not resolvable by a Windows host process).
        var aspireAgentHostEndpoint = builder.Configuration["AGENTHOST_MCP"];
        if (!string.IsNullOrWhiteSpace(aspireAgentHostEndpoint))
            agentHostBroker.BaseUrl = aspireAgentHostEndpoint;
        var agentHostBaseUri = agentHostBroker.ValidatedBaseUri();
        builder.Services.AddSingleton(agentHostBroker);
        builder.Services.AddHttpClient(nameof(AgentHostBrokerOperationHandler), client =>
        {
            client.BaseAddress = agentHostBaseUri;
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        builder.Services.AddSingleton<IAgentBrokerOperationHandler, AgentHostBrokerOperationHandler>();
        builder.Services.AddScoped<AgentImportPreviewService>();
        builder.Services.AddScoped<IAgentImportPreviewService>(sp => sp.GetRequiredService<AgentImportPreviewService>());
        builder.Services.AddScoped<IPluginImportService>(sp => sp.GetRequiredService<AgentImportPreviewService>());
        builder.Services.AddScoped<IPluginArchiveImportService, PluginArchiveImportService>();
        builder.Services.AddSingleton<IPluginManifestReader, PluginManifestReader>();
        builder.Services.AddScoped<IAgentUpdateService, AgentUpdateService>();
        builder.Services.AddScoped<IAgentDefinitionService, AgentDefinitionService>();
        builder.Services.AddScoped<AgentInstallationService>();
        builder.Services.AddScoped<IAgentInstallationService>(sp => sp.GetRequiredService<AgentInstallationService>());
        builder.Services.AddScoped<IPluginInstallationService>(sp => sp.GetRequiredService<AgentInstallationService>());
        builder.Services.AddScoped<IPluginOrganizationGrantService, PluginOrganizationGrantService>();
        builder.Services.AddScoped<IPluginAuthorizationPolicy, PersistedPluginAuthorizationPolicy>();
        builder.Services.AddScoped<IPluginSecretStore, DataProtectionPluginSecretStore>();
        builder.Services.Configure<PluginConnectionOptions>(builder.Configuration.GetSection(PluginConnectionOptions.SectionName));
        builder.Services.AddHttpClient(nameof(PluginSetupService), client => client.Timeout = TimeSpan.FromSeconds(30));
        builder.Services.AddHttpClient(nameof(PluginOAuthTokenBroker), client => client.Timeout = TimeSpan.FromSeconds(30));
        builder.Services.AddScoped<IPluginOAuthTokenBroker, PluginOAuthTokenBroker>();
        builder.Services.AddScoped<IPluginProviderProfileRegistry, PluginProviderProfileRegistry>();
        builder.Services.AddScoped<IPluginStandingPolicyService, PluginStandingPolicyService>();
        builder.Services.AddScoped<IPluginSetupService, PluginSetupService>();
        builder.Services.AddScoped<IPluginBootstrapCapabilityService, PluginBootstrapCapabilityService>();
        builder.Services.AddScoped<AgentInstallationConfigurationService>();
        builder.Services.AddScoped<IAgentInstallationConfigurationService>(sp =>
            sp.GetRequiredService<AgentInstallationConfigurationService>());
        builder.Services.AddScoped<IAgentConfigurationService>(sp =>
            sp.GetRequiredService<AgentInstallationConfigurationService>());
        builder.Services.AddScoped<IAgentBuildService, AgentBuildService>();
        builder.Services.AddScoped<IGuestImageRegistry, FleetGuestImageRegistry>();
        builder.Services.AddSingleton<InMemoryBuilderArtifactResultStore>();
        builder.Services.AddSingleton<IBuilderArtifactResultStore>(sp => sp.GetRequiredService<InMemoryBuilderArtifactResultStore>());
        builder.Services.AddSingleton<IBuilderArtifactResultPublisher>(sp => sp.GetRequiredService<InMemoryBuilderArtifactResultStore>());
        builder.Services.AddScoped<FleetAgentBuildExecutor>();
        builder.Services.AddScoped<IAgentBuildExecutor>(sp => sp.GetRequiredService<FleetAgentBuildExecutor>());
        builder.Services.AddScoped<IPluginBuildExecutor>(sp => sp.GetRequiredService<FleetAgentBuildExecutor>());
        builder.Services.AddScoped<FleetAgentWorkloadRunner>();
        builder.Services.AddScoped<IAgentWorkloadRunner>(sp => sp.GetRequiredService<FleetAgentWorkloadRunner>());
        builder.Services.AddScoped<IPluginWorkloadRunner>(sp => sp.GetRequiredService<FleetAgentWorkloadRunner>());
        builder.Services.AddScoped<AgentRuntimeManager>();
        builder.Services.AddScoped<IAgentRuntimeManager>(sp => sp.GetRequiredService<AgentRuntimeManager>());
        builder.Services.AddScoped<IAgentRuntimeEligibilityService, AgentRuntimeEligibilityService>();
        builder.Services.AddScoped<IPluginRuntimeManager>(sp => sp.GetRequiredService<AgentRuntimeManager>());
        builder.Services.AddScoped<IAgentInteractiveRuntimeService, AgentInteractiveRuntimeService>();
        builder.Services.AddScoped<IAgentRuntimeSignalService, AgentRuntimeSignalService>();
        builder.Services.AddScoped<IAgentRuntimeCleanupService, AgentRuntimeCleanupService>();
        builder.Services.AddScoped<AgentRuntimeStartupCleanupService>();
        builder.Services.AddOptions<AgentRuntimeManagerOptions>()
            .Bind(builder.Configuration.GetSection(AgentRuntimeManagerOptions.SectionName));
        builder.Services.AddHttpClient<GitHubAgentRepositoryClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.github.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CSweet-Agent-Importer/1.0");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        builder.Services.AddScoped<IGitHubAgentRepositoryClient>(sp => sp.GetRequiredService<GitHubAgentRepositoryClient>());
        builder.Services.AddScoped<IPluginSourceResolver>(sp => sp.GetRequiredService<GitHubAgentRepositoryClient>());
        builder.Services.AddSingleton<ILlmProviderSecretStore>(_ =>
        {
            if (builder.Environment.IsEnvironment("Testing"))
            {
                return new InMemoryLlmProviderSecretStore();
            }

            return new FileLlmProviderSecretStore(GetLocalStateFilePath(
                builder.Configuration,
                "CSweet:Secrets:FilePath",
                "provider-secrets.json"));
        });
        builder.Services.AddScoped(_ => new OpenAiCompatibleProviderClient(new HttpClient
        {
            // Local models may need substantial time for a cold load before the first
            // chat token, especially large BF16 models. Optional probes have their own
            // shorter cancellation windows in LlmConnectionTester.
            Timeout = TimeSpan.FromMinutes(3)
        }));
        builder.Services.AddScoped<ILlmProviderFactory, OpenAiCompatibleLlmProviderFactory>();
        builder.Services.AddScoped<ILlmConnectionTester, LlmConnectionTester>();
        builder.Services.AddScoped<IModelCatalogClient, ModelCatalogClient>();
        builder.Services.AddScoped<ILlmProviderProfileService, LlmProviderProfileService>();
        builder.Services.AddScoped<ILocalLlmProviderDiscoveryService, LocalLlmProviderDiscoveryService>();
        builder.Services.AddScoped<ILlmTokenUsageService, LlmTokenUsageService>();
        builder.Services.AddScoped<IInferenceAnalyticsService, InferenceAnalyticsService>();
        builder.Services.AddHttpClient("GenAi", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CSweet-Core/1.0");
        });
        builder.Services.AddScoped<IGenAiProviderAdapter, ComfyUiLocalGenAiProviderAdapter>();
        builder.Services.AddScoped<IGenAiProviderAdapter, ComfyUiCloudGenAiProviderAdapter>();
        builder.Services.AddScoped<IGenAiProviderAdapter, OpenAiGenAiProviderAdapter>();
        builder.Services.AddScoped<IGenAiProviderAdapter, GoogleGeminiGenAiProviderAdapter>();
        builder.Services.AddScoped<IGenAiProviderAdapter, ReplicateGenAiProviderAdapter>();
        builder.Services.AddScoped<IGenAiProviderProfileService, GenAiProviderProfileService>();
        builder.Services.AddScoped<ILocalGenAiProviderDiscoveryService, LocalGenAiProviderDiscoveryService>();
        builder.Services.AddScoped<IGenAiJobService, GenAiJobService>();
        builder.Services.Configure<MediaAssetStorageOptions>(builder.Configuration.GetSection(MediaAssetStorageOptions.SectionName));
        builder.Services.AddSingleton<IMediaAssetStore, FileMediaAssetStore>();
        builder.Services.AddScoped<IMediaAssetService, MediaAssetService>();
        builder.Services.AddSingleton<IResumableMediaUploadStore, FileResumableMediaUploadStore>();
        builder.Services.AddScoped<IResumableMediaUploadService, ResumableMediaUploadService>();
        builder.Services.AddScoped<IAgentRunLogWriter, AgentRunLogWriter>();
        builder.Services.AddScoped<IAgentRunner, AgentFrameworkAgentRunner>();
        builder.Services.AddScoped<IAgentWorkflowRunner, AgentFrameworkWorkflowRunner>();
        builder.Services.AddOptions<MarketplaceOptions>()
            .Bind(builder.Configuration.GetSection(MarketplaceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.Services.AddHttpClient<MarketplaceDiscoveryClient>((services, client) =>
        {
            var marketplace = services.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<MarketplaceOptions>>().Value;
            client.BaseAddress = new Uri(
                marketplace.BaseUrl.EndsWith('/') ? marketplace.BaseUrl : $"{marketplace.BaseUrl}/");
            client.Timeout = TimeSpan.FromSeconds(marketplace.TimeoutSeconds);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CSweet-Core/1.0");
        });
        builder.Services.AddScoped<IMarketplaceDiscoveryService>(
            services => services.GetRequiredService<MarketplaceDiscoveryClient>());
        builder.Services.AddOptions<AgentCatalogOptions>()
            .Bind(builder.Configuration.GetSection(AgentCatalogOptions.SectionName));
        builder.Services.AddScoped<IAgentCatalogProvider, InstalledAgentCatalogProvider>();
        builder.Services.AddScoped<LocalDirectoryAgentCatalogProvider>();
        builder.Services.AddScoped<IAgentCatalogProvider>(
            services => services.GetRequiredService<LocalDirectoryAgentCatalogProvider>());
        builder.Services.AddScoped<ILocalAgentSourceArchiveService>(
            services => services.GetRequiredService<LocalDirectoryAgentCatalogProvider>());
        builder.Services.AddScoped<IAgentCatalogProvider, FirstPartyAgentCatalogProvider>();
        builder.Services.AddScoped<IAgentCatalogProvider, MarketplaceAgentCatalogProvider>();
        builder.Services.AddScoped<IAgentCatalogService, AgentCatalogService>();

        // Planning services
        builder.Services.AddScoped<IPlanningRunService, PlanningRunService>();
        builder.Services.AddScoped<IPlanningDocumentService, PlanningDocumentService>();
        builder.Services.AddScoped<IPlanningWorkflowService, PlanningWorkflowService>();

        // Core business domain services
        builder.Services.AddScoped<IBusinessOnboardingService, BusinessOnboardingService>();
        builder.Services.AddScoped<IBusinessOnboardingOperationService>(services =>
            (BusinessOnboardingService)services.GetRequiredService<IBusinessOnboardingService>());
        builder.Services.AddScoped<ICoreOrganizationService, CoreOrganizationService>();
        builder.Services.AddScoped<IRoleService, RoleService>();
        builder.Services.AddScoped<IStrategicObjectiveService, StrategicObjectiveService>();
        builder.Services.AddScoped<IWorkerService, WorkerService>();
        builder.Services.AddScoped<IWorkTaskService, WorkTaskService>();
        builder.Services.AddScoped<IWorkBoardService, WorkBoardService>();
        builder.Services.AddScoped<IWorkItemMutationEngine, WorkItemMutationEngine>();
        builder.Services.AddScoped<IPersonalTodoService>(services =>
            new PersonalTodoService(services.GetRequiredService<IWorkItemMutationEngine>()));
        builder.Services.AddScoped<IEmployeeHierarchyAccessService, EmployeeHierarchyAccessService>();
        builder.Services.AddScoped<IEmployeeDetailsService, EmployeeDetailsService>();
        builder.Services.AddScoped<IEmployeeAssignedWorkQueryService, EmployeeAssignedWorkQueryService>();
        builder.Services.AddSingleton<IWorkBoardBehavior, StandardBoardBehavior>();
        builder.Services.AddSingleton<IWorkBoardBehavior, HumanPersonalBoardBehavior>();
        builder.Services.AddSingleton<IWorkBoardBehavior, AgentPersonalBoardBehavior>();
        builder.Services.AddScoped<ISoftwareDevelopmentWorkService, SoftwareDevelopmentWorkService>();
        builder.Services.AddScoped<RepositoryProvisioningProcessor>();
        builder.Services.AddScoped<SourceControlPlatformSetupService>();
        builder.Services.AddScoped<ISourceControlPlatformSetupService>(services =>
            services.GetRequiredService<SourceControlPlatformSetupService>());
        builder.Services.AddScoped<ISourceControlPlatformConfigurationProvider>(services =>
            services.GetRequiredService<SourceControlPlatformSetupService>());
        builder.Services.AddScoped<ISourceControlOnboardingService, SourceControlOnboardingService>();
        builder.Services.AddScoped<ISourceControlApprovalService, SourceControlApprovalService>();
        builder.Services.AddHttpClient<IPlatformGitHubManifestClient, GitHubAppManifestClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.github.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CSweet-Platform-Setup/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        builder.Services.AddHttpClient<IGitHubUserAuthorizationClient, GitHubUserAuthorizationClient>(client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CSweet-Core/2.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        builder.Services.AddSingleton<WorkspaceArtifactValidator>();
        builder.Services.AddScoped<IWorkspaceVolumeBridge, WorkspaceVolumeBridge>();
        builder.Services.AddScoped<IAgentWorkspaceBroker, AgentWorkspaceBroker>();
        builder.Services.TryAddSingleton<TrustedRequestReplayCache>();
        builder.Services.Configure<AgentBrokerAuthenticationOptions>(options =>
        {
            options.KeyId = builder.Configuration["CSweet:SourceControl:AgentBrokerKeyId"] ?? "agenthost";
            options.SharedKeyBase64 = builder.Configuration["CSweet:SourceControl:AgentBrokerKeyBase64"] ?? string.Empty;
        });
        builder.Services.AddScoped<IWorkBoardGrantService, WorkBoardGrantService>();
        builder.Services.AddScoped<IWorkItemCollaborationService, WorkItemCollaborationService>();
        builder.Services.AddScoped<IWorkSprintService, WorkSprintService>();
        builder.Services.AddScoped<AgentWorkInbox>();
        builder.Services.AddScoped<IWorkOrchestrationService, WorkOrchestrationService>();
        builder.Services.AddScoped<IWorkOrchestrator, WorkOrchestrator>();
        var trustedServiceKey = builder.Configuration["CSweet:SourceControl:TrustedServiceKeyBase64"];
        var trustedServiceKeyId = builder.Configuration["CSweet:SourceControl:TrustedServiceKeyId"] ?? "core";
        var hasTrustedServiceKey = TryDecodeTrustedServiceKey(trustedServiceKey);
        if (hasTrustedServiceKey)
        {
            builder.Services.Configure<TrustedServiceAuthenticationOptions>(options =>
            {
                options.KeyId = trustedServiceKeyId;
                options.SharedKeyBase64 = trustedServiceKey!;
            });
            builder.Services.AddTransient<TrustedServiceAuthenticationHandler>();
        }

        var gitHostBaseUrl = builder.Configuration["CSweet:SourceControl:GitHostBaseUrl"];
        if (hasTrustedServiceKey && TryGetTrustedServiceUri(gitHostBaseUrl, out var gitHostUri))
        {
            builder.Services.AddHttpClient<TrustedSourceControlHostClient>(client =>
                client.BaseAddress = gitHostUri)
                .AddHttpMessageHandler<TrustedServiceAuthenticationHandler>();
            builder.Services.AddTransient<ITrustedSourceControlHostClient>(services =>
                services.GetRequiredService<TrustedSourceControlHostClient>());
        }
        else
        {
            builder.Services.AddSingleton<ITrustedSourceControlHostClient,
                UnavailableTrustedSourceControlHostClient>();
        }

        var provisionerHostBaseUrl = builder.Configuration["CSweet:SourceControl:ProvisionerHostBaseUrl"];
        if (hasTrustedServiceKey && TryGetTrustedServiceUri(provisionerHostBaseUrl, out var provisionerHostUri))
        {
            builder.Services.AddHttpClient<TrustedProvisioningHostClient>(client =>
                client.BaseAddress = provisionerHostUri)
                .AddHttpMessageHandler<TrustedServiceAuthenticationHandler>();
            builder.Services.AddTransient<ITrustedProvisioningHostClient>(services =>
                services.GetRequiredService<TrustedProvisioningHostClient>());
        }
        else
        {
            builder.Services.AddSingleton<ITrustedProvisioningHostClient,
                UnavailableTrustedProvisioningHostClient>();
        }
        builder.Services.AddSingleton<ISourceControlDecisionSigner,
            DataProtectionSourceControlDecisionSigner>();
        builder.Services.AddScoped<ITrustedWorkActionExecutor, GovernedMergeWorkActionExecutor>();
        builder.Services.AddScoped<ITaskRunService, TaskRunService>();
        builder.Services.AddScoped<IArtifactService, ArtifactService>();
        builder.Services.AddScoped<IArtifactApprovalService, ArtifactApprovalService>();
        builder.Services.AddScoped<IOrganizationUserService, OrganizationUserService>();
        builder.Services.AddScoped<ITeamService, TeamService>();
        builder.Services.AddScoped<IExecutiveBriefingService, ExecutiveBriefingService>();
        builder.Services.AddScoped<IConversationService, ConversationService>();
        builder.Services.AddScoped<IChatTurnService, ChatTurnService>();
        builder.Services.AddScoped<HiringService>();
        builder.Services.AddScoped<IHiringService>(services => services.GetRequiredService<HiringService>());
        builder.Services.AddScoped<IAgentHireOrchestrator>(services => services.GetRequiredService<HiringService>());
        builder.Services.AddScoped<IAgentHireOperationService>(services => services.GetRequiredService<HiringService>());
        builder.Services.AddScoped<IResourceChangeService, ResourceChangeService>();
        builder.Services.AddScoped<IApprovalDashboardService, ApprovalDashboardService>();
        builder.Services.AddScoped<ICommunicationWorkspaceService, CommunicationWorkspaceService>();
        builder.Services.AddScoped<ICommunicationHubService, CommunicationHubService>();
        builder.Services.AddScoped<IAgentCoordinationService, AgentCoordinationService>();
        builder.Services.AddScoped<IUserActionService, UserActionService>();
        builder.Services.AddScoped<IUserActionWorkflowResolver, HiringMarketplaceUserActionWorkflowResolver>();
        builder.Services.AddScoped<IExecutiveDecisionService, ExecutiveDecisionService>();
        builder.Services.AddScoped<IAgentCommunicationOnboardingService, AgentCommunicationOnboardingService>();
        builder.Services.AddScoped<IApplicationRealtimeOutboxDispatcher, ApplicationRealtimeOutboxDispatcher>();
        builder.Services.AddScoped<ICommunicationEventOutboxDispatcher, CommunicationEventOutboxDispatcher>();
        builder.Services.AddScoped<ICommunicationRouter, CommunicationRouter>();
        builder.Services.AddScoped<ICommunicationIngressHandler, CommunicationIngressHandler>();
        builder.Services.AddScoped<INotificationService, NotificationService>();
        if (builder.Environment.IsEnvironment("Testing"))
        {
            builder.Services.TryAddSingleton<IMemoryStore>(_ => new SqliteMemoryStore(
                Path.Combine(Path.GetTempPath(), $"csweet-memory-tests-{Environment.ProcessId}.db")));
        }
        else
        {
            builder.Services.TryAddScoped<IMemoryStore>(_ => new PostgreSqlMemoryStore(
                connectionString ?? throw new InvalidOperationException("A PostgreSQL connection is required for memory.")));
        }
        builder.Services.AddScoped<IAgentMemoryService, AgentMemoryService>();
        builder.Services.TryAddSingleton(TimeProvider.System);

        return builder;
    }

    private static string GetLocalStateFilePath(
        IConfiguration configuration,
        string configurationKey,
        string fileName)
    {
        var configuredPath = configuration[configurationKey];
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(GetLocalStateDirectory(), fileName)
            : configuredPath;
        return path;
    }

    private static string GetLocalStateDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData)
            ? Path.Combine(AppContext.BaseDirectory, ".csweet")
            : Path.Combine(localAppData, "CSweet");
    }

    private static string ResolveAgentRuntimePath(
        IConfiguration configuration,
        string configurationKey,
        string childDirectory)
    {
        var configured = configuration[configurationKey];
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var root = OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(commonData)
            ? Path.Combine(commonData, "CSweet", "AgentRuntime")
            : Path.Combine(GetLocalStateDirectory(), "AgentRuntime");
        return Path.Combine(root, childDirectory);
    }

    private static bool TryDecodeTrustedServiceKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        try
        {
            return Convert.FromBase64String(value).Length >= 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryGetTrustedServiceUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) &&
            (parsed.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
             parsed.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
             parsed.Scheme.Equals("https+http", StringComparison.OrdinalIgnoreCase)))
        {
            uri = parsed.AbsoluteUri.EndsWith('/') ? parsed : new Uri($"{parsed.AbsoluteUri}/");
            return true;
        }
        uri = null!;
        return false;
    }
}
