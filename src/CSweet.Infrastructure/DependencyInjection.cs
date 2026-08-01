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
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using CSweet.Memory;
using CSweet.Communications.Abstractions;
using CSweet.Infrastructure.WorkManagement;

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
        builder.Services.AddSingleton<IAuditExecutionContextAccessor, AuditExecutionContextAccessor>();
        builder.Services.AddSingleton<IAuditEventWriter, AuditEventWriter>();
        builder.Services.AddScoped<ISecurityAuditService, SecurityAuditService>();
        builder.Services.AddScoped<IScopedActionAuthorizationService, ScopedActionAuthorizationService>();
        builder.Services.AddScoped<IAgentRuntimeSettingsService, AgentRuntimeSettingsService>();
        builder.Services.AddScoped<AgentImportPreviewService>();
        builder.Services.AddScoped<IAgentImportPreviewService>(sp => sp.GetRequiredService<AgentImportPreviewService>());
        builder.Services.AddScoped<IPluginImportService>(sp => sp.GetRequiredService<AgentImportPreviewService>());
        builder.Services.AddScoped<IPluginArchiveImportService, PluginArchiveImportService>();
        builder.Services.AddSingleton<IPluginManifestReader, PluginManifestReader>();
        builder.Services.AddScoped<IAgentUpdateService, AgentUpdateService>();
        builder.Services.AddScoped<AgentInstallationService>();
        builder.Services.AddScoped<IAgentInstallationService>(sp => sp.GetRequiredService<AgentInstallationService>());
        builder.Services.AddScoped<IPluginInstallationService>(sp => sp.GetRequiredService<AgentInstallationService>());
        builder.Services.AddScoped<IPluginOrganizationGrantService, PluginOrganizationGrantService>();
        builder.Services.AddScoped<IPluginAuthorizationPolicy, PersistedPluginAuthorizationPolicy>();
        builder.Services.AddScoped<IPluginSecretStore, DataProtectionPluginSecretStore>();
        builder.Services.AddScoped<IAgentInstallationConfigurationService, AgentInstallationConfigurationService>();
        builder.Services.AddScoped<IAgentBuildService, AgentBuildService>();
        builder.Services.AddSingleton<DockerAgentBuildExecutor>();
        builder.Services.AddSingleton<IAgentBuildExecutor>(sp => sp.GetRequiredService<DockerAgentBuildExecutor>());
        builder.Services.AddSingleton<IPluginBuildExecutor>(sp => sp.GetRequiredService<DockerAgentBuildExecutor>());
        builder.Services.AddSingleton<IDockerCommandExecutor, DockerCommandExecutor>();
        builder.Services.AddSingleton<DockerAgentContainerRunner>();
        builder.Services.AddSingleton<IAgentContainerRunner>(sp => sp.GetRequiredService<DockerAgentContainerRunner>());
        builder.Services.AddSingleton<IPluginContainerRunner>(sp => sp.GetRequiredService<DockerAgentContainerRunner>());
        builder.Services.AddScoped<AgentRuntimeManager>();
        builder.Services.AddScoped<IAgentRuntimeManager>(sp => sp.GetRequiredService<AgentRuntimeManager>());
        builder.Services.AddScoped<IPluginRuntimeManager>(sp => sp.GetRequiredService<AgentRuntimeManager>());
        builder.Services.AddScoped<IAgentInteractiveRuntimeService, AgentInteractiveRuntimeService>();
        builder.Services.AddScoped<IAgentRuntimeSignalService, AgentRuntimeSignalService>();
        builder.Services.AddScoped<IAgentRuntimeCleanupService, AgentRuntimeCleanupService>();
        builder.Services.AddScoped<GitWorkspaceRetentionCleanupService>();
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
        builder.Services.AddScoped<IGenAiJobService, GenAiJobService>();
        builder.Services.AddSingleton<IMediaAssetStore, FileMediaAssetStore>();
        builder.Services.AddScoped<IMediaAssetService, MediaAssetService>();
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
        builder.Services.AddScoped<ICoreOrganizationService, CoreOrganizationService>();
        builder.Services.AddScoped<IRoleService, RoleService>();
        builder.Services.AddScoped<IStrategicObjectiveService, StrategicObjectiveService>();
        builder.Services.AddScoped<IWorkerService, WorkerService>();
        builder.Services.AddScoped<IWorkTaskService, WorkTaskService>();
        builder.Services.AddScoped<IWorkBoardService, WorkBoardService>();
        builder.Services.AddScoped<ISoftwareDevelopmentWorkService, SoftwareDevelopmentWorkService>();
        builder.Services.AddScoped<IWorkBoardGrantService, WorkBoardGrantService>();
        builder.Services.AddScoped<IWorkItemCollaborationService, WorkItemCollaborationService>();
        builder.Services.AddScoped<IWorkSprintService, WorkSprintService>();
        builder.Services.AddScoped<AgentWorkInbox>();
        builder.Services.AddScoped<IWorkOrchestrationService, WorkOrchestrationService>();
        builder.Services.AddScoped<IWorkOrchestrator, WorkOrchestrator>();
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
        builder.Services.AddScoped<IResourceChangeService, ResourceChangeService>();
        builder.Services.AddScoped<IApprovalDashboardService, ApprovalDashboardService>();
        builder.Services.AddScoped<ICommunicationWorkspaceService, CommunicationWorkspaceService>();
        builder.Services.AddScoped<ICommunicationHubService, CommunicationHubService>();
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
}
