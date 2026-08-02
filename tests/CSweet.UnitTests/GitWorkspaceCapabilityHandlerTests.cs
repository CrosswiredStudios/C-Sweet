using CSweet.AgentHost.Broker;
using CSweet.Domain.Setup;

namespace CSweet.UnitTests;

public sealed class GitWorkspaceCapabilityHandlerTests
{
    [Fact]
    public void TeamRepositoryOptionRequiresDeveloperDeliveryRightsAndQaReadAccess()
    {
        var developerId = Guid.NewGuid();
        var qualityId = Guid.NewGuid();
        var developer = Grant(developerId, read: true, push: true, merge: true);
        var quality = Grant(qualityId, read: true, push: false, merge: false);

        Assert.True(GitWorkspaceCapabilityHandler.IsCommonDeliveryRepository(
            [developer, quality], developerId, qualityId));
        Assert.False(GitWorkspaceCapabilityHandler.IsCommonDeliveryRepository(
            [Grant(developerId, read: true, push: true, merge: false), quality], developerId, qualityId));
        Assert.False(GitWorkspaceCapabilityHandler.IsCommonDeliveryRepository(
            [developer, Grant(qualityId, read: false, push: false, merge: false)], developerId, qualityId));
        var revokedQuality = Grant(qualityId, read: true, push: false, merge: false);
        revokedQuality.RevokedAt = DateTimeOffset.UtcNow;
        Assert.False(GitWorkspaceCapabilityHandler.IsCommonDeliveryRepository(
            [developer, revokedQuality], developerId, qualityId));
    }

    private static GitRepositoryConnectionGrant Grant(
        Guid installationId,
        bool read,
        bool push,
        bool merge) => new()
    {
        Id = Guid.NewGuid(),
        RepositoryConnectionId = Guid.NewGuid(),
        AgentInstallationId = installationId,
        CanReadFetch = read,
        CanPushTicketBranch = push,
        CanMergeQaApprovedPullRequest = merge,
        GrantedAt = DateTimeOffset.UtcNow
    };
}
