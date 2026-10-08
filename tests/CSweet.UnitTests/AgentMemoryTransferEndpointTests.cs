using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using CSweet.Api.Core;
using CSweet.Application.Core;
using CSweet.Contracts.Memory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CSweet.UnitTests;

public sealed class AgentMemoryTransferEndpointTests
{
    [Theory]
    [InlineData("prepare", "ok", HttpStatusCode.OK)]
    [InlineData("get", "ok", HttpStatusCode.OK)]
    [InlineData("list", "ok", HttpStatusCode.OK)]
    [InlineData("audiences", "ok", HttpStatusCode.OK)]
    [InlineData("audiences", "anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("audiences", "forbidden", HttpStatusCode.Forbidden)]
    [InlineData("transition", "ok", HttpStatusCode.OK)]
    [InlineData("transition", "anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("transition", "unverified", HttpStatusCode.Unauthorized)]
    [InlineData("transition", "forbidden", HttpStatusCode.Forbidden)]
    [InlineData("transition", "missing", HttpStatusCode.NotFound)]
    [InlineData("transition", "changed", HttpStatusCode.Conflict)]
    [InlineData("transition", "source", HttpStatusCode.Conflict)]
    [InlineData("transition", "invalid", HttpStatusCode.BadRequest)]
    [InlineData("transition", "backend", HttpStatusCode.ServiceUnavailable)]
    public async Task TransferEndpointsDeriveAuthenticatedActorAndMapSafeOutcomes(string operation, string scenario, HttpStatusCode status)
    {
        var user = Guid.NewGuid(); var organization = Guid.NewGuid(); var employee = Guid.NewGuid(); var package = Guid.NewGuid();
        var service = new TransferStub(scenario);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = Path.GetTempPath(), Args = [] });
        builder.Configuration.Sources.Clear(); builder.Configuration.AddInMemoryCollection(); builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Services.AddSingleton<IAgentMemoryTransferService>(service);
        builder.Services.AddAuthentication("test").AddCookie("test", options =>
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; });
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.User = scenario == "anonymous" ? new ClaimsPrincipal() : new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user.ToString())], scenario == "unverified" ? null : "test"));
            await next(context);
        });
        app.MapGroup("/organizations/{organizationId:guid}/employees/{employeeId:guid}/memory").MapMemoryTransferRoutes();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var root = $"/organizations/{organization}/employees/{employee}/memory/transfers";
            using var response = operation switch
            {
                "prepare" => await client.PostAsJsonAsync(root, new { OperationId = Guid.NewGuid(), TargetEmployeeId = Guid.NewGuid(),
                    SourceScope = "Employee", Items = Array.Empty<MemoryTransferSelection>(), Debrief = "reviewed", ApplicationUserId = Guid.NewGuid() }),
                "get" => await client.GetAsync($"{root}/{package}"),
                "list" => await client.GetAsync(root),
                "audiences" => await client.GetAsync($"{root}/audiences?targetEmployeeId={Guid.NewGuid()}"),
                _ => await client.PostAsJsonAsync($"{root}/{package}", new { OperationId = Guid.NewGuid(), ExpectedToken = new string('a', 64),
                    Action = "approve", ApplicationUserId = Guid.NewGuid() })
            };
            Assert.Equal(status, response.StatusCode);
            Assert.Equal(scenario is "anonymous" or "unverified" ? 0 : 1, service.Calls);
            if (service.Calls != 0) Assert.Equal((organization, employee, user), service.Actor);
            Assert.DoesNotContain("private-evidence", await response.Content.ReadAsStringAsync());
        }
        finally { await app.StopAsync(); }
    }

    private sealed class TransferStub(string scenario) : IAgentMemoryTransferService
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<MemoryTransferAudience>> ListAudiencesAsync(Guid organizationId, Guid employeeId, Guid targetEmployeeId, Guid applicationUserId, CancellationToken token = default)
        { Call(organizationId, employeeId, applicationUserId); return Task.FromResult<IReadOnlyList<MemoryTransferAudience>>([]); }
        public (Guid Organization, Guid Employee, Guid User) Actor { get; private set; }
        private void Call(Guid org, Guid employee, Guid user)
        {
            Calls++; Actor = (org, employee, user);
            switch (scenario)
            {
                case "forbidden": throw new UnauthorizedAccessException("private-evidence");
                case "missing": throw new KeyNotFoundException("private-evidence");
                case "changed": throw new DbUpdateConcurrencyException("private-evidence");
                case "source": throw new InvalidOperationException("private-evidence");
                case "invalid": throw new ArgumentException("private-evidence");
                case "backend": throw new NotSupportedException("private-evidence");
            }
        }
        public Task<MemoryTransferResult> PrepareAsync(Guid organizationId, Guid employeeId, Guid applicationUserId, PrepareMemoryTransferRequest request, CancellationToken token = default)
        { Call(organizationId, employeeId, applicationUserId); return Task.FromResult(new MemoryTransferResult(Guid.NewGuid(), Guid.NewGuid(), "PendingApproval", null, false)); }
        public Task<MemoryTransferResponse> GetAsync(Guid organizationId, Guid employeeId, Guid packageId, Guid applicationUserId, CancellationToken token = default)
        { Call(organizationId, employeeId, applicationUserId); return Task.FromResult(new MemoryTransferResponse(packageId, Guid.NewGuid(), "PendingApproval", new string('a', 64), "reviewed", "Personal", true, false, true, null)); }
        public Task<MemoryTransferPage> ListAsync(Guid organizationId, Guid employeeId, Guid applicationUserId, Guid? cursor = null, CancellationToken token = default)
        { Call(organizationId, employeeId, applicationUserId); return Task.FromResult(new MemoryTransferPage([], null)); }
        public Task<MemoryTransferResult> TransitionAsync(Guid organizationId, Guid employeeId, Guid packageId, Guid applicationUserId, TransitionMemoryTransferRequest request, CancellationToken token = default)
        { Call(organizationId, employeeId, applicationUserId); return Task.FromResult(new MemoryTransferResult(Guid.NewGuid(), packageId, "Approved", null, false)); }
    }
}
