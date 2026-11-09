using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Api.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CSweet.UnitTests;

public sealed class ResourceFulfillmentEndpointTests
{
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Preparation_failure_is_visible_and_retryable_only_to_the_current_authorized_actor(bool authorized)
    {
        await using var f = await ResourceFulfillmentTests.Fixture.CreateAsync();
        f.Handler.InvalidPreparation = true;
        var request = await f.Service.RequestAsync(f.Organization, f.Installation, f.Input, default);
        await f.Service.ProcessNextAsync(default);
        var failed = await f.ReadAsync(request.Id);
        Assert.Equal("NeedsAttention", failed.Status); Assert.Null(failed.Plan);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = Path.GetTempPath(), Args = [] });
        builder.Configuration.Sources.Clear(); builder.Configuration.AddInMemoryCollection(); builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(f.Db); builder.Services.AddSingleton(f.Service);
        builder.Services.AddAuthorization(); builder.Services.AddAuthentication("test").AddCookie("test", options =>
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; });
        await using var app = builder.Build();
        app.UseAuthentication();
        app.Use(async (context, next) => {
            context.User = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,
                (authorized ? f.Manager.ApplicationUserId!.Value : Guid.NewGuid()).ToString())], "test"));
            await next(context);
        });
        app.UseAuthorization();
        app.MapResourceFulfillmentEndpoints(); await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var route = $"/api/core/organizations/{f.Organization:D}/resources";
            using var read = await client.GetAsync(route + $"?workstreamId={f.Project:D}");
            Assert.Equal(authorized ? HttpStatusCode.OK : HttpStatusCode.Forbidden, read.StatusCode);
            if (authorized)
            {
                var states = await read.Content.ReadFromJsonAsync<JsonElement[]>();
                var visible = Assert.Single(states!);
                Assert.Equal(JsonValueKind.Null, visible.GetProperty("plan").ValueKind);
                Assert.True(visible.GetProperty("canRetry").GetBoolean());
                Assert.False(visible.GetProperty("canDecide").GetBoolean());
                Assert.Contains("published checksum", visible.GetProperty("condition").GetString());
            }
            f.Handler.InvalidPreparation = false;
            var retry = new ResourceDecision(failed.Id, failed.Revision, failed.PlanHash, "Retry", "Corrected the published bundle.", "same-preparation-retry");
            using var result = await client.PostAsJsonAsync(route + $"/{failed.Id:D}/retry", retry);
            Assert.Equal(authorized ? HttpStatusCode.OK : HttpStatusCode.Forbidden, result.StatusCode);
            if (authorized)
            {
                using var replay = await client.PostAsJsonAsync(route + $"/{failed.Id:D}/retry", retry);
                Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
                Assert.Equal("Preparing", (await f.ReadAsync(failed.Id)).Status);
            }
            Assert.Empty(f.Db.ActionProposals); Assert.Equal(0, f.Handler.Installations);
        }
        finally { await app.StopAsync(); }
    }
}
