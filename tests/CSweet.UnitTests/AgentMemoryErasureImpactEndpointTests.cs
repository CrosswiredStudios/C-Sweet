using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using CSweet.Api.Core;
using CSweet.Application.Core;
using CSweet.Contracts.Memory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CSweet.UnitTests;

public sealed class AgentMemoryErasureImpactEndpointTests
{
    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("unverified", HttpStatusCode.Unauthorized)]
    [InlineData("ok", HttpStatusCode.OK)]
    [InlineData("forbidden", HttpStatusCode.Forbidden)]
    [InlineData("stale", HttpStatusCode.Conflict)]
    [InlineData("source", HttpStatusCode.Conflict)]
    [InlineData("invalid", HttpStatusCode.BadRequest)]
    [InlineData("backend", HttpStatusCode.ServiceUnavailable)]
    public async Task ErasureHistoryEndpointUsesAuthenticatedActorAndBoundedCursorParameters(string scenario, HttpStatusCode expected)
    {
        var user = Guid.NewGuid(); var organization = Guid.NewGuid(); var employee = Guid.NewGuid(); var cursor = Guid.NewGuid();
        var service = new ImpactStub(scenario);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = Path.GetTempPath(), Args = [] });
        builder.Configuration.Sources.Clear(); builder.Configuration.AddInMemoryCollection(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IAgentMemoryErasureImpactService>(service);
        builder.Services.AddAuthentication("test").AddCookie("test", options => options.Events.OnRedirectToAccessDenied = context =>
        { context.Response.StatusCode = 403; return Task.CompletedTask; });
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.User = scenario == "anonymous" ? new ClaimsPrincipal() : new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user.ToString())], scenario == "unverified" ? null : "test")); await next(context);
        });
        app.MapGroup("/api/core/organizations/{organizationId:guid}/employees/{employeeId:guid}/memory").MapMemoryErasureImpactRoutes();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };
            var prefix = $"/api/core/organizations/{organization}/employees/{employee}/memory/erasure-operations";
            using var read = await client.GetAsync($"{prefix}?beforeReceiptId={cursor}&limit=2&applicationUserId={Guid.NewGuid()}");
            Assert.Equal(expected, read.StatusCode); Assert.True(read.Headers.CacheControl?.NoStore);
            if (scenario is not ("anonymous" or "unverified"))
            {
                Assert.Equal((organization, employee, Guid.Empty, user), service.Request);
                Assert.Equal((cursor, 2), service.ListRequest);
            }
            else Assert.Equal(0, service.Calls);
            Assert.DoesNotContain("private-memory", await read.Content.ReadAsStringAsync());
            if (scenario == "ok")
            {
                var page = await read.Content.ReadFromJsonAsync<MemoryErasureOperationPage>();
                Assert.Equal("ownership-review-required", Assert.Single(page!.Items).Availability);
                using var first = await client.GetAsync(prefix); Assert.Equal(HttpStatusCode.OK, first.StatusCode);
                Assert.Equal(((Guid?)null, 10), service.ListRequest);
                var calls = service.Calls;
                using var invalid = await client.GetAsync(prefix + "?beforeReceiptId=not-a-guid");
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); Assert.Equal(calls, service.Calls);
            }
        }
        finally { await app.StopAsync(); }
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("unverified", HttpStatusCode.Unauthorized)]
    [InlineData("ok", HttpStatusCode.OK)]
    [InlineData("forbidden", HttpStatusCode.Forbidden)]
    [InlineData("stale", HttpStatusCode.Conflict)]
    [InlineData("source", HttpStatusCode.Conflict)]
    [InlineData("missing", HttpStatusCode.NotFound)]
    [InlineData("invalid", HttpStatusCode.BadRequest)]
    [InlineData("backend", HttpStatusCode.ServiceUnavailable)]
    public async Task ImpactUsesAuthenticatedActorIsUncachedAndHasNoWriteRoute(string scenario, HttpStatusCode expected)
    {
        var user = Guid.NewGuid(); var organization = Guid.NewGuid(); var employee = Guid.NewGuid(); var episode = Guid.NewGuid();
        var service = new ImpactStub(scenario);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = Path.GetTempPath(), Args = [] });
        builder.Configuration.Sources.Clear(); builder.Configuration.AddInMemoryCollection(); builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IAgentMemoryErasureImpactService>(service);
        builder.Services.AddAuthentication("test").AddCookie("test", options =>
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; });
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.User = scenario == "anonymous" ? new ClaimsPrincipal() : new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user.ToString())], scenario == "unverified" ? null : "test"));
            await next(context);
        });
        app.MapGroup("/api/core/organizations/{organizationId:guid}/employees/{employeeId:guid}/memory").MapMemoryErasureImpactRoutes();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };
            var url = $"/api/core/organizations/{organization}/employees/{employee}/memory/episodes/{episode}/erasure-impact";
            using var read = await client.GetAsync(url + "?applicationUserId=" + Guid.NewGuid());
            Assert.Equal(expected, read.StatusCode); Assert.True(read.Headers.CacheControl?.NoStore);
            Assert.Equal(scenario is "anonymous" or "unverified" ? 0 : 1, service.Calls);
            if (service.Calls != 0) Assert.Equal((organization, employee, episode, user), service.Request);
            Assert.DoesNotContain("private-memory", await read.Content.ReadAsStringAsync());
            using var write = await client.PostAsJsonAsync(url, new { OperationId = Guid.NewGuid() });
            Assert.Equal(HttpStatusCode.MethodNotAllowed, write.StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    private sealed class ImpactStub(string scenario) : IAgentMemoryErasureImpactService
    {
        public (Guid? Cursor, int Limit)? ListRequest { get; private set; }
        public Task<MemoryErasureOperationPage> ListErasureOperationsAsync(Guid organizationId, Guid employeeId,
            Guid applicationUserId, Guid? beforeReceiptId = null, int limit = 10, CancellationToken cancellationToken = default)
        {
            ListRequest = (beforeReceiptId, limit);
            GetErasureImpactAsync(organizationId, employeeId, Guid.Empty, applicationUserId, cancellationToken);
            return Task.FromResult(new MemoryErasureOperationPage([new(Guid.NewGuid(), DateTimeOffset.UtcNow, "ownership-review-required")], null));
        }
        public EraseMemorySourceRequest? ErasureRequest { get; private set; }
        public Task<MemoryErasureResponse> EraseSourceAsync(Guid organizationId, Guid employeeId, Guid episodeId,
            Guid applicationUserId, EraseMemorySourceRequest request, CancellationToken cancellationToken = default)
        {
            ErasureRequest = request;
            GetErasureImpactAsync(organizationId, employeeId, episodeId, applicationUserId, cancellationToken);
            return Task.FromResult(new MemoryErasureResponse(Guid.NewGuid(), request.OperationId, episodeId, 1, 2, 1, 1, 1, 0, "completed", DateTimeOffset.UtcNow, false));
        }
        public Task<MemoryErasureResponse> GetErasureStatusAsync(Guid organizationId, Guid employeeId, Guid operationId,
            Guid applicationUserId, CancellationToken cancellationToken = default)
        {
            GetErasureImpactAsync(organizationId, employeeId, operationId, applicationUserId, cancellationToken);
            return Task.FromResult(new MemoryErasureResponse(Guid.NewGuid(), operationId, Guid.NewGuid(), 1, 2, 1, 1, 1, 1, "runtime-reset-pending", DateTimeOffset.UtcNow, true));
        }
        public int Calls { get; private set; }
        public (Guid, Guid, Guid, Guid) Request { get; private set; }
        public Task<MemoryErasureImpactResponse> GetErasureImpactAsync(Guid organizationId, Guid employeeId, Guid episodeId,
            Guid applicationUserId, CancellationToken cancellationToken = default)
        {
            Calls++; Request = (organizationId, employeeId, episodeId, applicationUserId);
            switch (scenario)
            {
                case "forbidden": throw new UnauthorizedAccessException("private-memory");
                case "stale": throw new DbUpdateConcurrencyException("private-memory");
                case "source": throw new InvalidOperationException("private-memory");
                case "ownership": throw new InvalidOperationException("memory_erasure_ownership_review_required");
                case "evidence": throw new InvalidOperationException("memory_erasure_evidence_review_required");
                case "missing": throw new KeyNotFoundException("private-memory");
                case "invalid": throw new ArgumentException("private-memory");
                case "backend": throw new NotSupportedException("private-memory");
            }
            return Task.FromResult(new MemoryErasureImpactResponse(episodeId, 1, [], null));
        }
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("unverified", HttpStatusCode.Unauthorized)]
    [InlineData("ok", HttpStatusCode.OK)]
    [InlineData("forbidden", HttpStatusCode.Forbidden)]
    [InlineData("stale", HttpStatusCode.Conflict)]
    [InlineData("source", HttpStatusCode.Conflict)]
    [InlineData("missing", HttpStatusCode.NotFound)]
    [InlineData("invalid", HttpStatusCode.BadRequest)]
    [InlineData("backend", HttpStatusCode.ServiceUnavailable)]
    [InlineData("ownership", HttpStatusCode.Conflict)]
    [InlineData("evidence", HttpStatusCode.Conflict)]
    public async Task ErasureApplyAndStatusDeriveTheActorFromAuthentication(string scenario, HttpStatusCode expected)
    {
        var user = Guid.NewGuid(); var organization = Guid.NewGuid(); var employee = Guid.NewGuid(); var episode = Guid.NewGuid();
        var request = new EraseMemorySourceRequest(Guid.NewGuid(), new string('a', 64)); var service = new ImpactStub(scenario);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = Path.GetTempPath(), Args = [] });
        builder.Configuration.Sources.Clear(); builder.Configuration.AddInMemoryCollection(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IAgentMemoryErasureImpactService>(service);
        builder.Services.AddAuthentication("test").AddCookie("test", options => options.Events.OnRedirectToAccessDenied = context =>
        { context.Response.StatusCode = 403; return Task.CompletedTask; });
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.User = scenario == "anonymous" ? new ClaimsPrincipal() : new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user.ToString())], scenario == "unverified" ? null : "test")); await next(context);
        });
        app.MapGroup("/api/core/organizations/{organizationId:guid}/employees/{employeeId:guid}/memory").MapMemoryErasureImpactRoutes();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) }; var prefix = $"/api/core/organizations/{organization}/employees/{employee}/memory";
            using var apply = await client.PostAsJsonAsync($"{prefix}/episodes/{episode}/erase?applicationUserId={Guid.NewGuid()}", request);
            Assert.Equal(expected, apply.StatusCode); Assert.True(apply.Headers.CacheControl?.NoStore);
            if (scenario is not ("anonymous" or "unverified")) { Assert.Equal((organization, employee, episode, user), service.Request); Assert.Equal(request, service.ErasureRequest); }
            using var status = await client.GetAsync($"{prefix}/erasure-operations/{request.OperationId}?applicationUserId={Guid.NewGuid()}");
            Assert.Equal(expected, status.StatusCode); Assert.True(status.Headers.CacheControl?.NoStore);
            if (scenario is not ("anonymous" or "unverified")) Assert.Equal((organization, employee, request.OperationId, user), service.Request);
            Assert.DoesNotContain("private-memory", await apply.Content.ReadAsStringAsync()); Assert.DoesNotContain("private-memory", await status.Content.ReadAsStringAsync());
            if (scenario is "ownership" or "evidence")
            {
                var code = scenario == "ownership" ? "memory_erasure_ownership_review_required" : "memory_erasure_evidence_review_required";
                Assert.Equal("{\"error\":\"" + code + "\"}", await apply.Content.ReadAsStringAsync());
                Assert.Equal("{\"error\":\"" + code + "\"}", await status.Content.ReadAsStringAsync());
            }
        }
        finally { await app.StopAsync(); }
    }
}
