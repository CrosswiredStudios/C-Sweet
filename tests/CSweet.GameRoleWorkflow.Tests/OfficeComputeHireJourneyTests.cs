extern alias Engineer;
extern alias Developer;

using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.UnitTests;
using CSweet.WorkManagement.Contracts;
using GameEngineer = Engineer::CSweet.Agent.Engineer.VideoGame.GameEngineerAgent;
using SoftwareDeveloper = Developer::CSweet.Agents.SoftwareDeveloper.SoftwareDeveloperAgent;

namespace CSweet.GameRoleWorkflow.Tests;

public sealed class OfficeComputeHireJourneyTests
{
    [Theory, Trait("Category", "AgentHarness")]
    [InlineData("Game Engineer")]
    [InlineData("Software Developer")]
    public async Task Hired_developer_consumes_the_Headquarters_wake_and_claims_one_Office_workspace(string role)
    {
        var fixture = new ComputeBrokerTests.Fixture();
        AgentOperatingStateResponse? saved = null;
        var writes = 0;
        await AgentRuntimeManagerTests.VerifyHiredOfficePlacementAsync(fixture,
            () => role == "Game Engineer" ? new GameEngineer() : new SoftwareDeveloper(),
            platform => platform
                .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                    (request, _) => Task.FromResult(new AgentOperatingStateReadResponse(saved?.StateKey == request.StateKey ? saved : null)))
                .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,
                    (request, _) =>
                    {
                        Assert.Equal($"development/project-compute/{fixture.Workstream:N}", request.StateKey);
                        var now = DateTimeOffset.UtcNow;
                        saved = new(Guid.NewGuid(), request.StateKey, request.SchemaId, request.SchemaVersion, request.Status,
                            request.SourceRevisions, request.ConditionCodes, request.DecisionFingerprint, request.OpenCommitmentCorrelations,
                            request.AttentionReviewId, request.Payload, ++writes, now, now);
                        return Task.FromResult(saved);
                    })
                .RegisterCapability<JsonElement, PersonalTodoDirectory>(PersonalTodoCapabilities.Read, (_, _) =>
                    Task.FromResult(new PersonalTodoDirectory([]))));
        Assert.Equal(1, writes);
        Assert.NotEqual(Guid.Empty, saved!.Payload.GetProperty("environmentId").GetGuid());
    }
}
