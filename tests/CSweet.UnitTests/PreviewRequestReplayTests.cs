using CSweet.AgentHost.Broker;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class PreviewRequestReplayTests
{
    [Theory]
    [InlineData("same")]
    [InlineData("workstream")]
    [InlineData("build")]
    [InlineData("mode")]
    [InlineData("lifetime")]
    [InlineData("evidence")]
    [InlineData("actor")]
    public async Task PreviewRetryMustRemainBoundToItsOriginalRequest(string change)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var otherActor = Guid.NewGuid();
        var stream = Guid.NewGuid(); var otherStream = Guid.NewGuid(); var build = Guid.NewGuid();
        db.Workstreams.AddRange(new Workstream { Id = stream, OrganizationId = org, AccountableManagerOrganizationUserId = actor },
            new Workstream { Id = otherStream, OrganizationId = org, AccountableManagerOrganizationUserId = actor });
        db.DeliveryBuilds.Add(new() { Id = build, OrganizationId = org, WorkstreamId = stream, Status = "Succeeded" });
        await db.SaveChangesAsync();
        var handler = new DeliveryEvidenceCapabilityHandler(db, TimeProvider.System);
        var request = new CreatePreviewV2Request(stream, build, "web-static", TimeSpan.FromHours(1), ["screenshot"], "preview-key");
        var first = await handler.CreatePreviewAsync(org, actor, request, CancellationToken.None);
        db.ChangeTracker.Clear();
        var replay = change switch
        {
            "workstream" => request with { WorkstreamId = otherStream },
            "build" => request with { BuildId = Guid.NewGuid() },
            "mode" => request with { Mode = "download" },
            "lifetime" => request with { Lifetime = TimeSpan.FromHours(2) },
            "evidence" => request with { EvidenceTypeKeys = [] },
            _ => request
        };
        if (change == "actor")
        {
            (await db.Workstreams.SingleAsync(x => x.Id == stream)).AccountableManagerOrganizationUserId = otherActor;
            await db.SaveChangesAsync();
        }
        if (change == "same")
        {
            var result = await handler.CreatePreviewAsync(org, actor, replay, CancellationToken.None);
            Assert.Equal(first.Id, result.Id); Assert.Equal(first.ExpiresAt, result.ExpiresAt);
        }
        else await Assert.ThrowsAsync<InvalidOperationException>(() => handler.CreatePreviewAsync(org,
            change == "actor" ? otherActor : actor, replay, CancellationToken.None));
        db.ChangeTracker.Clear();
        var saved = Assert.Single(await db.PreviewSessions.ToListAsync());
        Assert.Equal(stream, saved.WorkstreamId); Assert.Equal(build, saved.BuildId);
        Assert.Equal(first.ExpiresAt, saved.ExpiresAt);
    }
}
