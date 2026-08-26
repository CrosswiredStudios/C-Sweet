using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class PlatformWorkTypeCatalogTests
{
    [Fact]
    public void SoftwareStoriesRequireArchitectureReviewAndExactParentType()
    {
        var type = PlatformWorkTypeCatalog.RequireType(
            WorkBoardProfileKeys.SoftwareDeliveryV1,
            WorkItemTypeKeys.SoftwareStoryV1,
            WorkItemTypeKeys.SoftwareEpicV1);

        Assert.Equal(WorkItemKinds.Story, type.Kind);
        Assert.Equal(WorkItemApprovalPolicyKeys.SoftwareArchitectureReviewV1,
            Assert.Single(type.RequiredApprovalPolicyKeys));
        Assert.Throws<ArgumentException>(() => PlatformWorkTypeCatalog.RequireType(
            WorkBoardProfileKeys.SoftwareDeliveryV1,
            WorkItemTypeKeys.SoftwareStoryV1,
            null));
    }

    [Fact]
    public void GeneralWorkHasNoArchitecturePolicyAndRejectsSoftwareTypes()
    {
        var type = PlatformWorkTypeCatalog.RequireType(
            WorkBoardProfileKeys.GeneralWorkV1,
            WorkItemTypeKeys.GeneralTaskV1,
            WorkItemTypeKeys.GeneralStoryV1);

        Assert.Empty(type.RequiredApprovalPolicyKeys);
        Assert.Throws<ArgumentException>(() => PlatformWorkTypeCatalog.RequireType(
            WorkBoardProfileKeys.GeneralWorkV1,
            WorkItemTypeKeys.SoftwareTaskV1,
            WorkItemTypeKeys.SoftwareStoryV1));
    }

    [Fact]
    public void BoardProfileCatalogIsImmutableAndProviderOwned()
    {
        var catalog = PlatformWorkTypeCatalog.Read();

        Assert.Equal(PlatformWorkTypeCatalog.Revision, catalog.Revision);
        Assert.Equal(2, catalog.BoardProfiles.Count);
        Assert.All(catalog.Types, type => Assert.Equal(PlatformWorkTypeCatalog.ProviderKey, type.ProviderKey));
        Assert.All(catalog.ApprovalPolicies,
            policy => Assert.Equal(PlatformWorkTypeCatalog.ProviderKey, policy.ProviderKey));
    }
}
