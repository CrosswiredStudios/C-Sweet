using System.Text.Json;
using CSweet.Contracts.Analytics;
using CSweet.Domain.Analytics;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Analytics;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class BenchmarkTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-16T13:00:00Z");
    private static BenchmarkService Service(CSweetDbContext db) => new(db, new WorkEfficiencyTests.Clock(Now), null!, null!, null!, null!, null!);
    private static async Task<BenchmarkDefinition> Definition(CSweetDbContext db, string assistance = "Assisted", bool approvals = false)
    {
        var model = new BenchmarkModel(Guid.NewGuid(), "test-model");
        var blueprint = new BenchmarkBlueprint("Benchmark", "Deliver a product",
            [new("lead", "Lead", Guid.NewGuid(), Guid.NewGuid())],
            [new("A", model, new Dictionary<string, BenchmarkModel>()), new("B", model with { Model = "other" },
                new Dictionary<string, BenchmarkModel> { ["lead"] = model with { Model = "lead-model" } })],
            [new("document", "Product exists", "ArtifactExists"), new("quality", "Product quality", "Rubric")], [], assistance, approvals);
        var d = new BenchmarkDefinition { Id = Guid.NewGuid(), FamilyId = Guid.NewGuid(), Version = 1,
            Name = blueprint.Name, Digest = "digest", BlueprintJson = JsonSerializer.Serialize(blueprint, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        db.BenchmarkDefinitions.Add(d); await db.SaveChangesAsync(); return d;
    }

    [Fact]
    public async Task Launch_IsIdempotent_RandomOrderHasEveryVariantPerRepetition_AndRejectsKeyReuse()
    {
        await using var db = WorkEfficiencyTests.Database(); var definition = await Definition(db);
        var service = Service(db); var user = Guid.NewGuid(); var request = new LaunchBenchmarkRequest(definition.Id, "launch", 3);
        var first = await service.LaunchAsync(request, user); var second = await service.LaunchAsync(request, user);
        Assert.Equal(first.Id, second.Id); Assert.Equal(6, first.Trials.Count);
        Assert.Equal(6, await db.BenchmarkWakes.CountAsync());
        foreach (var group in first.Trials.GroupBy(x => x.Repetition)) Assert.Equal(new[] { 0, 1 }, group.Select(x => x.VariantIndex).Order());
        await Assert.ThrowsAsync<ArgumentException>(() => service.LaunchAsync(request with { Repetitions = 2 }, user));
        Assert.Single(await db.BenchmarkCampaigns.ToListAsync());
    }

    [Theory]
    [InlineData("Assisted", false, false, true)]
    [InlineData("Assisted", true, false, false)]
    [InlineData("Assisted", true, true, true)]
    [InlineData("Unattended", false, true, false)]
    [InlineData("Unattended", false, false, false)]
    public async Task AssistancePolicy_EnforcesMode(string mode, bool approvals, bool approvalAction, bool allowed)
    {
        await using var db = WorkEfficiencyTests.Database(); var d = await Definition(db, mode, approvals);
        var c = await Service(db).LaunchAsync(new(d.Id, "launch"), Guid.NewGuid());
        var trial = await db.BenchmarkTrials.FindAsync(c.Trials[0].Id); trial!.OrganizationId = Guid.NewGuid(); trial.Status = "Running";
        await db.SaveChangesAsync();
        Assert.Equal(allowed, await BenchmarkHumanPolicy.AllowsAsync(db, trial.OrganizationId.Value, approvalAction, default));
        Assert.True(await BenchmarkHumanPolicy.AllowsAsync(db, Guid.NewGuid(), false, default));
    }

    [Fact]
    public async Task Cancel_LastTrialFinishesCampaign_AndPreservesPartialUsage()
    {
        await using var db = WorkEfficiencyTests.Database(); var d = await Definition(db); var service = Service(db);
        var c = await service.LaunchAsync(new(d.Id, "launch"), Guid.NewGuid());
        foreach (var t in c.Trials) await service.CancelAsync(t.Id);
        var result = await service.GetCampaignAsync(c.Id);
        Assert.Equal("DeliveryFinished", result!.Status); Assert.All(result.Trials, x => Assert.Equal("Cancelled", x.Status));
        await service.CancelAsync(c.Trials[0].Id);
        Assert.All(result.Trials, x => Assert.Null(x.DeliveryTimeMs));
    }

    [Fact]
    public async Task DefinitionsAndAssessments_AreAppendOnly_ReviewsAreIdempotent()
    {
        await using var db = WorkEfficiencyTests.Database(); var d = await Definition(db); var service = Service(db); var user = Guid.NewGuid();
        var c = await service.LaunchAsync(new(d.Id, "launch"), user);
        var trial = await db.BenchmarkTrials.FindAsync(c.Trials[0].Id);
        var request = new BenchmarkAssessmentRequest("Human", "quality", 4, null, "Evidence supports the score", [], "review");
        await Assert.ThrowsAsync<ArgumentException>(() => service.AssessAsync(trial!.Id, request, user));
        trial!.DeclaredCompletedAt = Now; trial.Status = "DeclaredComplete"; await db.SaveChangesAsync();
        var a = await service.AssessAsync(trial.Id, request, user);
        Assert.Equal(a.Id, (await service.AssessAsync(trial.Id, request, user)).Id);
        await Assert.ThrowsAsync<ArgumentException>(() => service.AssessAsync(trial.Id, request with { EvidenceReferences = ["different"] }, user));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AssessAsync(trial.Id, request with { Kind = "Check" }, user));
        d.Name = "modified"; await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.Entry(d).State = EntityState.Unchanged;
        var assessment = await db.BenchmarkAssessments.FindAsync(a.Id); assessment!.Score = 5;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Advance_UsesFirstDeclarationEvenAfterReopen_FreezesRevisions_ThenChecksAndAwaitsHuman()
    {
        await using var db = WorkEfficiencyTests.Database(); var d = await Definition(db); var service = Service(db);
        var c = await service.LaunchAsync(new(d.Id, "launch"), Guid.NewGuid());
        var trial = await db.BenchmarkTrials.FindAsync(c.Trials[0].Id); var org = Guid.NewGuid(); var projectId = Guid.NewGuid();
        trial!.OrganizationId = org; trial.WorkstreamId = projectId; trial.Status = "Running"; trial.StartedAt = Now.AddHours(-2);
        var project = new Workstream { Id = projectId, OrganizationId = org, Name = "Product", Status = WorkstreamStatus.Active, CreatedAt = Now.AddHours(-3), UpdatedAt = Now.AddHours(-3) };
        db.Workstreams.Add(project); await db.SaveChangesAsync();
        var artifact = new Artifact { Id = Guid.NewGuid(), OrganizationId = org, WorkstreamId = projectId, Title = "Product", Content = "current" };
        db.CoreArtifacts.Add(artifact);
        var revision = new ArtifactRevision { Id = Guid.NewGuid(), ArtifactId = artifact.Id, OrganizationId = org, Number = 1,
            Content = "frozen product", ContentSha256 = "digest", CreatedAt = Now.AddMinutes(-65), IdempotencyKey = "1" };
        db.ArtifactRevisions.Add(revision); await db.SaveChangesAsync();
        project.Status = WorkstreamStatus.Completed; project.UpdatedAt = Now.AddHours(-1); await db.SaveChangesAsync();
        project.Status = WorkstreamStatus.Active; project.UpdatedAt = Now.AddMinutes(-30); await db.SaveChangesAsync();
        db.ArtifactRevisions.Add(new ArtifactRevision { Id = Guid.NewGuid(), ArtifactId = artifact.Id, OrganizationId = org, Number = 2,
            Content = "later modification", ContentSha256 = "new", CreatedAt = Now.AddMinutes(-20), IdempotencyKey = "2" });
        await db.SaveChangesAsync();
        Assert.True(await service.AdvanceAsync());
        Assert.Equal("DeclaredComplete", trial.Status); Assert.Equal(Now.AddHours(-1), trial.DeclaredCompletedAt);
        Assert.Equal("AwaitingHuman", trial.EvaluationStatus);
        Assert.Contains("frozen product", trial.SubmissionJson); Assert.DoesNotContain("later modification", trial.SubmissionJson);
        Assert.True((await db.BenchmarkAssessments.SingleAsync()).Passed);
        await service.AssessAsync(trial.Id, new("Human", "quality", 3, null, "Reviewed revision", [], "review"), Guid.NewGuid());
        // Consume its own wake before the second trial can be admitted.
        trial.NextRecoveryAt = Now.AddSeconds(-1); await db.SaveChangesAsync();
        await service.CancelAsync(c.Trials[1].Id);
        Assert.True(await service.AdvanceAsync()); Assert.Equal("Complete", trial.EvaluationStatus);
    }

    [Fact]
    public async Task ModelPolicy_AppliesDefaultAndRoleOverride_ToAnAdaptiveHire()
    {
        await using var db = WorkEfficiencyTests.Database(); var d = await Definition(db); var service = Service(db);
        var c = await service.LaunchAsync(new(d.Id, "launch"), Guid.NewGuid());
        var trial = await db.BenchmarkTrials.FindAsync(c.Trials.Single(x => x.VariantIndex == 1).Id); trial!.OrganizationId = Guid.NewGuid();
        await db.SaveChangesAsync();
        var settings = new Dictionary<string, JsonElement> { ["llmModel"] = JsonSerializer.SerializeToElement("wrong") };
        await BenchmarkModelPolicy.ApplyAsync(db, Guid.NewGuid(), trial.OrganizationId.ToString()!, settings, default);
        Assert.Equal("other", settings["llmModel"].GetString());
        var installation = Guid.NewGuid(); var role = new Role { Id = Guid.NewGuid(), OrganizationId = trial.OrganizationId.Value, Name = "lead" };
        db.CoreRoles.Add(role);
        db.CoreOrganizationUsers.Add(new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = trial.OrganizationId.Value,
            RoleId = role.Id, Role = role, AgentInstallationId = installation, DisplayName = "Later lead", EmployeeType = EmployeeType.Agent });
        await db.SaveChangesAsync();
        await BenchmarkModelPolicy.ApplyAsync(db, installation, trial.OrganizationId.ToString()!, settings, default);
        Assert.Equal("lead-model", settings["llmModel"].GetString());
        var unrelated = new Dictionary<string, JsonElement>();
        await BenchmarkModelPolicy.ApplyAsync(db, Guid.NewGuid(), Guid.NewGuid().ToString(), unrelated, default);
        Assert.Empty(unrelated);
    }
}
