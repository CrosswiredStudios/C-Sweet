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

builder.Configuration.AddJsonFile(
    "first-party-agents.json",
    optional: false,
    reloadOnChange: true);

builder.AddServiceDefaults();
builder.AddCSweetInfrastructure();
builder.Services.AddHostedService<AgentCatalogWarmupService>();
builder.Services.AddChatGateway(builder.Configuration);
builder.Services.AddCommunicationPluginRuntime();
builder.Services.AddAgentManagement();
builder.Services.AddAgentRateLimiting();
builder.Services.AddHostedService<MemoryCaptureWorker>();
builder.Services.AddHostedService<ChatTurnWorker>();
builder.Services.AddHostedService<ArtifactReviewJobWorker>();
builder.Services.AddHostedService<ArtifactAccessExpiryWorker>();
builder.Services.AddHostedService<SourceControlPlatformReconciliationWorker>();
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
            .AllowCredentials();
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
});
builder.Services.AddSignalR();
builder.Services.AddSingleton<IApplicationRealtimePublisher, SignalRApplicationRealtimePublisher>();
builder.Services.AddHostedService<ApplicationRealtimeOutboxWorker>();
builder.Services.AddHostedService<WorkOrchestrationWorker>();
builder.Services.AddHostedService<AgentHireOperationWorker>();
builder.Services.AddHostedService<BusinessOnboardingOperationWorker>();

var app = builder.Build();
app.UseForwardedHeaders();
app.UseExceptionHandler();
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

app.MapGet("/api/health", () => new { status = "ok", service = "CSweet.Api" }).AllowAnonymous();

app.MapHealthChecks("/health").AllowAnonymous();
app.MapAuthenticationEndpoints();

app.MapLlmProviderProfileEndpoints();
app.MapGenAiEndpoints();
app.MapSetupEndpoints();
app.MapOfficeBootstrapEndpoints();
app.MapExecutionFleetEndpoints();
app.MapAgentRuntimeSettingsEndpoints();
app.MapPlanningRunEndpoints();
app.MapPlanningDocumentEndpoints();
app.MapPlanningWorkflowEndpoints();

// Core business domain endpoints
app.MapBusinessOnboardingEndpoints();
app.MapCoreOrganizationEndpoints();
app.MapOrganizationUserEndpoints();
app.MapEmployeeEndpoints();
app.MapTeamEndpoints();
app.MapHiringEndpoints();
app.MapApprovalEndpoints();
app.MapExecutiveBriefingEndpoints();
app.MapRoleEndpoints();
app.MapStrategicObjectiveEndpoints();
app.MapWorkerEndpoints();
app.MapWorkTaskEndpoints();
app.MapTaskRunEndpoints();
app.MapArtifactEndpoints();
app.MapDocumentEndpoints();
app.MapAgentMemoryEndpoints();
app.MapCommunicationEndpoints();
app.MapAgentManagementEndpoints();
app.MapPluginManagementEndpoints();
app.MapPluginSetupEndpoints();
app.MapSecurityAuditEndpoints();
app.MapMarketplaceDiscoveryEndpoints();
app.MapAgentCatalogEndpoints();
app.MapWorkBoardEndpoints();
app.MapSourceControlEndpoints();
app.MapAgentWorkspaceBrokerEndpoints();
app.MapAnalyticsEndpoints();

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
