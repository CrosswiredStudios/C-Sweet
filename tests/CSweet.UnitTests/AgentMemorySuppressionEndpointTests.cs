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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CSweet.UnitTests;

public sealed class AgentMemorySuppressionEndpointTests
{
    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("unverified-claim", HttpStatusCode.Unauthorized)]
    [InlineData("ok", HttpStatusCode.OK)]
    [InlineData("forbidden", HttpStatusCode.Forbidden)]
    [InlineData("stale", HttpStatusCode.Conflict)]
    [InlineData("source", HttpStatusCode.Conflict)]
    [InlineData("missing", HttpStatusCode.NotFound)]
    [InlineData("invalid", HttpStatusCode.BadRequest)]
    [InlineData("backend", HttpStatusCode.ServiceUnavailable)]
    public async Task ReviewUsesAuthenticatedActorAndMapsReviewOutcomes(string scenario, HttpStatusCode expected)
    {
        var user = Guid.NewGuid();
        var organization = Guid.NewGuid();
        var employee = Guid.NewGuid();
        var job = Guid.NewGuid();
        var operation = Guid.NewGuid();
        var service = new ReviewStub(scenario);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = Path.GetTempPath(), Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IAgentMemorySuppressionService>(service);
        builder.Services.AddSingleton(DispatchProxy.Create<IAgentMemoryReviewService, UnusedService>());
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
            using var response = await client.PostAsJsonAsync($"/api/core/organizations/{organization}/employees/{employee}/memory/episodes/{job}/suppression",
                new { OperationId = operation, ExpectedRevision = 2L, EvidenceToken = new string('a', 64), ApplicationUserId = Guid.NewGuid() });
            Assert.Equal(expected, response.StatusCode);
            Assert.Equal(scenario is "anonymous" or "unverified-claim" ? 0 : 1, service.Calls);
            if (service.Calls != 0)
            {
                Assert.Equal(user, service.Actor);
                Assert.Equal((organization, employee, job), service.Scope);
                Assert.Equal(new SuppressMemorySourceRequest(operation, 2, new string('a', 64)), service.Request);
            }
            Assert.DoesNotContain("private-memory", await response.Content.ReadAsStringAsync());
            using var read = await client.GetAsync($"/api/core/organizations/{organization}/employees/{employee}/memory/episodes/{job}/suppression");
            Assert.Equal(expected, read.StatusCode);
            Assert.Equal(scenario is "anonymous" or "unverified-claim" ? 0 : 2, service.Calls);
            Assert.DoesNotContain("private-memory", await read.Content.ReadAsStringAsync());
            if (service.Calls != 0) Assert.Equal(user, service.Actor);
        }
        finally { await app.StopAsync(); }
    }

    public class UnusedService : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException();
    }

    private sealed class ReviewStub(string scenario) : IAgentMemorySuppressionService
    {
        public int Calls { get; private set; }
        public Guid Actor { get; private set; }
        public (Guid Organization, Guid Employee, Guid Job) Scope { get; private set; }
        public SuppressMemorySourceRequest? Request { get; private set; }
        public async Task<MemorySuppressionPreview> GetSuppressionAsync(Guid organizationId, Guid employeeId, Guid procedureId,
            Guid applicationUserId, CancellationToken cancellationToken = default)
        {
            await SuppressSourceAsync(organizationId, employeeId, procedureId, applicationUserId,
                new(Guid.NewGuid(), 2, new string('a', 64)), cancellationToken);
            return new(procedureId, 2, new string('a', 64), "Steps", false, false);
        }
        public Task<SuppressMemorySourceResponse> SuppressSourceAsync(Guid organizationId, Guid employeeId, Guid jobId,
            Guid applicationUserId, SuppressMemorySourceRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Actor = applicationUserId;
            Scope = (organizationId, employeeId, jobId);
            Request = request;
            switch (scenario)
            {
                case "forbidden": throw new UnauthorizedAccessException("private-memory");
                case "stale": throw new DbUpdateConcurrencyException("private-memory");
                case "source": throw new InvalidOperationException("private-memory");
                case "missing": throw new KeyNotFoundException("private-memory");
                case "invalid": throw new ArgumentException("private-memory");
                case "backend": throw new NotSupportedException("private-memory");
            }
            return Task.FromResult(new SuppressMemorySourceResponse(Guid.NewGuid(), jobId, 3, DateTimeOffset.UtcNow, false));
        }
    }
}
