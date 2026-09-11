using System.Text.Json;
using CSweet.WebHost.Contracts;
using CSweet.WebHost.Core;
using Microsoft.EntityFrameworkCore;
namespace CSweet.UnitTests;
public sealed partial class WebPreviewDispatchTests
{
    [Fact] public async Task Renewal_is_idempotent_reserves_only_added_cpu_and_waits_for_guest_confirmation()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var (job, initialize) = await f.StartToInitializeAsync();
        await f.CompleteAsync(initialize, new("Completed", Guest: new(initialize.CommandId, "initialize", PreviewPhase.Ready)));
        var evidence = await f.PollAsync("evidence"); await f.CompleteAsync(evidence, new("Evidence", Evidence: new(job.Id, 0, [], false)));
        var request = new RenewPreviewRequest(job.Id, 1200, "extend-demo");
        var renewed = await f.Service.RenewAsync(f.Org, f.Agent, request, default);
        Assert.Equal(PreviewPhase.Starting, renewed.Phase); Assert.Equal(job.CreatedAt.AddSeconds(1200), renewed.ExpiresAt);
        Assert.Equal(2400, (await f.Db.WebPreviewGrants.SingleAsync()).ReservedCpuSeconds);
        Assert.Equal(renewed.Id, (await f.Service.RenewAsync(f.Org, f.Agent, request, default)).Id);
        Assert.Equal(2400, (await f.Db.WebPreviewGrants.SingleAsync()).ReservedCpuSeconds);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RenewAsync(f.Org, f.Agent, request with { TotalLifetimeSeconds = 1300 }, default));
        var command = await f.PollAsync("renew");
        var control = JsonSerializer.Deserialize<ProductRuntimeRequest>(command.RuntimeRequestJson, PreviewJson.Options)!.Control!;
        var body = JsonSerializer.Deserialize<ProductGuestRequest>(control.BodyJson, PreviewJson.Options)!;
        Assert.Equal(2, body.Renewal!.FencingEpoch); Assert.Equal(renewed.ExpiresAt, body.Renewal.ExpiresAt);
        await f.CompleteAsync(command, new("Completed", Guest: new(command.CommandId, "renew", PreviewPhase.Ready)));
        Assert.Equal(PreviewPhase.Ready, (await f.Service.ReadAsync(f.Org, f.Agent, job.Id, default)).Phase);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.RenewAsync(f.Org, f.Agent, new(job.Id, 10000, "excess"), default));
    }
    [Fact] public async Task Browser_tests_are_deduplicated_bound_to_grants_and_create_retained_findings()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var (job, initialize) = await f.StartToInitializeAsync();
        await f.CompleteAsync(initialize, new("Completed", Guest: new(initialize.CommandId, "initialize", PreviewPhase.Ready)));
        var evidence = await f.PollAsync("evidence"); await f.CompleteAsync(evidence, new("Evidence", Evidence: new(job.Id, 0, [], false)));
        var request = new RunPreviewTestsRequest(job.Id, "smoke", [new("/", "#game")]);
        var test = await f.Service.TestAsync(f.Org, f.Agent, request, default);
        Assert.Equal(test.Id, (await f.Service.TestAsync(f.Org, f.Agent, request, default)).Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.TestAsync(f.Org, f.Agent, request with { Checks = [new("/other")] }, default));
        var command = await f.PollAsync("test");
        await f.CompleteAsync(command, new("Completed", Guest: new(command.CommandId, "test", PreviewPhase.Ready,
            TestResults: [new(0, false, "BrowserAssertionFailed", "password=private-value")])));
        var wake = await f.Db.AgentPlatformEventOutbox.OrderByDescending(x => x.OccurredAt).ToListAsync();
        var recovered = await f.Service.ReadAsync(f.Org, f.Agent, job.Id, default);
        Assert.Equal("Failed", Assert.Single(recovered.TestRuns).Status);
        Assert.Contains(wake, item => System.Text.Json.JsonSerializer.Deserialize<PreviewChangedEvent>(item.DataJson, PreviewJson.Options)!.Revision == recovered.Revision);
        var actorGrant = (await f.Db.AgentInstallations.Include(x => x.Grant).SingleAsync(x => x.Id == f.Agent)).Grant!;
        var originalCapabilities = actorGrant.RequiredCapabilitiesJson;
        actorGrant.RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { WebPreviewCapabilities.Read }); await f.Db.SaveChangesAsync();
        var withoutEvidencePermission = await f.Service.ReadAsync(f.Org, f.Agent, job.Id, default);
        Assert.Null(Assert.Single(withoutEvidencePermission.TestRuns).Results);
        actorGrant.RequiredCapabilitiesJson = originalCapabilities; await f.Db.SaveChangesAsync();
        var completed = await f.Service.TestAsync(f.Org, f.Agent, request, default);
        Assert.Equal("Failed", completed.Status); Assert.DoesNotContain("private-value", Assert.Single(completed.Results!).Summary);
        Assert.Single(await f.Db.WebPreviewFindings.ToListAsync());
        Assert.DoesNotContain("private-value", (await f.Db.WebHostCommands.SingleAsync(x => x.Id == command.CommandId)).ResponseJson!);
        f.Clock.Now = f.Clock.Now.AddDays(8);
        Assert.Equal("Expired", (await f.Service.TestAsync(f.Org, f.Agent, request, default)).Status);
    }
    [Theory]
    [InlineData("https://outside.example/")]
    [InlineData("//127.0.0.1:2762")]
    public void Browser_checks_do_not_accept_external_destinations(string path) =>
        Assert.Throws<InvalidDataException>(() => BrowserTestPolicy.Validate([new(path)]));
    [Fact] public async Task Preview_builds_require_both_ordinary_build_authority_and_the_scoped_hosting_grant()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var request = new PreviewBuildRequest(f.Request("build"), Guid.NewGuid(), Guid.NewGuid(), "build", "web", JsonSerializer.SerializeToElement(new { }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.AuthorizeBuildAsync(f.Org, f.Agent, request, default));
        var installation = await f.Db.AgentInstallations.Include(x => x.Grant).SingleAsync(x => x.Id == f.Agent);
        installation.Grant!.RequiredCapabilitiesJson = JsonSerializer.Serialize(WebPreviewCapabilities.All.Concat(["platform.build.request.v2"]));
        await f.Db.SaveChangesAsync();
        Assert.NotEqual(Guid.Empty, await f.Service.AuthorizeBuildAsync(f.Org, f.Agent, request, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.AuthorizeBuildAsync(f.Org, f.Agent,
            request with { Preview = request.Preview with { RepositoryId = Guid.NewGuid() } }, default));
        f.Grant.Status = "Revoked"; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.AuthorizeBuildAsync(f.Org, f.Agent, request, default));
    }}


