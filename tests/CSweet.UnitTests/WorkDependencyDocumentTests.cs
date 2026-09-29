using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shared = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class WorkDependencyDocumentTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("valid")]
    [InlineData("foreign-organization")]
    [InlineData("foreign-board")]
    [InlineData("unapproved")]
    [InlineData("wrong-reviewer")]
    [InlineData("old-review")]
    [InlineData("wrong-origin")]
    [InlineData("wrong-author")]
    [InlineData("wrong-hash")]
    [InlineData("changed-content")]
    [InlineData("wrong-attempt")]
    [InlineData("latest-failed")]
    [InlineData("reopened")]
    public async Task OnlyExactManagerAcceptedDependencyRevisionsReachTheAssignment(string condition)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var org = Guid.NewGuid(); var author = Guid.NewGuid(); var installation = Guid.NewGuid();
        var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = org, ManagerOrganizationUserId = Guid.NewGuid(),
            WorkstreamId = Guid.NewGuid(), TeamId = Guid.NewGuid() };
        var dependency = new WorkTask { Id = Guid.NewGuid(), OrganizationId = org, BoardId = board.Id,
            Identifier = "GAME-PLAN", Status = WorkTaskStatus.Completed };
        var consumer = new WorkTask { Id = Guid.NewGuid(), OrganizationId = org, BoardId = board.Id };
        consumer.Dependencies.Add(new WorkItemDependency { WorkItemId = consumer.Id, DependsOnWorkItemId = dependency.Id });
        var execution = new WorkSprintExecution { Id = Guid.NewGuid(), OrganizationId = org, BoardId = board.Id };
        var itemExecution = new WorkItemExecution { Id = Guid.NewGuid(), SprintExecutionId = execution.Id,
            WorkItemId = dependency.Id, Status = WorkItemExecutionStatus.Completed, Traversal = 0 };
        execution.Items.Add(itemExecution);
        var worker = new WorkStageExecution { Id = Guid.NewGuid(), ItemExecutionId = itemExecution.Id,
            StageKey = "specialist-execution", Status = WorkStageExecutionStatus.Completed, LastOutcomeCode = "completed",
            AgentInstallationId = installation, OrganizationUserId = author };
        var review = new WorkStageExecution { Id = Guid.NewGuid(), ItemExecutionId = itemExecution.Id,
            StageKey = "producer-review", Status = WorkStageExecutionStatus.Completed, LastOutcomeCode = "approved",
            OrganizationUserId = board.ManagerOrganizationUserId };
        itemExecution.Stages.Add(worker); itemExecution.Stages.Add(review);
        const string content = "Plan: use a fixed simulation step and a separate render loop.";
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        var document = new Artifact { Id = Guid.NewGuid(), OrganizationId = org, OriginWorkItemId = dependency.Id,
            CreatedByOrganizationUserId = author, WorkstreamId = board.WorkstreamId, TeamId = board.TeamId,
            Title = "Accepted foundation plan", Content = "Newer draft must not replace the delivered revision", LatestRevisionId = Guid.NewGuid() };
        var revision = new ArtifactRevision { Id = Guid.NewGuid(), OrganizationId = org, ArtifactId = document.Id,
            CreatedByAgentInstallationId = installation, Content = content, ContentSha256 = sha, Status = ArtifactRevisionStatus.Submitted };
        var attempt = new WorkExecutionAttempt { Id = Guid.NewGuid(), StageExecutionId = worker.Id,
            Status = WorkExecutionAttemptStatus.Completed, Attempt = 1 };
        var outcome = new Shared.WorkExecutionOutcomeV1(worker.Id, attempt.Id, "Completed", "completed", "Plan delivered",
            JsonSerializer.SerializeToElement(new { artifactId = document.Id, revisionId = revision.Id, sha256 = sha }), [], []);
        switch (condition)
        {
            case "foreign-organization": dependency.OrganizationId = Guid.NewGuid(); break;
            case "foreign-board": dependency.BoardId = Guid.NewGuid(); break;
            case "unapproved": review.LastOutcomeCode = "rejected"; break;
            case "wrong-reviewer": review.OrganizationUserId = Guid.NewGuid(); break;
            case "old-review": review.Traversal = 1; break;
            case "wrong-origin": document.OriginWorkItemId = Guid.NewGuid(); break;
            case "wrong-author": revision.CreatedByAgentInstallationId = Guid.NewGuid(); break;
            case "wrong-hash": revision.ContentSha256 = new string('b', 64); break;
            case "changed-content": revision.Content = "Tampered plan"; break;
            case "wrong-attempt": outcome = outcome with { AttemptId = Guid.NewGuid() }; break;
            case "latest-failed": worker.Attempts.Add(new WorkExecutionAttempt { Id = Guid.NewGuid(), StageExecutionId = worker.Id, Attempt = 2, Status = WorkExecutionAttemptStatus.Failed }); break;
            case "reopened": itemExecution.Status = WorkItemExecutionStatus.Running; break;
        }
        attempt.ResultJson = JsonSerializer.Serialize(outcome, Json); worker.Attempts.Add(attempt);
        db.AddRange(board, dependency, execution, document, revision);
        await db.SaveChangesAsync();
        var orchestrator = new WorkOrchestrator(db, null!, null!, null!, [], TimeProvider.System, NullLogger<WorkOrchestrator>.Instance);
        if (condition != "valid")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.DependencyDocumentsAsync(org, board, consumer, CancellationToken.None));
            return;
        }
        var evidence = Assert.Single(await orchestrator.DependencyDocumentsAsync(org, board, consumer, CancellationToken.None));
        Assert.Equal("dependency-document.v1", evidence.Kind);
        var payload = JsonSerializer.Deserialize<JsonElement>(evidence.Value);
        Assert.Equal(content, payload.GetProperty("content").GetString());
        Assert.Equal(revision.Id, payload.GetProperty("revisionId").GetGuid());
        Assert.Equal(attempt.Id, payload.GetProperty("attemptId").GetGuid());
        Assert.Empty(db.ScopedActionGrants); // Passing a work product adds no artifact browsing/editing authority.
    }
}
