using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ConnectorMediaSourceTests
{
    [Fact]
    public async Task ExactRetainedSource_ResolvesMetadataOnly()
    {
        await using var f = await ConnectorPlanServiceTests.Fixture.Create();
        var asset = Asset(f.Organization);
        var source = await AttachAsync(f.Db, f.Organization, f.Requester, asset);
        var service = new ConnectorMediaSourceService(f.Db);
        var result = await service.ResolveAsync(f.Organization, f.Requester.Id, asset.Id, source, default);
        Assert.Equal(new ConnectorMediaBinding(asset.Id, asset.Sha256, asset.SizeBytes, asset.ContentType,
            source, asset.FileName), result);
        Assert.Empty(await f.Db.ConnectorExecutions.ToArrayAsync());
        Assert.Empty(await f.Db.ActionProposals.ToArrayAsync());
    }

    [Theory]
    [InlineData("organization")]
    [InlineData("installation")]
    [InlineData("asset")]
    [InlineData("conversation")]
    [InlineData("message")]
    [InlineData("attachment")]
    [InlineData("absent")]
    public async Task KnownAssetId_DoesNotConferAccess(string mutation)
    {
        await using var f = await ConnectorPlanServiceTests.Fixture.Create();
        var asset = Asset(f.Organization);
        ConversationAttachmentReference? source = await AttachAsync(f.Db, f.Organization, f.Requester, asset);
        if (mutation == "conversation") source = source with { ConversationId = Guid.NewGuid() };
        if (mutation == "message") source = source with { MessageId = Guid.NewGuid() };
        if (mutation == "attachment") source = source with { AttachmentId = Guid.NewGuid() };
        if (mutation == "absent") source = null;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new ConnectorMediaSourceService(f.Db).ResolveAsync(
            mutation == "organization" ? Guid.NewGuid() : f.Organization,
            mutation == "installation" ? Guid.NewGuid() : f.Requester.Id,
            mutation == "asset" ? Guid.NewGuid() : asset.Id, source, default));
    }

    [Theory]
    [InlineData("left")]
    [InlineData("archived")]
    [InlineData("inactive")]
    [InlineData("disabled")]
    [InlineData("grant")]
    [InlineData("message-removed")]
    [InlineData("asset-removed")]
    [InlineData("hash")]
    [InlineData("size")]
    [InlineData("type")]
    public async Task SourceMustRemainAccessibleAndMatchRetainedMetadata(string mutation)
    {
        await using var f = await ConnectorPlanServiceTests.Fixture.Create();
        var asset = Asset(f.Organization);
        var source = await AttachAsync(f.Db, f.Organization, f.Requester, asset);
        var service = new ConnectorMediaSourceService(f.Db);
        _ = await service.ResolveAsync(f.Organization, f.Requester.Id, asset.Id, source, default);
        switch (mutation)
        {
            case "left": (await f.Db.CoreConversations.Include(x => x.Participants).SingleAsync()).Participants.Single().LeftAt = DateTimeOffset.UtcNow; break;
            case "archived": (await f.Db.CoreConversations.SingleAsync()).ArchivedAt = DateTimeOffset.UtcNow; break;
            case "inactive": (await f.Db.CoreOrganizationUsers.SingleAsync()).IsActive = false; break;
            case "disabled": f.Requester.IsEnabled = false; break;
            case "grant": f.Requester.Grant!.RequiredCapabilitiesJson = "[]"; break;
            case "message-removed": f.Db.CoreConversationMessages.Remove(await f.Db.CoreConversationMessages.SingleAsync()); break;
            case "asset-removed":
                f.Db.ConversationMessageAttachments.Remove(await f.Db.ConversationMessageAttachments.SingleAsync());
                f.Db.MediaAssets.Remove(asset);
                break;
            case "hash": asset.Sha256 = new string('b', 64); break;
            case "size": asset.SizeBytes++; break;
            case "type": asset.ContentType = "application/octet-stream"; break;
        }
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ResolveAsync(f.Organization,
            f.Requester.Id, asset.Id, source, default));
    }

    internal static MediaAsset Asset(Guid organization) => new()
    {
        Id = Guid.NewGuid(), OrganizationId = organization, FileName = "company-video.mp4", ContentType = "video/mp4",
        SizeBytes = 128, Sha256 = new string('a', 64), StorageKey = "never-return-this-path"
    };

    internal static async Task<ConversationAttachmentReference> AttachAsync(CSweetDbContext db, Guid organization,
        AgentInstallation requester, MediaAsset asset)
    {
        if (!db.MediaAssets.Local.Contains(asset)) db.MediaAssets.Add(asset);
        var actor = db.CoreOrganizationUsers.Local.SingleOrDefault(x => x.AgentInstallationId == requester.Id);
        if (actor is null)
        {
            actor = new() { Id = Guid.NewGuid(), OrganizationId = organization, AgentInstallationId = requester.Id,
                DisplayName = "Specialist", EmployeeType = EmployeeType.Agent, IsActive = true };
            db.CoreOrganizationUsers.Add(actor);
        }
        var capabilities = JsonSerializer.Deserialize<string[]>(requester.Grant!.RequiredCapabilitiesJson) ?? [];
        requester.Grant.RequiredCapabilitiesJson = JsonSerializer.Serialize(capabilities.Append(CommunicationCapabilities.ChatRead).Distinct());
        var source = new ConversationAttachmentReference(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        db.CoreConversations.Add(new() { Id = source.ConversationId, OrganizationId = organization,
            InitiatedByOrganizationUserId = actor.Id,
            Participants = [new() { Id = Guid.NewGuid(), ConversationId = source.ConversationId, OrganizationUserId = actor.Id }] });
        db.CoreConversationMessages.Add(new() { Id = source.MessageId, ConversationId = source.ConversationId,
            Content = "Use the attached file", SenderOrganizationUserId = actor.Id });
        db.ConversationMessageAttachments.Add(new() { Id = source.AttachmentId, OrganizationId = organization,
            ConversationId = source.ConversationId, MessageId = source.MessageId, MediaAssetId = asset.Id,
            FileName = asset.FileName, ContentType = asset.ContentType, SizeBytes = asset.SizeBytes, Sha256 = asset.Sha256 });
        await db.SaveChangesAsync();
        return source;
    }
}
