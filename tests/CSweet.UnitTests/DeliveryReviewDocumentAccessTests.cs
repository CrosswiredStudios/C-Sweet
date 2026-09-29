using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class DeliveryReviewDocumentAccessTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("valid")]
    [InlineData("foreign-organization")]
    [InlineData("inactive-reviewer")]
    [InlineData("wrong-installation")]
    [InlineData("wrong-manager")]
    [InlineData("wrong-reviewer")]
    [InlineData("wrong-principal")]
    [InlineData("wrong-stage-type")]
    [InlineData("old-traversal")]
    [InlineData("review-complete")]
    [InlineData("cancelled-sprint")]
    [InlineData("reopened-item")]
    [InlineData("archived-document")]
    [InlineData("wrong-project")]
    [InlineData("wrong-origin")]
    [InlineData("wrong-author")]
    [InlineData("wrong-revision-author")]
    [InlineData("wrong-attempt")]
    [InlineData("latest-failed")]
    [InlineData("wrong-hash")]
    [InlineData("changed-content")]
    [InlineData("draft")]
    [InlineData("wrong-transition")]
    public async Task Review_reads_only_the_delivered_revision_under_current_assignment(string condition)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var org = Guid.NewGuid(); var author = Guid.NewGuid(); var authorInstallation = Guid.NewGuid();
        var reviewer = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org, IsActive = true,
            EmployeeType = EmployeeType.Agent, AgentInstallationId = Guid.NewGuid() };
        var installation = reviewer.AgentInstallationId!.Value;
        var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = org, ManagerOrganizationUserId = reviewer.Id,
            WorkstreamId = Guid.NewGuid(), TeamId = Guid.NewGuid() };
        var ticket = new WorkTask { Id = Guid.NewGuid(), OrganizationId = org, BoardId = board.Id,
            Status = WorkTaskStatus.WaitingForApproval };
        var sprint = new WorkSprintExecution { Id = Guid.NewGuid(), OrganizationId = org, BoardId = board.Id,
            PolicyRevisionId = Guid.NewGuid(), Status = WorkSprintExecutionStatus.Active };
        var execution = new WorkItemExecution { Id = Guid.NewGuid(), WorkItemId = ticket.Id, SprintExecutionId = sprint.Id,
            Status = WorkItemExecutionStatus.WaitingForApproval, CurrentStageKey = "producer-review" };
        sprint.Items.Add(execution);
        var worker = new WorkStageExecution { Id = Guid.NewGuid(), ItemExecutionId = execution.Id,
            StageKey = "specialist-execution", Status = WorkStageExecutionStatus.Completed, LastOutcomeCode = "completed",
            OrganizationUserId = author, AgentInstallationId = authorInstallation };
        var review = new WorkStageExecution { Id = Guid.NewGuid(), ItemExecutionId = execution.Id,
            StageKey = "producer-review", StageType = WorkOrchestrationStageType.ManagerApproval,
            PrincipalKind = WorkOrchestrationPrincipalKind.BoardManager, OrganizationUserId = reviewer.Id,
            Status = WorkStageExecutionStatus.WaitingForApproval };
        execution.Stages.Add(worker); execution.Stages.Add(review);
        var transition = new WorkOrchestrationTransition { Id = Guid.NewGuid(), PolicyRevisionId = sprint.PolicyRevisionId,
            FromStageKey = worker.StageKey, ToStageKey = review.StageKey, OutcomeCode = "completed" };
        const string content = "Submitted plan for engineering.";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        var artifact = new Artifact { Id = Guid.NewGuid(), OrganizationId = org, OriginWorkItemId = ticket.Id,
            CreatedByOrganizationUserId = author, WorkstreamId = board.WorkstreamId, TeamId = board.TeamId };
        var revision = new CSweet.Domain.Core.ArtifactRevision { Id = Guid.NewGuid(), ArtifactId = artifact.Id, OrganizationId = org,
            CreatedByOrganizationUserId = author, CreatedByAgentInstallationId = authorInstallation,
            Content = content, ContentSha256 = hash, Status = ArtifactRevisionStatus.Accepted, Number = 1 };
        var draft = new CSweet.Domain.Core.ArtifactRevision { Id = Guid.NewGuid(), ArtifactId = artifact.Id, OrganizationId = org,
            Content = "Unrelated newer draft must remain private", Status = ArtifactRevisionStatus.Draft, Number = 2 };
        artifact.LatestRevisionId = draft.Id;
        artifact.Revisions.Add(revision); artifact.Revisions.Add(draft);
        var attempt = new WorkExecutionAttempt { Id = Guid.NewGuid(), StageExecutionId = worker.Id,
            Status = WorkExecutionAttemptStatus.Completed, Attempt = 1 };
        var outcome = new W.WorkExecutionOutcomeV1(worker.Id, attempt.Id, W.WorkExecutionDispositions.Completed,
            "completed", "Plan delivered", JsonSerializer.SerializeToElement(new
            { artifactId = artifact.Id, revisionId = revision.Id, sha256 = hash }), [], []);
        switch (condition)
        {
            case "foreign-organization": artifact.OrganizationId = Guid.NewGuid(); break;
            case "inactive-reviewer": reviewer.IsActive = false; break;
            case "wrong-installation": reviewer.AgentInstallationId = Guid.NewGuid(); break;
            case "wrong-manager": board.ManagerOrganizationUserId = Guid.NewGuid(); break;
            case "wrong-reviewer": review.OrganizationUserId = Guid.NewGuid(); break;
            case "wrong-principal": review.PrincipalKind = WorkOrchestrationPrincipalKind.AgentInstallation; break;
            case "wrong-stage-type": review.StageType = WorkOrchestrationStageType.AgentExecution; break;
            case "old-traversal": review.Traversal = 1; break;
            case "review-complete": review.Status = WorkStageExecutionStatus.Completed; break;
            case "cancelled-sprint": sprint.Status = WorkSprintExecutionStatus.Cancelled; break;
            case "reopened-item": ticket.Status = WorkTaskStatus.Running; break;
            case "archived-document": artifact.ArchivedAt = DateTimeOffset.UtcNow; break;
            case "wrong-project": artifact.WorkstreamId = Guid.NewGuid(); break;
            case "wrong-origin": artifact.OriginWorkItemId = Guid.NewGuid(); break;
            case "wrong-author": artifact.CreatedByOrganizationUserId = Guid.NewGuid(); break;
            case "wrong-revision-author": revision.CreatedByAgentInstallationId = Guid.NewGuid(); break;
            case "wrong-attempt": outcome = outcome with { AttemptId = Guid.NewGuid() }; break;
            case "latest-failed": worker.Attempts.Add(new WorkExecutionAttempt { Id = Guid.NewGuid(), StageExecutionId = worker.Id,
                Status = WorkExecutionAttemptStatus.Failed, Attempt = 2 }); break;
            case "wrong-hash": revision.ContentSha256 = new string('b', 64); break;
            case "changed-content": revision.Content = "Tampered"; break;
            case "draft": revision.Status = ArtifactRevisionStatus.Draft; break;
            case "wrong-transition": transition.ToStageKey = "other-review"; break;
        }
        attempt.ResultJson = JsonSerializer.Serialize(outcome, Json); worker.Attempts.Add(attempt);
        db.AddRange(reviewer, board, ticket, sprint, artifact, transition);
        await db.SaveChangesAsync();
        var handler = new ArtifactCapabilityHandler(db, null!, new TestAuditEventWriter(), TimeProvider.System);
        var session = new AgentSession("session", "producer", installation.ToString(), org.ToString(), "runtime", "tick",
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(),
                new HashSet<string> { PlatformCapabilities.ArtifactRead, PlatformCapabilities.ArtifactRevise }, 1));
        var request = new RequestCapability { RequestId = "read", Capability = PlatformCapabilities.ArtifactRead,
            Payload = JsonPayload.From(new { artifactId = artifact.Id }, Json) };
        var results = new List<CapabilityResult>();
        await foreach (var result in handler.HandleAsync(session, request, default)) results.Add(result);
        var response = Assert.Single(results);
        Assert.Equal(condition == "valid", response.Succeeded);
        Assert.Empty(db.ScopedActionGrants);
        if (condition != "valid") return;
        var payload = JsonSerializer.Deserialize<JsonElement>(response.Payload.ToByteArray(), Json);
        var returned = Assert.Single(payload.GetProperty("revisions").EnumerateArray());
        Assert.Equal(revision.Id, returned.GetProperty("id").GetGuid());
        Assert.Equal(content, returned.GetProperty("content").GetString());
        // Reading this review does not authorize editing the document.
        results.Clear();
        await foreach (var result in handler.HandleAsync(session, new RequestCapability { RequestId = "revise",
            Capability = PlatformCapabilities.ArtifactRevise, Payload = JsonPayload.From(new
            { artifactId = artifact.Id, expectedBaseRevisionId = draft.Id, content = "changed", idempotencyKey = "test" }, Json) }, default)) results.Add(result);
        Assert.False(Assert.Single(results).Succeeded);
        // A completed/cancelled review loses this implicit input access on the very next read.
        review.Status = WorkStageExecutionStatus.Completed;
        await db.SaveChangesAsync();
        results.Clear();
        await foreach (var result in handler.HandleAsync(session, request, default)) results.Add(result);
        Assert.False(Assert.Single(results).Succeeded);
    }
}
