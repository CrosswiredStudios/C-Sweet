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

public sealed class AgentMemoryReextractionEndpointTests
{
    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("unverified", HttpStatusCode.Unauthorized)]
    [InlineData("ok", HttpStatusCode.OK)]
    [InlineData("forbidden", HttpStatusCode.Forbidden)]
    [InlineData("changed", HttpStatusCode.Conflict)]
    [InlineData("source", HttpStatusCode.Conflict)]
    [InlineData("missing", HttpStatusCode.NotFound)]
    [InlineData("invalid", HttpStatusCode.BadRequest)]
    [InlineData("backend", HttpStatusCode.ServiceUnavailable)]
    public async Task ReextractionRoutesUseAuthenticatedActorAndSanitizeFailures(string scenario, HttpStatusCode expected)
    {
        var user = Guid.NewGuid(); var organization = Guid.NewGuid(); var employee = Guid.NewGuid(); var episode = Guid.NewGuid();
        var service = new Stub(scenario);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = Path.GetTempPath(), Args = [] });
        builder.Configuration.Sources.Clear(); builder.Configuration.AddInMemoryCollection(); builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IAgentMemoryReextractionService>(service);
        builder.Services.AddAuthentication("test").AddCookie("test", options =>
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; });
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.User = scenario == "anonymous" ? new ClaimsPrincipal() : new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user.ToString())], scenario == "unverified" ? null : "test"));
            await next(context);
        });
        app.MapGroup("/api/core/organizations/{organizationId:guid}/employees/{employeeId:guid}/memory").MapMemoryReextractionRoutes();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };
            var prefix = $"/api/core/organizations/{organization}/employees/{employee}/memory";
            foreach (var url in new[] { prefix + "/ingestion/reextraction?limit=3", prefix + $"/jobs/{episode}/reextraction-review" })
            {
                using var response = await client.GetAsync(url);
                Assert.Equal(expected, response.StatusCode); Assert.DoesNotContain("private-input", await response.Content.ReadAsStringAsync());
            }
            using var posted = await client.PostAsJsonAsync(prefix + $"/jobs/{episode}/reextraction-review", new {
                OperationId = Guid.NewGuid(), ExpectedInputGeneration = 3, EvidenceToken = new string('a',64),
                PreservationPolicy = "preserve-existing-v1", ApplicationUserId = Guid.NewGuid() });
            Assert.Equal(expected, posted.StatusCode); Assert.DoesNotContain("private-input", await posted.Content.ReadAsStringAsync());
            Assert.Equal(scenario is "anonymous" or "unverified" ? 0 : 3, service.Calls);
            if (service.Calls != 0) { Assert.Equal(user, service.Actor); Assert.Equal((organization,employee), service.Owner); Assert.Equal("preserve-existing-v1",service.Policy); }
        }
        finally { await app.StopAsync(); }
    }
    private sealed class Stub(string scenario) : IAgentMemoryReextractionService
    {
        public int Calls { get; private set; }
        public Guid Actor { get; private set; }
        public (Guid, Guid) Owner { get; private set; }
        public string? Policy { get; private set; }
        private void Check(Guid org, Guid employee, Guid user)
        {
            Calls++; Actor=user; Owner=(org,employee);
            switch (scenario)
            {
                case "forbidden": throw new UnauthorizedAccessException("private-input");
                case "changed": throw new DbUpdateConcurrencyException("private-input");
                case "source": throw new InvalidOperationException("private-input");
                case "missing": throw new KeyNotFoundException("private-input");
                case "invalid": throw new ArgumentException("private-input");
                case "backend": throw new NotSupportedException("private-input");
            }
        }
        public Task<MemoryReextractionCandidatePage> ListAsync(Guid organizationId, Guid employeeId, Guid applicationUserId, string? cursor=null, int limit=20, CancellationToken token=default)
        { Check(organizationId,employeeId,applicationUserId); return Task.FromResult(new MemoryReextractionCandidatePage([],null)); }
        public Task<MemoryReextractionPreview> PreviewAsync(Guid organizationId, Guid employeeId, Guid episodeId, Guid applicationUserId, CancellationToken token=default)
        { Check(organizationId,employeeId,applicationUserId); return Task.FromResult(new MemoryReextractionPreview(episodeId,Guid.NewGuid(),3,new string('a',64),"Approved text","agent-proposal","Employee","Personal",true,null,0,false)); }
        public Task<ReviewMemoryReextractionResponse> ReviewAsync(Guid organizationId, Guid employeeId, Guid episodeId, Guid applicationUserId, ReviewMemoryReextractionRequest request, CancellationToken token=default)
        { Policy=request.PreservationPolicy; Check(organizationId,employeeId,applicationUserId); return Task.FromResult(new ReviewMemoryReextractionResponse(Guid.NewGuid(),episodeId,Guid.NewGuid(),Guid.NewGuid(),4,"Pending",false)); }
    }
}

