using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using CSweet.Api.Core;
using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CSweet.UnitTests;

public sealed class ProjectApprovalEndpointTests
{
    [Theory]
    [InlineData("Approve")]
    [InlineData("Reject")]
    [InlineData("RequestRevision")]
    public async Task BothSurfacesUseBoundDecisionAndPersistFeedbackWithoutRepeatingExecution(string decision)
    {
        await using var db = ProjectApprovalReviewTests.Database();
        var (owner, _, proposal, _) = await ProjectApprovalReviewTests.Seed(db);
        var executor = new Executor();
        var audit = new Audit();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = Path.GetTempPath(), Args = [] });
        builder.Configuration.Sources.Clear(); builder.Configuration.AddInMemoryCollection(); builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton<IManagedActionExecutor>(executor);
        builder.Services.AddSingleton<IAuditEventWriter>(audit);
        builder.Services.AddSingleton(new ConnectorActionApprovalService(db, null!, audit));
        builder.Services.AddSingleton(DispatchProxy.Create<IApprovalDashboardService, Unused>());
        builder.Services.AddSingleton(DispatchProxy.Create<IPluginSecretStore, Unused>());
        builder.Services.AddAuthentication("test").AddCookie("test", options =>
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; });
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.User = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, owner.ApplicationUserId!.Value.ToString())], "test"));
            await next(context);
        });
        app.MapApprovalEndpoints(); await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var route = $"/api/core/organizations/{owner.OrganizationId:D}/approvals/agent-actions/{proposal.Id:D}/decide";
            var request = new DecideManagedAgentActionRequest(proposal.Id, decision, "Reviewed scope", new string('b', 64), null, "plan", "first");
            using var invalid = await client.PostAsJsonAsync(route, request with { PayloadHash = "changed" });
            Assert.Equal(HttpStatusCode.Conflict, invalid.StatusCode);
            Assert.Equal(0, executor.Calls);
            using var result = await client.PostAsJsonAsync(route, request);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            var review = await client.GetFromJsonAsync<ApprovalDashboardItemResponse>($"/api/core/organizations/{owner.OrganizationId:D}/approvals/projects/{proposal.Id:D}");
            Assert.False(review!.CanDecide);
            Assert.Equal("Reviewed scope", review.DecisionComment);
            Assert.Equal(decision, review.DecisionKind);
            Assert.Equal(owner.DisplayName, review.ActualDecisionMaker);
            using var stale = await client.PostAsJsonAsync(route, request with { Decision = "Approve", DecisionIdempotencyKey = "stale-chat" });
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.Equal(decision == "Approve" ? 1 : 0, executor.Calls);
        }
        finally { await app.StopAsync(); }
    }
    private sealed class Executor : IManagedActionExecutor
    {
        public int Calls { get; private set; }
        public bool CanExecute(string type) => type == ProjectApprovalReader.ActionType;
        public Task<ManagedActionExecutionResult> ExecuteAsync(ActionProposal proposal, OrganizationUser actor, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new ManagedActionExecutionResult(Guid.NewGuid(), 1, "Created project")); }
    }
    private sealed class Audit : IAuditEventWriter
    {
        public Task WriteAsync(string eventType, string entityType, Guid? entityId, string? summary, string? metadataJson = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public class Unused : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => throw new NotSupportedException();
    }
}
