using CSweet.Api.PersonalAssistant;
using CSweet.Api.Agents;
using CSweet.Api.BusinessOnboarding;
using CSweet.Infrastructure.BusinessOnboarding;
using CSweet.Api.Auth;
using CSweet.Api.Chat;
using CSweet.Api.Communications;
using CSweet.Api.Core;
using CSweet.Api.Llm;
using CSweet.Api.Planning;
using CSweet.Api.Setup;
using CSweet.Application.Planning;
using CSweet.Infrastructure;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Agents;
using CSweet.Infrastructure.Communications;
using CSweet.Api.Notifications;
using CSweet.Api.Security;
using CSweet.Api.Marketplace;
using CSweet.Api.GenAi;
using CSweet.Application.Notifications;
using CSweet.Api.WorkManagement;
using CSweet.Api.Analytics;
using CSweet.Api.SourceControl;
using CSweet.TrustedServices;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);
if (builder.Environment.IsDevelopment())
    builder.Services.AddHostedService<LocalWebPreviewWorker>();


builder.Configuration.AddJsonFile(
    "first-party-agents.json",
    optional: false,
    reloadOnChange: true);

builder.AddServiceDefaults();
builder.AddCSweetInfrastructure();
builder.Services.Configure<CSweet.Infrastructure.Compute.OfficeComputeCatalogOptions>(builder.Configuration.GetSection("CSweet:OfficeCompute"));
builder.Services.AddHostedService<AgentCatalogWarmupService>();
builder.Services.AddChatGateway(builder.Configuration);
builder.Services.AddCommunicationPluginRuntime();
builder.Services.AddAgentManagement();
builder.Services.AddAgentRateLimiting();
// Hosted first run (Installation section): C-Sweet account linking. One long-lived client so discovery stays cached.
builder.Services.Configure<CSweet.HostedAccount.HostedAccountOptions>(builder.Configuration.GetSection(CSweet.HostedAccount.HostedAccountOptions.SectionName));
builder.Services.AddSingleton(sp => new CSweet.HostedAccount.HostedAccountClient(
    new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) },
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CSweet.HostedAccount.HostedAccountOptions>>(),
    TimeProvider.System));
builder.Services.AddSingleton(sp => new CSweet.HostedAccount.HostedSetupCoordinator(
    sp.GetRequiredService<CSweet.HostedAccount.HostedAccountClient>(), TimeProvider.System,
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CSweet.HostedAccount.HostedAccountOptions>>().Value.ReservedOwnerSubject));
builder.Services.AddSingleton(sp => new CSweet.HostedAccount.DirectoryPublisher(
    sp.GetRequiredService<CSweet.HostedAccount.HostedAccountClient>(),
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CSweet.HostedAccount.HostedAccountOptions>>(), TimeProvider.System));
builder.Services.AddSingleton<CSweet.Api.Auth.DirectoryPublicationSignal>();
builder.Services.AddScoped<CSweet.HostedAccount.IDirectoryPublicationStore>(sp => new CSweet.Api.Auth.EfDirectoryPublicationStore(
    sp.GetRequiredService<CSweet.Infrastructure.Persistence.CSweetDbContext>(),
    sp.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(), TimeProvider.System,
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CSweet.HostedAccount.HostedAccountOptions>>()));
builder.Services.AddHostedService<CSweet.Api.Auth.DirectoryPublicationService>();
builder.Services.Configure<CSweet.Api.Auth.HeadquartersRelayOptions>(builder.Configuration.GetSection(CSweet.Api.Auth.HeadquartersRelayOptions.Section));
builder.Services.AddHostedService<CSweet.Api.Auth.MobileRelayConnectorService>();
builder.Services.AddScoped<CSweet.Api.Auth.MobileDeviceService>();
builder.Services.AddSingleton<CSweet.Api.Auth.MobileRequestDispatcher>();
builder.Services.AddSingleton<CSweet.Api.Auth.InstallationLinkSignal>();
builder.Services.Configure<CSweet.Api.Auth.MobileShellOptions>(builder.Configuration.GetSection(CSweet.Api.Auth.MobileShellOptions.Section));
builder.Services.AddSingleton<CSweet.Api.Auth.MobileWebAssets>();
CSweet.Api.Compute.ComputeProviderEndpoints.AddComputeProviderIngress(builder.Services);
builder.Services.AddHostedService<CSweet.Api.Compute.ComputeLocalSetupWorker>();
builder.Services.AddHostedService<CSweet.Api.Compute.ComputeFailedProvisionCleanupWorker>();
builder.Services.AddHostedService<MemoryCaptureWorker>();
builder.Services.AddHostedService<ChatTurnWorker>();
builder.Services.AddHostedService<ArtifactReviewJobWorker>();
builder.Services.AddHostedService<ArtifactAccessExpiryWorker>();
builder.Services.AddHostedService<OrganizationDeletionWorker>();
builder.Services.AddHostedService<CSweet.Infrastructure.Setup.ResourceCleanupWorker>();
builder.Services.AddHostedService<SourceControlPlatformReconciliationWorker>();
builder.Services.AddHostedService<TaskDeliveryWorker>();
builder.Services.AddHostedService<MediaUploadCleanupWorker>();
builder.Services.AddSingleton<IChatTurnEventRouter, ChatTurnEventRouter>();
builder.Services.Configure<ChatTurnOptions>(builder.Configuration.GetSection("ChatTurns"));
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = builder.Environment.IsDevelopment()
        ? "CSweet.Auth"
        : "__Host-CSweet.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.Events = new CookieAuthenticationEvents
    {
        OnValidatePrincipal = HostedWebSignIn.ValidateSessionAsync,
        OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        },
        OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }
    };
});
builder.AddHostedWebSignIn();
var authorization = builder.Services.AddAuthorizationBuilder();
authorization.AddPolicy("PluginAdministration", policy =>
{
    if (builder.Environment.IsEnvironment("Testing")) policy.RequireAssertion(_ => true);
    else policy.RequireRole(CSweet.Infrastructure.Auth.AuthenticationService.AdministratorRole);
});
authorization.AddPolicy("SourceControlAdministration", policy =>
{
    if (builder.Environment.IsEnvironment("Testing")) policy.RequireAssertion(_ => true);
    else policy.RequireRole(CSweet.Infrastructure.Auth.AuthenticationService.AdministratorRole);
});
authorization.AddPolicy("HostAdministration", policy =>
{
    if (builder.Environment.IsEnvironment("Testing")) policy.RequireAssertion(_ => true);
    else policy.RequireRole(CSweet.Infrastructure.Auth.AuthenticationService.AdministratorRole);
});
authorization.SetFallbackPolicy(builder.Environment.IsEnvironment("Testing")
    ? new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAssertion(_ => true)
        .Build()
    : new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = builder.Environment.IsDevelopment()
        ? "CSweet.Antiforgery"
        : "__Host-CSweet.Antiforgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.HeaderName = "X-CSWEET-CSRF";
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("DevelopmentBlazorApp", policy =>
    {
        policy.SetIsOriginAllowed(IsDevelopmentLoopbackOrigin)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
            .WithOwnerFailureHeaders();
    });
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddProblemDetails();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                               ForwardedHeaders.XForwardedHost |
                               ForwardedHeaders.XForwardedProto;
    foreach (var proxy in builder.Configuration.GetSection("CSweet:Http:KnownProxies").Get<string[]>() ?? [])
        options.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
});
builder.Services.AddSignalR();
builder.Services.AddSingleton<IApplicationRealtimePublisher, SignalRApplicationRealtimePublisher>();
builder.Services.AddHostedService<ApplicationRealtimeOutboxWorker>();
builder.Services.AddHostedService<WorkOrchestrationWorker>();
builder.Services.AddHostedService<AgentHireOperationWorker>();
builder.Services.AddHostedService<BusinessOnboardingOperationWorker>();
builder.Services.AddHostedService<CSweet.Infrastructure.PersonalAssistant.RepositoryOnboardingWorker>();

builder.Services.AddHttpClient("ComputeDownloadMetadata", client => client.Timeout = TimeSpan.FromSeconds(5))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<CSweet.Api.Compute.ComputeDownloadProgress>();

var app = builder.Build();
if (builder.Configuration.GetValue<bool>("Installation:ServePackagedWeb") ||
    (OperatingSystem.IsWindows() && Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService()))
{
    var assets = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "web")));
    app.Lifetime.ApplicationStopped.Register(assets.Dispose);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = assets });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = assets,
        ContentTypeProvider = CSweet.Api.Auth.MobileWebAssets.CreateTypes()
    });
    app.MapFallback((HttpRequest request) =>
        HttpMethods.IsGet(request.Method) && !request.Path.StartsWithSegments("/api") &&
        !request.Path.StartsWithSegments("/internal") && !request.Path.StartsWithSegments("/hubs") &&
        request.GetTypedHeaders().Accept?.Any(media => media.MediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)) == true
            ? Results.File(Path.Combine(assets.Root, "index.html"), "text/html") : Results.NotFound()).AllowAnonymous();
}
// Phone requests (H4) enter here, in process, and run through the whole pipeline below. Routing is explicit so the
// captured pipeline includes it; WebApplication would otherwise add routing ahead of this middleware.
app.Use(next =>
{
    app.Services.GetRequiredService<CSweet.Api.Auth.MobileRequestDispatcher>().Pipeline = next;
    return next;
});
app.UseRouting();
app.UseMiddleware<CSweet.Api.Auth.MobileDispatchGateMiddleware>();
app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseAgentBrokerAuthentication();

if (app.Environment.IsDevelopment())
{
    await CSweetDatabaseInitializer.EnsureDatabaseReadyAsync(app.Services);
    app.UseCors("DevelopmentBlazorApp");

    // Seed planning workflows on startup in development
    using (var scope = app.Services.CreateScope())
    {
        var workflowService = scope.ServiceProvider.GetRequiredService<IPlanningWorkflowService>();
        await workflowService.EnsureSeededAsync();
    }
}

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<AuditExecutionContextMiddleware>();
app.UseMiddleware<ApiAntiforgeryMiddleware>();
app.UseMiddleware<FirstRunSetupGuardMiddleware>();
app.UseMiddleware<BenchmarkHumanPolicyMiddleware>();

app.MapGet("/api/health", () => new { status = "ok", service = "CSweet.Api" }).AllowAnonymous();

app.MapHealthChecks("/health").AllowAnonymous();
app.MapAuthenticationEndpoints();
app.MapHostedSetupEndpoints();
app.MapHostedWebSignIn();
app.MapHostedOffices();
app.MapMobileDeviceEndpoints();
app.MapInstallationLinkEndpoints();

app.MapLlmProviderProfileEndpoints();
app.MapGenAiEndpoints();
app.MapSetupEndpoints();
app.MapOfficeBootstrapEndpoints();
app.MapExecutionFleetEndpoints();
CSweet.Api.Compute.ComputeAdministrationEndpoints.MapComputeAdministrationEndpoints(app);
CSweet.Api.Compute.ComputeDashboardEndpoints.MapComputeDashboardEndpoints(app);
CSweet.Api.Compute.ComputeProviderEndpoints.MapComputeProviderEndpoints(app);
app.MapAgentRuntimeSettingsEndpoints();
app.MapPlanningRunEndpoints();
app.MapPlanningWorkflowEndpoints();

// Core business domain endpoints
app.MapBusinessOnboardingEndpoints();
app.MapPersonalAssistantEndpoints();
app.MapCoreOrganizationEndpoints();
app.MapOrganizationUserEndpoints();
app.MapEmployeeEndpoints();
app.MapTeamEndpoints();
app.MapHiringEndpoints();
app.MapApprovalEndpoints();
app.MapResourceFulfillmentEndpoints();
app.MapWorkstreamInspectionEndpoints();
app.MapProjectSetupEndpoints();
app.MapToolchainCertificationEndpoints();
app.MapExecutiveBriefingEndpoints();
app.MapCompanyDashboardEndpoints();
app.MapRoleEndpoints();
app.MapStrategicObjectiveEndpoints();
app.MapWorkerEndpoints();
app.MapWorkTaskEndpoints();
app.MapTaskRunEndpoints();
app.MapArtifactEndpoints();
app.MapDocumentEndpoints();
app.MapCalendarEndpoints();
app.MapAgentMemoryEndpoints();
app.MapCommunicationEndpoints();
app.MapAgentManagementEndpoints();
app.MapAgentOnboardingRecoveryEndpoints();
app.MapPluginManagementEndpoints();
app.MapConnectionServiceAdministrationEndpoints();
app.MapPluginSetupEndpoints();
app.MapConnectorStandingPolicyEndpoints();
app.MapSecurityAuditEndpoints();
app.MapMarketplaceDiscoveryEndpoints();
app.MapAgentCatalogEndpoints();
app.MapWorkBoardEndpoints();
app.MapWorkTrackingEndpoints();
app.MapWorkDeliveryEndpoints();
app.MapSourceControlEndpoints();
app.MapInternalGitHttpEndpoints();
app.MapAgentWorkspaceBrokerEndpoints();
app.MapAnalyticsEndpoints();
app.MapWorkEfficiencyEndpoints();
app.MapBenchmarkEndpoints();

app.MapControllers();
app.MapHub<AppEventsHub>("/hubs/app-events");

app.Run();

static bool IsDevelopmentLoopbackOrigin(string origin)
{
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
    {
        return false;
    }

    return uri.Scheme is "http" or "https" &&
        (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("[::1]", StringComparison.OrdinalIgnoreCase));
}

public partial class Program;
