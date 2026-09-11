using System.Text.Json;
using CSweet.Api.Core;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Setup;
using CSweet.WebHost.Contracts;
using CSweet.WebHost.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;
public sealed partial class WebPreviewDispatchTests
{
    private static readonly WebPreviewGatewayOptions GatewayOptions = new() { HeadquartersOrigin = "https://hq.example.com", PreviewHostSuffix = "preview.example.net" };
    private static async Task<(Guid Preview, Guid User, WebPreviewGatewayService Gateway)> ReadyBrowserAsync(Fixture f)
    {
        await f.SeedAsync();
        var (job, initialize) = await f.StartToInitializeAsync();
        await f.CompleteAsync(initialize, new("Completed", Guest: new(initialize.CommandId, "initialize", PreviewPhase.Ready)));
        var evidence = await f.PollAsync("evidence");
        await f.CompleteAsync(evidence, new("Evidence", Evidence: new(job.Id, 0, [], false)));
        var user = Guid.NewGuid();
        f.Db.CoreOrganizationUsers.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Org, ApplicationUserId = user,
            EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner, DisplayName = "Owner" });
        await f.Db.SaveChangesAsync();
        return (job.Id, user, new(f.Db, f.Service, new(Options.Create(GatewayOptions)), f.Clock));
    }
    [Fact]
    public async Task Opening_tickets_are_single_use_preview_bound_and_not_saved_as_secrets()
    {
        await using var f = new Fixture(); var (preview, user, gateway) = await ReadyBrowserAsync(f);
        var open = await gateway.OpenAsync(f.Org, user, preview, default);
        Assert.Equal($"https://{preview:N}.preview.example.net", open.Origin);
        Assert.DoesNotContain(open.Ticket, (await f.Db.WebPreviewBrowserSessions.SingleAsync()).TicketHash);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => gateway.RedeemAsync(Guid.NewGuid(), open.Ticket, default));
        var cookie = await gateway.RedeemAsync(preview, open.Ticket, default);
        Assert.NotEqual(open.Ticket, cookie.Value);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => gateway.RedeemAsync(preview, open.Ticket, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => gateway.SendAsync(preview, open.Ticket, new("GET", "/", new Dictionary<string,string>(), []), default));
    }
    [Fact]
    public async Task Membership_and_grant_revocation_block_existing_browser_sessions()
    {
        await using var f = new Fixture(); var (preview, user, gateway) = await ReadyBrowserAsync(f);
        var open = await gateway.OpenAsync(f.Org, user, preview, default);
        var cookie = await gateway.RedeemAsync(preview, open.Ticket, default);
        var member = await f.Db.CoreOrganizationUsers.SingleAsync(x => x.ApplicationUserId == user);
        member.IsActive = false; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => gateway.SendAsync(preview, cookie.Value, new("GET", "/", new Dictionary<string,string>(), []), default));
        member.IsActive = true; f.Grant.RevokedAt = f.Clock.Now; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => gateway.SendAsync(preview, cookie.Value, new("GET", "/", new Dictionary<string,string>(), []), default));
        Assert.Empty(await f.Db.WebHostCommands.Where(x => x.Action == "http").ToListAsync());
    }
    [Theory]
    [InlineData("/api/core/organizations")]
    [InlineData("/health")]
    [InlineData("/__preview/unknown")]
    public async Task Preview_origin_never_falls_through_to_headquarters_routes(string path)
    {
        await using var f = new Fixture(); var (preview, _, gateway) = await ReadyBrowserAsync(f);
        var reachedHeadquarters = false;
        var middleware = new WebPreviewGatewayMiddleware(_ => { reachedHeadquarters = true; return Task.CompletedTask; }, new(Options.Create(GatewayOptions)), Options.Create(GatewayOptions));
        var context = new DefaultHttpContext(); context.Request.Scheme = "https";
        context.Request.Host = new HostString($"{preview:N}.preview.example.net"); context.Request.Path = path; context.Request.Method = "GET";
        await middleware.InvokeAsync(context, gateway);
        Assert.False(reachedHeadquarters); Assert.Contains(context.Response.StatusCode, new[] { 403, 404 });
        Assert.Contains("frame-ancestors 'none'", context.Response.Headers.ContentSecurityPolicy.ToString());
    }
    [Theory]
    [InlineData("https://hq.example.com", "previews.example.com")]
    [InlineData("http://hq.example.com", "previews.example.net")]
    [InlineData("https://hq.example.com/path", "previews.example.net")]
    [InlineData("https://hq.example.com", "-bad.example.net")]
    public void Browser_origins_reject_shared_sites_and_malformed_configuration(string hq, string suffix)
    {
        Assert.Throws<InvalidOperationException>(() => new WebPreviewOrigins(Options.Create(new WebPreviewGatewayOptions
            { HeadquartersOrigin = hq, PreviewHostSuffix = suffix })).Origin(Guid.NewGuid()));
    }
    [Fact]
    public async Task Ticket_expiry_and_repeated_stop_are_bounded()
    {
        await using var f = new Fixture(); var (preview, user, gateway) = await ReadyBrowserAsync(f);
        var open = await gateway.OpenAsync(f.Org, user, preview, default); f.Clock.Now = f.Clock.Now.AddMinutes(2);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => gateway.RedeemAsync(preview, open.Ticket, default));
        await f.Service.StopAsync(f.Org, f.Agent, preview, default); await f.Service.StopAsync(f.Org, f.Agent, preview, default);
        Assert.Single(await f.Db.WebHostCommands.Where(x => x.Action == "stop" && x.Status == "Pending").ToListAsync());
    }
    [Fact]
    public async Task Preflight_accepts_server_derived_digest_only_for_a_matching_successful_build()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var grants = new WebPreviewGrantService(f.Db, f.Clock, f.Catalog);
        Assert.True((await grants.PreflightAsync(f.Org, f.Agent, f.Request(), default)).Allowed);
        Assert.False((await grants.PreflightAsync(f.Org, f.Agent, f.Request() with { BuildId = Guid.NewGuid() }, default)).Allowed);
    }
    [Fact]
    public async Task Expired_host_identity_can_confirm_cleanup_but_cannot_receive_artifacts()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var job = await f.Service.StartAsync(f.Org, f.Agent, f.Request(), default);
        var upload = await f.PollAsync("upload");
        f.Host.ExpiresAt = f.Clock.Now.AddSeconds(-1); await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ArtifactAsync(f.Message("artifact", new WebHostArtifactRequest(upload.CommandId)), default));
        var stop = await f.PollAsync("stop"); await f.CompleteAsync(stop, new("Stopped"));
        Assert.NotNull((await f.Db.WebPreviewJobs.SingleAsync(x => x.Id == job.Id)).TeardownConfirmedAt);
        Assert.Null((await f.Db.WebPreviewJobs.SingleAsync()).AccessReference);
    }}
