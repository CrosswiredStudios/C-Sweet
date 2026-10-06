using System.Text.Json;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class WorkItemDiscussionTests
{
    [Theory]
    [InlineData("@Victor Lin: Please clarify.", true)]
    [InlineData("Email me at hello@Victor Lin: please", false)]
    [InlineData("@Victor Lincoln: Please clarify.", false)]
    [InlineData("@@Victor Lin: please", false)]
    public void MentionsRequireAnExactDelimitedName(string body, bool expected) =>
        Assert.Equal(expected, WorkItemDiscussion.Mentions(body, "Victor Lin"));

    [Theory]
    [InlineData(GrantScopeKind.Board)]
    [InlineData(GrantScopeKind.Team)]
    [InlineData(GrantScopeKind.WorkItem)]
    public async Task DiscussionIsAtomicTargetedIdempotentAndGrantGoverned(GrantScopeKind scope)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var org = Guid.NewGuid(); var team = Guid.NewGuid(); var author = Guid.NewGuid();
        var reviewer = Guid.NewGuid(); var manager = Guid.NewGuid(); var denied = Guid.NewGuid();
        var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = org, Name = "Delivery", TeamId = team };
        var item = new WorkTask { Id = Guid.NewGuid(), OrganizationId = org, BoardId = board.Id, Title = "Ticket" };
        db.WorkBoards.Add(board); db.CoreWorkTasks.Add(item);
        foreach (var (installation, name) in new[] { (reviewer, "Victor Lin"), (manager, "Gabriel Reyes"), (denied, "No Access"), (author, "Daniel Kim") })
        {
            var person = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org, DisplayName = name,
                AgentInstallationId = installation, EmployeeType = EmployeeType.Agent, IsActive = true };
            db.CoreOrganizationUsers.Add(person);
            if (installation == manager) board.ManagerOrganizationUserId = person.Id;
            if (installation == denied) continue;
            foreach (var action in new[] { WorkItemActions.Read, WorkItemActions.ReadComments })
                db.ScopedActionGrants.Add(new ScopedActionGrant { Id = Guid.NewGuid(), OrganizationId = org,
                    SubjectKind = GrantSubjectKind.AgentInstallation, SubjectId = installation, Action = action,
                    ScopeKind = scope, ScopeId = scope == GrantScopeKind.Team ? team : scope == GrantScopeKind.WorkItem ? item.Id : board.Id });
        }
        await db.SaveChangesAsync();
        var comment = new WorkItemComment { Id = Guid.NewGuid(), OrganizationId = org, WorkItemId = item.Id,
            AuthorKind = GrantSubjectKind.AgentInstallation, AuthorSubjectId = author, AuthorDisplayName = "Daniel Kim",
            Body = "@Victor Lin: Which check failed? @No Access: please help.", IdempotencyKey = "question", Revision = 1 };
        db.WorkItemComments.Add(comment);
        await WorkItemDiscussion.QueueAsync(db, board.Id, comment, "comment.created", default);
        await WorkItemDiscussion.QueueAsync(db, board.Id, comment, "comment.created", default);
        Assert.Empty(await db.AgentPlatformEventOutbox.AsNoTracking().ToListAsync()); // Nothing committed early.
        await db.SaveChangesAsync();
        var events = await db.AgentPlatformEventOutbox.ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.DoesNotContain(events, e => e.TargetInstallationId == author || e.TargetInstallationId == denied);
        Assert.True(JsonDocument.Parse(events.Single(e => e.TargetInstallationId == reviewer).DataJson).RootElement.GetProperty("requiresResponse").GetBoolean());
        Assert.False(JsonDocument.Parse(events.Single(e => e.TargetInstallationId == manager).DataJson).RootElement.GetProperty("requiresResponse").GetBoolean());
        comment.Revision++; comment.Kind = "discussion.reply";
        await WorkItemDiscussion.QueueAsync(db, board.Id, comment, "comment.updated", default);
        await db.SaveChangesAsync();
        Assert.All((await db.AgentPlatformEventOutbox.ToListAsync()).Where(e => e.IdempotencyKey.Contains(":2:")),
            e => Assert.False(JsonDocument.Parse(e.DataJson).RootElement.GetProperty("requiresResponse").GetBoolean()));
        comment.Revision++; comment.DeletedAt = DateTimeOffset.UtcNow; comment.Kind = null;
        await WorkItemDiscussion.QueueAsync(db, board.Id, comment, "comment.deleted", default);
        await db.SaveChangesAsync();
        Assert.All((await db.AgentPlatformEventOutbox.ToListAsync()).Where(e => e.IdempotencyKey.Contains(":3:")),
            e => Assert.False(JsonDocument.Parse(e.DataJson).RootElement.GetProperty("requiresResponse").GetBoolean()));
    }
}
