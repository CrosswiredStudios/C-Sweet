using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using CSweet.Api.WorkManagement;
using CSweet.Application.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.UI.Components.WorkBoards;
using CSweet.WorkManagement.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class WorkTrackingApiTests
{
    [Fact]
    public async Task AuthenticatedApiAndRenderedComponentsHonorFollowingAndLimitedVisibility()
    {
        await using var f = await TrackedDelegationTests.Fixture.Create();
        var user = Guid.NewGuid(); f.Matt.ApplicationUserId = user; await f.Db.SaveChangesAsync();
        var d = await f.Service.CreateAsync(f.Org, f.Naomi.Id, f.Request());
        d = await f.Service.RespondAsync(f.Org, f.Gabriel.Id, new(d.Id, d.Revision, "Accepted", "Recheck staffing readiness.", "accept"));
        var item = d.WorkItemId!.Value;
        var builder = WebApplication.CreateBuilder(); builder.Configuration.Sources.Clear(); builder.Configuration.AddInMemoryCollection(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuth>("fixture", _ => { });
        builder.Services.AddAuthorization(); builder.Services.AddSingleton(f.Db); builder.Services.AddSingleton<ITrackedDelegationService>(f.Service);
        await using var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); app.MapWorkTrackingEndpoints();
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new(app.Urls.Single()) }; http.DefaultRequestHeaders.Add("X-Fixture-User", user.ToString("D"));
        var endpoint = $"api/organizations/{f.Org}/work/tracking/{item}";
        var full = await http.GetFromJsonAsync<WorkTrackingSnapshot>(endpoint); Assert.False(full!.Limited);
        Assert.True((await (await http.PostAsJsonAsync(endpoint+"/follow", new FollowWorkRequest(item,"human-follow"))).Content.ReadFromJsonAsync<WorkTrackingSnapshot>())!.Following);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync(endpoint+"/follow", new FollowWorkRequest(Guid.NewGuid(),"mismatch"))).StatusCode);
        http.DefaultRequestHeaders.Remove("X-Fixture-User"); Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(endpoint)).StatusCode);
        http.DefaultRequestHeaders.Add("X-Fixture-User", user.ToString("D"));

        // Optional loopback smoke fixture renders the real component against these disposable API records.
        var browserPath = Environment.GetEnvironmentVariable("CSWEET_DELEGATION_BROWSER_FIXTURE");
        if (!string.IsNullOrEmpty(browserPath))
        {
            app.MapGet("/fixture/full", () => Render(http, f.Org, item));
            app.MapGet("/fixture/limited", async () =>
            {
                foreach (var grant in f.Db.ScopedActionGrants.Where(x => x.SubjectId == f.Matt.Id)) grant.RevokedAt = DateTimeOffset.UtcNow;
                await f.Db.SaveChangesAsync(); return await Render(http, f.Org, item);
            });
            app.MapPost("/fixture/toggle", async () =>
            {
                var snapshot = (await http.GetFromJsonAsync<WorkTrackingSnapshot>(endpoint))!;
                await http.PostAsJsonAsync(endpoint+(snapshot.Following ? "/unfollow" : "/follow"), new FollowWorkRequest(item,Guid.NewGuid().ToString("N")));
                return Microsoft.AspNetCore.Http.Results.Redirect("/fixture/full");
            });
            await File.WriteAllTextAsync(browserPath, app.Urls.Single());
            var deadline = DateTime.UtcNow.AddMinutes(4);
            while (!File.Exists(browserPath+".done") && DateTime.UtcNow < deadline) await Task.Delay(250);
            Assert.True(File.Exists(browserPath+".done"), "Browser fixture validation did not complete.");
        }
        foreach (var grant in f.Db.ScopedActionGrants.Where(x => x.SubjectId == f.Matt.Id)) grant.RevokedAt = DateTimeOffset.UtcNow;
        await f.Db.SaveChangesAsync();
        var limited = (await http.GetFromJsonAsync<WorkTrackingSnapshot>(endpoint))!; Assert.True(limited.Limited); Assert.Null(limited.WorkItemUrl);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync(endpoint+"/follow", new FollowWorkRequest(item,"revoked"))).StatusCode);
        Assert.False((await (await http.PostAsJsonAsync(endpoint+"/unfollow", new FollowWorkRequest(item,"stop-following"))).Content.ReadFromJsonAsync<WorkTrackingSnapshot>())!.Following);
        await app.StopAsync();
    }

    private static async Task<Microsoft.AspNetCore.Http.IResult> Render(HttpClient http, Guid org, Guid item)
    {
        var services = new ServiceCollection().AddLogging(); services.AddSingleton(http);
        await using var provider = services.BuildServiceProvider(); await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<WorkTracking>(
            ParameterView.FromDictionary(new Dictionary<string,object?> { [nameof(WorkTracking.OrganizationId)] = org,
                [nameof(WorkTracking.ItemId)] = item, [nameof(WorkTracking.ShowUpdates)] = true }))).ToHtmlString());
        return Microsoft.AspNetCore.Http.Results.Content("<!doctype html><html><head><title>Delegation tracking fixture</title></head><body>"+html+
            "<script>document.querySelector('button')?.addEventListener('click',async()=>{await fetch('/fixture/toggle',{method:'POST'});location.reload()});</script></body></html>", "text/html");
    }

    private sealed class FixtureAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            Guid.TryParse(Request.Headers["X-Fixture-User"], out var id)
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,id.ToString("D"))],"fixture")),"fixture"))
                : AuthenticateResult.NoResult());
    }
}
