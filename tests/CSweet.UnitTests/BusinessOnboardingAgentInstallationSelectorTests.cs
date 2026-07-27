using CSweet.Contracts.Agents;
using CSweet.UI.Services;

namespace CSweet.UnitTests;

public sealed class BusinessOnboardingAgentInstallationSelectorTests
{
    [Fact]
    public void FindReusable_ReturnsNewestMatchingUnassignedInstallation()
    {
        var packageVersionId = Guid.NewGuid();
        var older = Installation(packageVersionId, "default", createdAt: DateTimeOffset.UtcNow.AddMinutes(-2));
        var newer = Installation(packageVersionId, "default", createdAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var assigned = Installation(packageVersionId, Guid.NewGuid().ToString("D"), createdAt: DateTimeOffset.UtcNow);

        var result = BusinessOnboardingAgentInstallationSelector.FindReusable(
            [assigned, older, newer],
            packageVersionId,
            "com.csweet.chief-of-staff");

        Assert.Equal(newer.Id, result?.Id);
    }

    [Fact]
    public void FindReusable_IgnoresDisabledWrongPackageAndWrongAgentInstallations()
    {
        var packageVersionId = Guid.NewGuid();
        var disabled = Installation(packageVersionId, "default", isEnabled: false);
        var wrongPackage = Installation(Guid.NewGuid(), "default");
        var wrongAgent = Installation(packageVersionId, "default", agentId: "com.example.other");

        var result = BusinessOnboardingAgentInstallationSelector.FindReusable(
            [disabled, wrongPackage, wrongAgent],
            packageVersionId,
            "com.csweet.chief-of-staff");

        Assert.Null(result);
    }

    private static AgentInstallationResponse Installation(
        Guid packageVersionId,
        string businessId,
        string agentId = "com.csweet.chief-of-staff",
        bool isEnabled = true,
        DateTimeOffset? createdAt = null) =>
        new(
            Guid.NewGuid(),
            packageVersionId,
            businessId,
            agentId,
            "Chief of Staff",
            "1.8.0",
            "C-Sweet",
            new string('a', 40),
            isEnabled,
            [],
            [],
            [],
            [],
            [],
            512,
            50,
            new AgentScheduleResponse(
                Guid.NewGuid(),
                "AlwaysOn",
                3600,
                null,
                null,
                null,
                null,
                600,
                0,
                0,
                null,
                "Skip",
                true),
            createdAt ?? DateTimeOffset.UtcNow,
            createdAt ?? DateTimeOffset.UtcNow)
        {
            PluginKind = "Agent",
            RevisionStatus = "Active"
        };
}
