using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class AgentBuildSummaryTests
{
    private const string BrokerFailure = "Headquarters rejected the authenticated guest broker session: The isolated guest workload exited (process-exited, exit 1).";
    private const string MissingSdk = "error NU1102: Unable to find package CSweet.Agent.SDK with version (>= 3.30.0)";

    [Fact]
    public async Task DefinitionRead_ShowsStoredNuGetFailureWithoutRebuildingOrChangingHistory()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var package = new AgentPackageVersion { Id = Guid.NewGuid(), AgentId = "test", AgentName = "Test agent" };
        var definition = new AgentDefinition { Id = Guid.NewGuid(), PackageVersion = package, PackageVersionId = package.Id };
        var build = FailedBuild("/run/build/Agent.csproj : " + MissingSdk + "\nRequired command 'dotnet' failed with exit code 1.");
        build.PackageVersion = package;
        build.PackageVersionId = package.Id;
        db.AddRange(package, definition, build);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = new AgentDefinitionService(db, new TestAuditEventWriter(), null!);

        var detail = await service.GetAsync(definition.Id);
        var listed = Assert.Single(await service.ListAsync());

        foreach (var response in new[] { detail!, listed })
        {
            Assert.Equal(MissingSdk, response.Build!.FailureMessage);
            Assert.True(response.Build.HasLog);
            var failed = Assert.Single(response.Build.Steps!, step => step.Status == "Failed");
            Assert.Equal(AgentBuildStepKeys.Restore, failed.Key);
            Assert.Equal(MissingSdk, failed.Error);
            Assert.DoesNotContain(response.Build.Steps!, step => step.Key == AgentBuildStepKeys.Isolate && step.Status == "Failed");
        }
        Assert.Equal(BrokerFailure, (await db.AgentBuildJobs.SingleAsync()).FailureMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("warning NU1903: Package has a known vulnerability.\nRequired command failed.")]
    [InlineData("The isolated guest did not start.")]
    public void MissingOrUnrecognizedDiagnostic_PreservesOriginalFailure(string? diagnostic)
    {
        var summary = AgentBuildSummaryMapper.Create(FailedBuild(diagnostic));
        Assert.Equal(BrokerFailure, summary!.FailureMessage);
        Assert.Equal(AgentBuildStepKeys.Isolate, Assert.Single(summary.Steps!, step => step.Status == "Failed").Key);
    }

    [Fact]
    public void CompilerError_IsShownUnderPublish_AndControlsAreRemoved()
    {
        var summary = AgentBuildSummaryMapper.Create(FailedBuild("/run/Agent.cs(1,2): error CS1002: ; expected\0"));
        Assert.Equal("error CS1002: ; expected", summary!.FailureMessage);
        Assert.Equal(AgentBuildStepKeys.Publish, Assert.Single(summary.Steps!, step => step.Status == "Failed").Key);
    }

    [Fact]
    public void NuGetFailureDuringPublish_PreservesTheRecordedStage()
    {
        var build = FailedBuild(MissingSdk);
        build.StepsJson = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new AgentBuildStepResponse("isolate", "Prepare build environment", "Succeeded", null, null, null, null),
            new AgentBuildStepResponse("restore", "Restore dependencies", "Succeeded", null, null, null, null),
            new AgentBuildStepResponse("publish", "Compile and publish", "InProgress", null, null, null, null)
        }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var summary = AgentBuildSummaryMapper.Create(build);
        Assert.Equal("publish", Assert.Single(summary!.Steps!, step => step.Status == "Failed").Key);
    }

    [Fact]
    public void LatestAssignmentWithoutDiagnostics_DoesNotReuseAnOlderFailure()
    {
        var build = FailedBuild(MissingSdk);
        build.ExecutionAssignments.Add(new ExecutionWorkloadAssignment
        {
            Id = Guid.NewGuid(), QueuedAt = DateTimeOffset.UtcNow.AddMinutes(1),
            Status = ExecutionAssignmentStatus.Failed
        });
        Assert.Equal(BrokerFailure, AgentBuildSummaryMapper.Create(build)!.FailureMessage);
    }

    [Fact]
    public void RunningBuild_DoesNotShowDiagnosticsFromAPreviousAssignmentAttempt()
    {
        var build = new AgentBuildJob { Id = Guid.NewGuid() };
        build.ExecutionAssignments.Add(new ExecutionWorkloadAssignment
        {
            Id = Guid.NewGuid(), Status = ExecutionAssignmentStatus.Failed, ResultLogExcerpt = MissingSdk
        });
        Assert.Null(AgentBuildSummaryMapper.Create(build)!.FailureMessage);
    }

    [Fact]
    public void DiagnosticSummary_IsBoundedAndDeduplicated()
    {
        var summary = AgentBuildSummaryMapper.Create(FailedBuild(MissingSdk + "\n" + MissingSdk));
        Assert.Equal(MissingSdk, summary!.FailureMessage);
        summary = AgentBuildSummaryMapper.Create(FailedBuild(MissingSdk + new string('x', 8000)));
        Assert.Equal(1500, summary!.FailureMessage!.Length);
    }

    private static AgentBuildJob FailedBuild(string? diagnostic)
    {
        var job = new AgentBuildJob
        {
            Id = Guid.NewGuid(), LogPath = "build.log", QueuedAt = DateTimeOffset.UtcNow,
            FailureMessage = BrokerFailure
        };
        job.TransitionTo(AgentBuildStatus.Failed, DateTimeOffset.UtcNow);
        job.ExecutionAssignments.Add(new ExecutionWorkloadAssignment
        {
            Id = Guid.NewGuid(), AgentBuildJob = job, AgentBuildJobId = job.Id,
            Status = ExecutionAssignmentStatus.Failed, ResultLogExcerpt = diagnostic,
            QueuedAt = DateTimeOffset.UtcNow
        });
        return job;
    }
}
