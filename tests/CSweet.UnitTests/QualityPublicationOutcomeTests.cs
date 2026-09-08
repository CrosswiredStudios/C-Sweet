using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Shared = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class QualityPublicationOutcomeTests
{
    [Theory]
    [InlineData("passed", true)]
    [InlineData("failed", false)]
    [InlineData("changes_requested", false)]
    public async Task RecordsExactCandidateVerdictIncludingGameFailure(string code, bool passed)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var org = Guid.NewGuid(); var sha = new string('a', 40);
        var item = new WorkTask { Id = Guid.NewGuid(), OrganizationId = org, AssignmentRevision = 5 };
        var workspace = new SourceControlWorkspace { Id = Guid.NewGuid(), OrganizationId = org, WorkItemId = item.Id, AssignmentRevision = 5 };
        var publication = new SourceControlPublication { Id = Guid.NewGuid(), OrganizationId = org, WorkspaceId = workspace.Id,
            CommitSha = sha, Status = SourceControlPublicationStatus.AwaitingValidation };
        db.AddRange(item, workspace, publication); await db.SaveChangesAsync();
        var stage = new WorkStageExecution { StageKey = "quality", AgentInstallationId = Guid.NewGuid(),
            ItemExecution = new WorkItemExecution { WorkItem = item } };
        var execution = new WorkSprintExecution { OrganizationId = org };
        var outcome = new Shared.WorkExecutionOutcomeV1(Guid.NewGuid(), Guid.NewGuid(), Shared.WorkExecutionDispositions.Completed,
            code, "Executed game tests", JsonSerializer.SerializeToElement(new { passed }),
            [new Shared.WorkExecutionEvidence("commit", "Candidate", sha)], []);
        var orchestrator = new WorkOrchestrator(db, null!, null!, null!, [], TimeProvider.System, null!);
        var wrongCandidate = outcome with { Evidence = [new Shared.WorkExecutionEvidence("commit", "Wrong", new string('b', 40))] };
        Assert.NotNull(await orchestrator.RecordQualityValidationAsync(execution, stage, wrongCandidate, DateTimeOffset.UtcNow, default));
        Assert.Empty(db.SourceControlValidations.Local);
        Assert.Null(await orchestrator.RecordQualityValidationAsync(execution, stage, outcome, DateTimeOffset.UtcNow, default));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var validation = Assert.Single(db.SourceControlValidations);
        Assert.Equal(sha, validation.CommitSha);
        Assert.Equal(passed ? SourceControlValidationStatus.Passed : SourceControlValidationStatus.Failed, validation.Status);
        Assert.Equal(passed ? SourceControlPublicationStatus.AwaitingLeadAuthorization : SourceControlPublicationStatus.AwaitingValidation, (await db.SourceControlPublications.SingleAsync()).Status);
    }
}
