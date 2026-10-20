using System.Text.Json;
using CSweet.Agent.Contracts.Packaging;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class DeveloperBriefAuthorityTests
{
    private static readonly string[] Workspace = [WorkItemCapabilities.Read, WorkItemCapabilities.Comment,
        GitWorkspaceCapabilities.Prepare, GitWorkspaceCapabilities.Refresh, GitWorkspaceCapabilities.Inspect,
        GitWorkspaceCapabilities.Publish, GitWorkspaceCapabilities.Cleanup];

    [Fact]
    public void GovernedStageDoesNotRequireOrGrantLegacyLifecycleCapabilities()
    {
        var actions = SoftwareDevelopmentWorkService.ValidatePackageForAssignment(Manifest(Workspace), true);
        Assert.Equal(Workspace.Order(), actions.Order());
        Assert.DoesNotContain(WorkItemCapabilities.Start, actions);
        Assert.DoesNotContain(WorkItemCapabilities.Complete, actions);
        var extra = SoftwareDevelopmentWorkService.ValidatePackageForAssignment(
            Manifest([.. Workspace, WorkItemCapabilities.Start, WorkItemCapabilities.Complete, "unrelated.capability"]), true);
        Assert.Equal(actions.Order(), extra.Order());
    }

    [Fact]
    public void LegacyAssignmentsRetainTheirStartAndCompleteRequirements()
    {
        Assert.Throws<InvalidOperationException>(() => SoftwareDevelopmentWorkService.ValidatePackageForAssignment(Manifest(Workspace), false));
        var expected = Workspace.Concat([WorkItemCapabilities.Start, WorkItemCapabilities.Complete]).ToArray();
        Assert.Equal(expected.Order(), SoftwareDevelopmentWorkService.ValidatePackageForAssignment(Manifest(expected), false).Order());
    }

    [Theory]
    [InlineData("work.item.read")]
    [InlineData("work.item.comment")]
    [InlineData("git.workspace.prepare.v2")]
    [InlineData("git.workspace.refresh.v2")]
    [InlineData("git.workspace.inspect.v2")]
    [InlineData("git.workspace.publish.v2")]
    [InlineData("git.workspace.cleanup.v2")]
    public void GovernedAssignmentStillRequiresEveryWorkspaceCapability(string missing)
    {
        Assert.Contains(missing, Workspace);
        var error = Assert.Throws<InvalidOperationException>(() => SoftwareDevelopmentWorkService.ValidatePackageForAssignment(
            Manifest(Workspace.Where(x => x != missing).ToArray()), true));
        Assert.Contains(missing, error.Message);
    }

    [Theory]
    [InlineData(false, "software-development-polyglot-v1", "ReadWrite", 1)]
    [InlineData(true, "other-runtime", "ReadWrite", 1)]
    [InlineData(true, "software-development-polyglot-v1", "ReadOnly", 1)]
    [InlineData(true, "software-development-polyglot-v1", "ReadWrite", 2)]
    public void GovernedAssignmentRequiresExecutionSupportAndTheConfinedSerializedRuntime(bool execution, string profile, string access, int jobs) =>
        Assert.Throws<InvalidOperationException>(() => SoftwareDevelopmentWorkService.ValidatePackageForAssignment(Manifest(Workspace, execution, profile, access, jobs), true));

    [Fact]
    public void RepositoryBindingCannotReplaceTheCurrentDevelopmentPrincipal()
    {
        var id = Guid.NewGuid(); var item = new WorkTask();
        Assert.False(SoftwareDevelopmentWorkService.UsesGovernedDevelopmentStage(item, id));
        var assignment = new WorkItemStageAssignment { StageKey = "development", PrincipalKind = WorkOrchestrationPrincipalKind.AgentInstallation, AgentInstallationId = id };
        item.StageAssignments.Add(assignment);
        Assert.True(SoftwareDevelopmentWorkService.UsesGovernedDevelopmentStage(item, id));
        Assert.Throws<InvalidOperationException>(() => SoftwareDevelopmentWorkService.UsesGovernedDevelopmentStage(item, Guid.NewGuid()));
        assignment.PrincipalKind = WorkOrchestrationPrincipalKind.Human;
        Assert.Throws<InvalidOperationException>(() => SoftwareDevelopmentWorkService.UsesGovernedDevelopmentStage(item, id));
        assignment.PrincipalKind = WorkOrchestrationPrincipalKind.AgentInstallation;
        item.StageAssignments.Add(new() { StageKey = "development", PrincipalKind = WorkOrchestrationPrincipalKind.AgentInstallation, AgentInstallationId = id });
        Assert.Throws<InvalidOperationException>(() => SoftwareDevelopmentWorkService.UsesGovernedDevelopmentStage(item, id));
    }

    private static AgentManifest Manifest(IReadOnlyList<string> requires, bool execution = true,
        string profile = "software-development-polyglot-v1", string access = "ReadWrite", int jobs = 1) => new()
    {
        Id = "independent.publisher.developer", Name = "Developer", Version = "1.0.0", Publisher = new("publisher", "Publisher"), Protocol = new("2.0", "2.0"),
        Runtime = new() { EnvironmentProfile = profile, WorkspaceAccess = access, MaximumConcurrentJobs = jobs },
        Requires = requires.Select(x => new AgentRequiredCapability(x)).ToArray(),
        Provides = execution ? [new(WorkManagementCapabilityNames.ExecutionRun, "Governed execution", JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { }), 300, "work-item")] : []
    };
}
