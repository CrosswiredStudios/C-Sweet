using CSweet.Agent.SDK;
using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class FinalizedWorkspaceGrantTests
{
    [Fact]
    public void FinalizedCustomStageGetsOnlyApprovedWorkspaceOperations()
    {
        var delivery = new WorkItemDeliverySpecification(Guid.NewGuid(), ["Move"], ["Moves"]) { BaseBranch = "main" };
        var approved = new[] { GitWorkspaceCapabilities.Prepare, GitWorkspaceCapabilities.Inspect, GitWorkspaceCapabilities.Publish,
            GitWorkspaceCapabilities.Cleanup, GitMergeCapabilities.Authorize, "unrelated.capability" };
        Assert.Equal(new[] { GitWorkspaceCapabilities.Prepare, GitWorkspaceCapabilities.Inspect, GitWorkspaceCapabilities.Publish,
            GitWorkspaceCapabilities.Cleanup }, WorkOrchestrator.FinalizedWorkspaceActions(delivery, approved));
        Assert.Equal(new[] { GitWorkspaceCapabilities.Prepare, GitWorkspaceCapabilities.Inspect },
            WorkOrchestrator.FinalizedWorkspaceActions(delivery, [GitWorkspaceCapabilities.Prepare, GitWorkspaceCapabilities.Inspect]));
        Assert.Empty(WorkOrchestrator.FinalizedWorkspaceActions(delivery, []));
        Assert.Empty(WorkOrchestrator.FinalizedWorkspaceActions(null, approved));
        Assert.Empty(WorkOrchestrator.FinalizedWorkspaceActions(delivery with { RepositoryId = Guid.Empty }, approved));
        Assert.Empty(WorkOrchestrator.FinalizedWorkspaceActions(delivery with { BaseBranch = "" }, approved));
    }
}
