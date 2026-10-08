using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using CSweet.Api.Core;
using CSweet.Application.Core;
using CSweet.Contracts.Memory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CSweet.UnitTests;

public sealed class AgentMemoryHistoryEndpointTests
{
    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("unverified-claim", HttpStatusCode.Unauthorized)]
    [InlineData("ok", HttpStatusCode.OK)]
    [InlineData("forbidden", HttpStatusCode.Forbidden)]
    [InlineData("source", HttpStatusCode.Conflict)]
    [InlineData("missing", HttpStatusCode.NotFound)]
    [InlineData("invalid", HttpStatusCode.BadRequest)]
    [InlineData("backend", HttpStatusCode.ServiceUnavailable)]
    public async Task HistoryUsesAuthenticatedActorAndMapsOutcomes(string scenario, HttpStatusCode expected)
    {
        var user = Guid.NewGuid();
        var organization = Guid.NewGuid();
        var employee = Guid.NewGuid();
        var job = Guid.NewGuid();
        var proxy = DispatchProxy.Create<IAgentMemoryReviewService, ReviewStub>();
        var service = (ReviewStub)(object)proxy; service.Scenario = scenario;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = Path.GetTempPath(), Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IAgentMemoryReviewService>(proxy);
        builder.Services.AddSingleton(DispatchProxy.Create<IAgentMemoryRecoveryService, UnusedService>());
        builder.Services.AddSingleton(DispatchProxy.Create<IAgentMemoryService, UnusedService>());
        builder.Services.AddSingleton(DispatchProxy.Create<IAgentMemoryTransferService, UnusedService>());
        builder.Services.AddSingleton(DispatchProxy.Create<IEmployeeHierarchyAccessService, UnusedService>());
        builder.Services.AddAuthentication("test").AddCookie("test", options =>
        {
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
        });
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.User = scenario == "anonymous" ? new ClaimsPrincipal() : new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user.ToString())], scenario == "unverified-claim" ? null : "test"));
            await next(context);
        });
        app.MapAgentMemoryEndpoints();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var response = await client.GetAsync($"/api/core/organizations/{organization}/employees/{employee}/memory/history/Claim/{job}?afterRevision=5&limit=10&applicationUserId={Guid.NewGuid()}");
            Assert.Equal(expected, response.StatusCode);
            Assert.Equal(scenario is "anonymous" or "unverified-claim" ? 0 : 1, service.Calls);
            if (service.Calls != 0)
            {
                Assert.Equal(user, service.Actor); Assert.Equal((organization, employee, job), service.Scope);
            }
            Assert.DoesNotContain("private-memory", await response.Content.ReadAsStringAsync());
        }
        finally { await app.StopAsync(); }
    }

    public class UnusedService : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException();
    }

    public class ReviewStub : DispatchProxy
    {
        public string Scenario { get; set; } = "";
        public int Calls { get; private set; }
        public Guid Actor { get; private set; }
        public (Guid, Guid, Guid) Scope { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Assert.Equal(nameof(IAgentMemoryReviewService.ReadHistoryAsync), method!.Name);
            Calls++; Actor = (Guid)args![4]!; Scope = ((Guid)args[0]!, (Guid)args[1]!, (Guid)args[3]!);
            Assert.Equal("Claim", args[2]); Assert.Equal(5L, args[5]); Assert.Equal(10, args[6]);
            switch (Scenario)
            {
                case "forbidden": throw new UnauthorizedAccessException("private-memory");
                case "source": throw new InvalidOperationException("private-memory");
                case "missing": throw new KeyNotFoundException("private-memory");
                case "invalid": throw new ArgumentException("private-memory");
                case "backend": throw new NotSupportedException("private-memory");
            }
            return Task.FromResult(new MemoryHistoryPage("Claim", Scope.Item3, "Employee", [], null));
        }
    }
}
