using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class WorkAssignmentManifestPolicyTests
{
    [Theory]
    [InlineData("{\"rolePolicy\":{\"declaredRoleKeys\":[\"software-developer\"]}}")]
    [InlineData("{\"rolePolicy\":{\"declaredRoleKeys\":[\"software-developer\"],\"specializationKeys\":[]}}")]
    public void GeneralistCanFillSameCoreRoleWithoutOptionalSkills(string manifest)
    {
        var requirements = new WorkAssignmentRequirements("game-engineer", [], ["gameplay-programming"], ["work.execution.run.v1"]);
        Assert.Null(WorkOrchestrationService.ValidateManifestAssignmentPolicy(manifest, requirements, Evidence([])));
    }

    [Fact]
    public void OmittedSkillsCannotSatisfyRequiredSpecializations()
    {
        var result = WorkOrchestrationService.ValidateManifestAssignmentPolicy(
            "{\"rolePolicy\":{\"declaredRoleKeys\":[\"software-developer\"]}}",
            new("game-engineer", ["gameplay-programming"], [], []), Evidence(["gameplay-programming"]));
        Assert.Contains("required specialization", result);
    }

    [Theory]
    [InlineData("{\"rolePolicy\":{\"declaredRoleKeys\":[\"software-qa\"]}}")]
    [InlineData("{\"rolePolicy\":{\"declaredRoleKeys\":[\"software-developer\"],\"specializationKeys\":null}}")]
    [InlineData("{\"rolePolicy\":{\"declaredRoleKeys\":[\"software-developer\"],\"specializationKeys\":\"invalid\"}}")]
    [InlineData("{\"rolePolicy\":{}}")]
    public void WrongRoleAndMalformedPoliciesStillFail(string manifest)
    {
        Assert.NotNull(WorkOrchestrationService.ValidateManifestAssignmentPolicy(manifest,
            new("game-engineer", [], [], []), Evidence([])));
    }

    private static WorkAssignmentSelectionEvidence Evidence(IReadOnlyList<string> matched) =>
        new(Guid.NewGuid(), 1, "profile", matched, "selection", DateTimeOffset.UtcNow);
}
