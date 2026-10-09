using System.Net;
using System.Reflection;
using System.Security.Claims;
using CSweet.Agent.SDK;
using CSweet.Api.Agents;
using CSweet.Application.Agents;
using CSweet.Application.Core;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CSweet.UnitTests;

public sealed class HiringCandidateEndpointTests
{
    [Fact]
    public async Task RecommendationQuery_UsesFamilyAndPreservesExplicitFiltersAndRejectsMissingContext()
    {
        var organization = Guid.NewGuid();
        var user = Guid.NewGuid();
        var recommendation = Guid.NewGuid();
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.CoreOrganizationUsers.Add(new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = organization, ApplicationUserId = user,
            DisplayName = "Owner", IsActive = true, PermissionLevel = OrganizationPermissionLevel.Owner
        });
        await db.SaveChangesAsync();
        var hiring = DispatchProxy.Create<IHiringService, HiringProxy>();
        var proxy = (HiringProxy)(object)hiring;
        proxy.Organization = organization;
        proxy.Recommendation = recommendation;
        var catalog = new RecordingCatalog();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing", ContentRootPath = Path.GetTempPath(), Args = []
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton(hiring);
        builder.Services.AddSingleton<IAgentCatalogService>(catalog);
        builder.Services.AddSingleton(DispatchProxy.Create<IAgentCatalogProfileService, UnusedProxy>());
        builder.Services.AddAuthentication("test").AddCookie("test", options =>
        {
            options.Events.OnRedirectToAccessDenied = http => { http.Response.StatusCode = 403; return Task.CompletedTask; };
        });
        await using var app = builder.Build();
        app.Use(async (http, next) =>
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.ToString())], "test"));
            await next(http);
        });
        app.MapAgentCatalogEndpoints();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var path = $"/api/core/organizations/{organization}/agents/available";
        using var success = await client.GetAsync($"{path}?recommendationId={recommendation}&role=Gameplay%20Engineer&q=Daniel&category=Engineering&capabilities=work.execution.run.v2");
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.Null(catalog.Query!.Role);
        Assert.Equal("software-developer", catalog.Query.RoleCategoryKey);
        Assert.Equal("Daniel", catalog.Query.SearchString);
        Assert.Equal("Engineering", catalog.Query.Category);
        Assert.Equal(["work.execution.run.v2"], catalog.Query.RequiredCapabilities);
        Assert.Equal(["video-game-development"], catalog.Query.PreferredSpecializationKeys);
        using var missing = await client.GetAsync($"{path}?recommendationId={Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var invalid = await client.GetAsync($"{path}?recommendationId=invalid");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var foreign = await client.GetAsync($"/api/core/organizations/{Guid.NewGuid()}/agents/available?recommendationId={recommendation}");
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal(1, catalog.Calls);
    }

    public class HiringProxy : DispatchProxy
    {
        public Guid Organization { get; set; }
        public Guid Recommendation { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.Equal(nameof(IHiringService.GetCandidateSearchContextAsync), targetMethod!.Name);
            Assert.Equal(Organization, (Guid)args![0]!);
            return Task.FromResult((Guid)args[1]! == Recommendation
                ? new HiringCandidateSearchContext("Gameplay Engineer", "software-developer", ["video-game-development"]) : null);
        }
    }

    public class UnusedProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException();
    }

    private sealed class RecordingCatalog : IAgentCatalogService
    {
        public AvailableAgentSearchQuery? Query { get; private set; }
        public int Calls { get; private set; }
        public Task<AvailableAgentSearchResult> GetAvailableAgentsAsync(Guid? organizationId,
            AvailableAgentSearchQuery query, CancellationToken cancellationToken = default)
        {
            Query = query;
            Calls++;
            return Task.FromResult(new AvailableAgentSearchResult([], []));
        }
        public Task<AvailableAgent?> ResolveAsync(Guid? organizationId, string agentReference,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
