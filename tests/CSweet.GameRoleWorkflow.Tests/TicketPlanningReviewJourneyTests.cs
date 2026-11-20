using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.GameRoleWorkflow.Tests;

public sealed partial class FreshTeamSprintJourneyTests
{
    [Theory, Trait("Category", "AgentHarness")]
    [InlineData("recorded-scope")]
    [InlineData("clarify-scope")]
    [InlineData("ask-implementer")]
    public async Task Shatterpoint_planning_review_reads_the_ticket_and_routes_only_the_missing_fact(string scenario)
    {
        var model = new FirstSprintModel(false);
        await using var platform = new FreshTeamSprintPlatform();
        await platform.OpenAsync(model.Pitch);
        const string description = "Ship one playable Chromium layout from a clean checkout.";
        const string requirement = "Support mouse and keyboard paddle input.";
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "shatterpoint-ticket.json")));
        var recorded = fixture.RootElement;
        var criteria = recorded.GetProperty("acceptanceCriteriaAtSprintStart").Deserialize<string[]>()!;
        Assert.Equal(19, criteria.Length);
        if (scenario == "clarify-scope") criteria = [.. criteria, "Include the full review package in this increment."];
        var item = new WorkTask { Id = recorded.GetProperty("id").GetGuid(), BoardId = platform.Board, OrganizationId = platform.Organization,
            BoardColumnId = await platform.Db.WorkBoardColumns.Where(c => c.BoardId == platform.Board).Select(c => c.Id).FirstAsync(),
            Title = recorded.GetProperty("title").GetString()!, Description = description,
            Status = scenario == "clarify-scope" ? WorkTaskStatus.Ready : WorkTaskStatus.Running,
            AssignedEmployeeId = platform.Engineer.OrganizationUserId, AssignedAgentInstallationId = platform.Engineer.AgentInstallationId,
            PlanningSpecificationJson = JsonSerializer.Serialize(new WorkItemPlanningSpecification([requirement], criteria), JsonSerializerOptions.Web) };
        if (scenario == "recorded-scope")
        {
            item.ProposalCoordinationSessionId = Guid.NewGuid();
            item.ProposalArtifactDigest = FreshTeamSprintPlatform.Digest(item.Title);
            item.ProposalItemKey = "roadmap-epic-1.story-1.task-1";
        }
        if (scenario == "ask-implementer")
        {
            // The current assignee can be a reviewer. The implementation observation belongs to the author.
            item.AssignedEmployeeId = platform.Victor.OrganizationUserId;
            item.AssignedAgentInstallationId = platform.Victor.AgentInstallationId;
            item.StageAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = platform.Organization,
                BoardId = platform.Board, WorkItemId = item.Id, StageKey = "specialist-execution",
                PrincipalKind = CSweet.Domain.WorkManagement.WorkOrchestrationPrincipalKind.AgentInstallation,
                OrganizationUserId = platform.Engineer.OrganizationUserId, AgentInstallationId = platform.Engineer.AgentInstallationId });
        }
        platform.Db.CoreWorkTasks.Add(item);
        await platform.Db.SaveChangesAsync();
        var team = new PlanningTeam(platform, model);
        var recipient = scenario == "clarify-scope" ? platform.Victor : platform.Engineer;
        var question = scenario == "clarify-scope" ? "Which review-package criterion governs this increment?"
            : "Which input event did you observe being dropped on your local Chromium run?";
        const string response = "The observed result is documented here for Gabriel.";
        var reviews = 0;
        bool readReply = false;
        team.SetModel(platform.Gabriel, "ticket-review", (request, _) => {
            var messages = request.GetProperty("messages");
            if (!messages[0].GetProperty("text").GetString()!.Contains("nextCandidate"))
                return Task.FromResult<object>(new { text = JsonSerializer.Serialize(model.Answer(platform.Gabriel, request), JsonSerializerOptions.Web) });
            var data = JsonDocument.Parse(messages[1].GetProperty("text").GetString()!).RootElement;
            var evidence = data.GetProperty("evidence").EnumerateArray().ToArray();
            int index = Array.FindIndex(evidence, e => e.GetProperty("Id").GetGuid() == item.Id && e.GetProperty("Kind").GetString() == "item");
            Assert.True(index >= 0, "The review omitted the existing ticket.");
            Assert.True(evidence[index].TryGetProperty("Ticket", out var ticket), "Gabriel received a ticket summary without its scope or acceptance criteria.");
            Assert.Equal(description, ticket.GetProperty("Description").GetString());
            var planning = ticket.GetProperty("Planning");
            Assert.Contains(requirement, planning.GetProperty("Requirements").Deserialize<string[]>()!);
            Assert.Equal(criteria, planning.GetProperty("AcceptanceCriteria").Deserialize<string[]>());
            readReply = data.GetRawText().Contains(response);
            reviews++;
            var replayBadRequest = scenario == "recorded-scope" && reviews == 1;
            if (scenario == "recorded-scope" && reviews == 2)
                Assert.Contains(messages.EnumerateArray(), m => m.GetProperty("text").GetString()!.Contains("Use clarify-scope for conflicting intent"));
            var pending = Array.FindIndex(evidence, e => e.GetProperty("Kind").GetString() == "planning-question" && e.GetProperty("AwaitingResult").GetBoolean());
            bool waitingForVictor = scenario == "clarify-scope" && pending >= 0;
            var kind = replayBadRequest ? "consult" : scenario == "recorded-scope" || readReply || waitingForVictor ? "proceed" : scenario;
            return Task.FromResult<object>(new { text = JsonSerializer.Serialize(new { disposition = readReply ? "select" : "wait", nextCandidate = readReply ? 0 : (int?)null,
                nextGoal = "Hold for the current core slice.", draftAhead = false, keepDraft = true, revisitWhen = "When the core slice reports.",
                feelSummary = "The implementation is running.", actions = new[] { new { kind, capability = replayBadRequest ? "Engineer" : null,
                    rationale = replayBadRequest ? recorded.GetProperty("recordedPlanningQuestion").GetString() : kind == "proceed" ?
                    "Use the ticket and any requested answer." : question, evidenceIndexes = new[] { waitingForVictor ? pending : index } } } }) });
        });
        team.SetModel(recipient, "ticket-answer", (request, _) => {
            var messages = request.GetProperty("messages");
            if (messages[0].GetProperty("text").GetString()!.Contains("Answer the ticket question"))
            {
                var data = JsonDocument.Parse(messages[1].GetProperty("text").GetString()!).RootElement;
                Assert.Equal(item.Id, data.GetProperty("item").GetProperty("id").GetGuid());
                Assert.Contains(question, data.GetProperty("question").GetProperty("body").GetString());
                return Task.FromResult<object>(new { text = response });
            }
            return Task.FromResult<object>(new { text = JsonSerializer.Serialize(model.Answer(recipient, request), JsonSerializerOptions.Web) });
        });
        await team.StartHostsAsync();
        await team.PlanAsync(reachedWait: () => reviews > 0);
        if (scenario == "recorded-scope")
        {
            Assert.Equal(2, reviews);
            Assert.Empty(await platform.Db.WorkItemComments.Where(c => c.WorkItemId == item.Id).ToListAsync());
            return;
        }
        var sent = Assert.Single(await platform.Db.WorkItemComments.Where(c => c.WorkItemId == item.Id).ToListAsync());
        Assert.StartsWith($"@{recipient.DisplayName}:", sent.Body);
        var reviewsBeforeRestart = reviews;
        await team.StartHostsAsync();
        await team.PlanAsync(reachedWait: () => reviews > reviewsBeforeRestart);
        Assert.Equal(sent.Id, (await platform.Db.WorkItemComments.SingleAsync(c => c.WorkItemId == item.Id)).Id);
        var notifications = await platform.Db.AgentPlatformEventOutbox.Where(e => e.EventType == "com.csweet.work.item.discussion.changed.v1").ToListAsync();
        var wake = Assert.Single(notifications, e => JsonDocument.Parse(e.DataJson).RootElement.GetProperty("requiresResponse").GetBoolean());
        Assert.Equal(recipient.AgentInstallationId, wake.TargetInstallationId);
        await team.DeliverTicketQuestionAsync(recipient, wake.Id, wake.DataJson);
        await team.StartHostsAsync();
        await team.DeliverTicketQuestionAsync(recipient, wake.Id, wake.DataJson);
        var reply = Assert.Single(await platform.Db.WorkItemComments.Where(c => c.WorkItemId == item.Id && c.Kind == "discussion.reply").ToListAsync());
        Assert.Equal(recipient.AgentInstallationId, reply.AuthorSubjectId);
        Assert.Equal($"{sent.Id:D}:{sent.Revision}", reply.CausationId);
        await team.ReviewPlanningCardAsync();
        Assert.True(readReply);
        Assert.Equal(2, await platform.Db.WorkItemComments.CountAsync(c => c.WorkItemId == item.Id));
    }

    internal sealed partial class PlanningTeam
    {
        public async Task ReviewPlanningCardAsync()
        {
            var wake = queue.NextWake(c => c.CorrelationId?.StartsWith("producer-planning:") == true);
            var result = await hosts[platform.Gabriel.OrganizationUserId].DeliverEventAsync(PersonalTodoEvents.Available, wake, Guid.NewGuid());
            Assert.True(result.Succeeded, result.Error);
        }

        public async Task DeliverTicketQuestionAsync(AgentCoordinationParticipant actor, Guid id, string data)
        {
            var result = await hosts[actor.OrganizationUserId].DeliverEventAsync("com.csweet.work.item.discussion.changed.v1",
                JsonDocument.Parse(data).RootElement, id);
            Assert.True(result.Succeeded, result.Error);
        }
    }
}
