using CSweet.Application.Communications;
using CSweet.Contracts.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.SourceControl;

public sealed partial class TaskDeliveryService
{
    public async Task<CSweet.Agent.SDK.TaskReviewResult> DecideFromChatAsync(Guid org, Guid installation,
        CSweet.Agent.SDK.DecideTaskReviewRequest request, CancellationToken ct)
    {
        var review = await db.TaskDeliveryReviews.SingleAsync(x => x.OrganizationId == org && x.Id == request.ReviewId, ct);
        if (review.DeveloperInstallationId != installation) throw new UnauthorizedAccessException("This task belongs to another developer.");
        var actor = await ChatManagerAsync(org, installation, request.SourceMessageId, ct, review.BoardId);
        var key = $"task-review-chat:{request.SourceMessageId:N}:{review.Id:N}";
        if (await db.WorkItemActivities.AnyAsync(x => x.OrganizationId == org && x.IdempotencyKey == key, ct))
            return Result(review, await TaskAsync(org, review.TaskId, ct));
        var error = await ApplyDecisionAsync(org, request.SourceMessageId, new(review.Id, request.ExpectedRevision), actor.Id, request.Choice, ct);
        if (error is not null) throw new InvalidOperationException(error);
        db.WorkItemActivities.Add(new() { Id = Guid.NewGuid(), OrganizationId = org, BoardId = review.BoardId,
            WorkItemId = review.TaskId, EventType = "task.merge.decided", Action = "task.merge.decide",
            ActorKind = CSweet.Domain.Security.GrantSubjectKind.OrganizationUser, ActorSubjectId = actor.Id,
            ActorDisplayName = actor.DisplayName, IdempotencyKey = key, OccurredAt = clock.GetUtcNow(),
            DataJson = System.Text.Json.JsonSerializer.Serialize(new { request.Choice, review.CommitSha, request.SourceMessageId }, Json) });
        await db.SaveChangesAsync(ct);
        return Result(review, await TaskAsync(org, review.TaskId, ct));
    }

    private async Task<OrganizationUser> ChatManagerAsync(Guid org, Guid installation, Guid messageId, CancellationToken ct, Guid? boardId = null)
    {
        var message = await db.CoreConversationMessages.AsNoTracking().Include(x => x.Conversation)
            .SingleOrDefaultAsync(x => x.Id == messageId && x.Conversation!.OrganizationId == org, ct)
            ?? throw new UnauthorizedAccessException("A retained manager instruction is required.");
        var developer = await db.CoreOrganizationUsers.AsNoTracking().SingleAsync(x => x.OrganizationId == org && x.AgentInstallationId == installation && x.IsActive && x.ArchivedAt == null, ct);
        var manager = await ManagerAsync(org, developer, ct, boardId);
        if (message.SenderOrganizationUserId != manager.Id || message.Conversation?.ArchivedAt is not null ||
            await db.CoreConversationMessages.AnyAsync(x => x.ConversationId == message.ConversationId && x.SenderOrganizationUserId == manager.Id && x.Sequence > message.Sequence, ct) ||
            !await db.ConversationParticipants.AnyAsync(x => x.ConversationId == message.ConversationId && x.OrganizationUserId == developer.Id && x.LeftAt == null, ct))
            throw new UnauthorizedAccessException("Only the current manager can approve this task in the developer's conversation.");
        return manager;
    }

    internal async Task<string?> ApplyDecisionAsync(Guid org, Guid decisionId, TaskMergeChoice choice,
        Guid managerId, string? option, CancellationToken ct)
    {
        await LockAsync(org, ct);
        var review = await db.TaskDeliveryReviews.SingleOrDefaultAsync(x => x.OrganizationId == org && x.Id == choice.ReviewId, ct);
        if (review is null || review.Revision != choice.Revision || review.Status is not ("AwaitingApproval" or "ManualReview"))
            return "This task changed. Review its current publication before deciding.";
        var developer = await db.CoreOrganizationUsers.AsNoTracking().SingleAsync(x => x.OrganizationId == org && x.AgentInstallationId == review.DeveloperInstallationId, ct);
        if ((await ManagerAsync(org, developer, ct, review.BoardId)).Id != managerId || review.ManagerOrganizationUserId != managerId)
            return "Only this task's current manager may approve its merge.";
        if (option is not ("task" or "story" or "epic" or "review")) return "Choose a merge scope or review the task first.";
        var currentTask = await TaskAsync(org, review.TaskId, ct);
        if (currentTask.Status != WorkTaskStatus.WaitingForApproval) return "The task is no longer awaiting review.";
        if (option == "review") review.Status = "ManualReview";
        else
        {
            if (option is "story" or "epic") await SetPreferenceAsync(org, option == "story" ? review.StoryId : review.EpicId,
                "Auto", managerId, null, $"merge-decision:{decisionId:N}", ct);
            review.ApprovedByOrganizationUserId = managerId;
            review.ApprovedCommitSha = review.CommitSha;
            review.Status = "AwaitingApproval";
        }
        review.Revision++; review.UpdatedAt = clock.GetUtcNow();
        Queue(review, review.DeveloperInstallationId, CSweet.Agent.SDK.TaskDeliveryCapabilities.Changed);
        return null; // The decision, preference and wake hint commit in the caller's transaction.
    }

    public async Task PresentDecisionAsync(TaskDeliveryReview review, ICommunicationHubService hub,
        IExecutiveDecisionService decisions, CancellationToken ct)
    {
        if (review.Status != "AwaitingApproval" || review.DecisionId is not null ||
            review.ApprovedCommitSha == review.CommitSha || await AutoApprovedAsync(review, ct)) return;
        var developer = await db.CoreOrganizationUsers.AsNoTracking().SingleAsync(x => x.OrganizationId == review.OrganizationId && x.AgentInstallationId == review.DeveloperInstallationId, ct);
        var manager = await ManagerAsync(review.OrganizationId, developer, ct, review.BoardId);
        var task = await TaskAsync(review.OrganizationId, review.TaskId, ct);
        var repo = await db.SourceControlRepositories.AsNoTracking().SingleAsync(x => x.Id == review.RepositoryId && x.OrganizationId == review.OrganizationId, ct);
        var story = await TaskAsync(review.OrganizationId, review.StoryId, ct);
        var epic = await TaskAsync(review.OrganizationId, review.EpicId, ct);
        var key = $"task-merge-question:{review.Id:N}:{review.Revision}";
        var chat = await hub.CreateAsync(review.OrganizationId, developer.Id,
            new(null, "Task code review", true, true, [manager.Id]), ct);
        if (!chat.Succeeded || chat.Chat is null) throw new InvalidOperationException(chat.Message);
        var sourceLink = $"/organizations/{review.OrganizationId:D}/source-control/{review.RepositoryId:D}?reference={Uri.EscapeDataString(review.CommitSha)}";
        var notes = review.Summary.StartsWith("Your review build is running:", StringComparison.Ordinal)
            ? review.Summary : review.Summary.Split('\n')[0];
        if (notes.Length > 1000) notes = notes[..1000];
        var text = $"I have finished {task.Title}. " + (review.QaInstallationId is null
            ? "There is no QA agent on this project team. Would you like to review the changes or approve a merge?"
            : "QA passed. Would you like to review the changes or approve a merge?") +
            $"\n\n{notes}\n\n[Review source]({sourceLink}) — {repo.Name}, revision {review.CommitSha[..Math.Min(12, review.CommitSha.Length)]}." +
            "\n\nYou can change the story or epic merge preference by messaging me at any time.";
        var sent = await hub.SendAsync(review.OrganizationId, chat.Chat.Id, developer.Id, new(text, key), ct)
            ?? throw new InvalidOperationException("The manager review message could not be sent.");
        var card = await decisions.CreateAsync(new(review.OrganizationId, chat.Chat.Id, null, sent.Message.Id,
            review.DeveloperInstallationId, $"Merge {task.Title} into {repo.DefaultBranch}?",
            [new("task", "Approve this merge", "Approve only this task's current published changes."),
             new("story", "Approve all merges for this story", $"Save automatic merge approval for {story.Title}."),
             new("epic", "Approve all merges for this epic", $"Save automatic merge approval for {epic.Title}."),
             new("review", "I want to review first", "Keep this task in Testing until I approve it.")],
            "task", key) { TaskMerge = new(review.Id, review.Revision) }, ct);
        review.DecisionId = card.Id;
        await db.SaveChangesAsync(ct);
    }
}
