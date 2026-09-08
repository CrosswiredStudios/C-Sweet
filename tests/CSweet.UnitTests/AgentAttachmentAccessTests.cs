using CSweet.Contracts.Communications;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class AgentAttachmentAccessTests
{
    [Fact]
    public async Task AgentCannotManufactureASourceByAttachingAGuessedAsset()
    {
        await using var f = await ConnectorPlanServiceTests.Fixture.Create();
        var asset = ConnectorMediaSourceTests.Asset(f.Organization);
        asset.ContentType = "application/pdf";
        var original = await ConnectorMediaSourceTests.AttachAsync(f.Db, f.Organization, f.Requester, asset);
        var actor = await f.Db.CoreOrganizationUsers.SingleAsync();
        (await f.Db.CoreConversations.Include(x => x.Participants).SingleAsync()).Participants.Single().LeftAt = DateTimeOffset.UtcNow;
        var chat = new Conversation { Id = Guid.NewGuid(), OrganizationId = f.Organization, InitiatedByOrganizationUserId = actor.Id,
            Kind = ConversationKind.AgentChannel, Participants = [new() { Id = Guid.NewGuid(), OrganizationUserId = actor.Id }] };
        f.Db.CoreConversations.Add(chat);
        await f.Db.SaveChangesAsync();
        var hub = new CommunicationHubService(f.Db, new TestAuditEventWriter(), new ChatTurnService(f.Db));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => hub.SendAsync(f.Organization, chat.Id, actor.Id,
            new SendCommunicationMessageRequest("Attach guessed asset", "launder", null, [asset.Id])));
        Assert.Single(await f.Db.ConversationMessageAttachments.ToArrayAsync());
        Assert.False(await f.Db.CoreConversationMessages.AnyAsync(x => x.ConversationId == chat.Id));
    }

    [Theory]
    [InlineData("visible", true)]
    [InlineData("read-grant", false)]
    [InlineData("own", true)]
    [InlineData("other-owner", false)]
    [InlineData("wrong-organization", false)]
    [InlineData("project-manager", true)]
    [InlineData("unassigned-project", false)]
    public async Task SharingRequiresExistingVisibilityOrOwnedAuthorizedWork(string mode, bool permitted)
    {
        await using var f = await ConnectorPlanServiceTests.Fixture.Create();
        var asset = ConnectorMediaSourceTests.Asset(f.Organization);
        var source = await ConnectorMediaSourceTests.AttachAsync(f.Db, f.Organization, f.Requester, asset);
        var actor = await f.Db.CoreOrganizationUsers.SingleAsync();
        if (mode is not ("visible" or "read-grant"))
            (await f.Db.CoreConversations.Include(x => x.Participants).SingleAsync()).Participants.Single().LeftAt = DateTimeOffset.UtcNow;
        if (mode == "own") asset.CreatingAgentInstallationId = f.Requester.Id;
        if (mode == "read-grant") f.Requester.Grant!.RequiredCapabilitiesJson = "[]";
        if (mode == "other-owner") asset.CreatingAgentInstallationId = Guid.NewGuid();
        if (mode == "wrong-organization") asset.OrganizationId = Guid.NewGuid();
        if (mode is "project-manager" or "unassigned-project")
        {
            var workstream = new Workstream { Id = Guid.NewGuid(), OrganizationId = f.Organization, Name = "Media",
                AccountableManagerOrganizationUserId = mode == "project-manager" ? actor.Id : Guid.NewGuid() };
            f.Db.Workstreams.Add(workstream);
            asset.WorkstreamId = workstream.Id;
            // Being the original creator cannot override a revoked project assignment.
            asset.CreatingAgentInstallationId = f.Requester.Id;
        }
        await f.Db.SaveChangesAsync();
        var service = new AgentAttachmentAccessService(f.Db);
        if (permitted) await service.RequireAsync(actor, [asset], default);
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RequireAsync(actor, [asset], default));
    }
}
