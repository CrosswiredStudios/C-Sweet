using CSweet.Domain.Analytics;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class ProjectHealthTests
{
    [Fact]
    public async Task RenewingLeaseCannotHideStalledAttemptAndOtherActiveWorkCannotMaskIt()
    {
        await using var f = await Fixture.Create();
        var task = f.Ticket(WorkTaskStatus.Running);
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString(),
            AgentInstallationId = f.Producer.AgentInstallationId!.Value, Name = "work.execution.run", Status = AgentWorkStatus.Leased,
            AttemptCount = 1, CreatedAt = f.Clock.Now, DeadlineAt = f.Clock.Now.AddHours(3) };
        var attempt = new AgentWorkAttempt { Id = Guid.NewGuid(), AgentWorkItemId = work.Id, Attempt = 1,
            ClaimedAt = f.Clock.Now, LeaseExpiresAt = f.Clock.Now.AddHours(3), LastConfirmedAt = f.Clock.Now };
        f.Db.AddRange(task, work, attempt, new WorkExecutionContext { Id = Guid.NewGuid(), OrganizationId = f.Org,
            AgentInstallationId = work.AgentInstallationId, AgentWorkItemId = work.Id, RootWorkItemId = task.Id },
            new AgentWorkProgress { Id = Guid.NewGuid(), AgentWorkAttemptId = attempt.Id, AgentWorkItemId = work.Id,
                Sequence = 1, OccurredAt = f.Clock.Now });
        await f.Db.SaveChangesAsync(); await f.Review();
        f.Clock.Now += TimeSpan.FromMinutes(16);
        attempt.LastConfirmedAt = f.Clock.Now;
        await f.Db.SaveChangesAsync(); await f.Review();
        var incident = Assert.Single(f.Db.ProjectIncidents);
        Assert.Equal(task.Id, incident.AffectedWorkItemId);
        Assert.Contains("stopped reporting progress", incident.Reason);
        Assert.Contains("renewed lease", incident.Facts);
        var deadline = incident.EscalateAt;
        f.Clock.Now += TimeSpan.FromMinutes(1); attempt.LastConfirmedAt = f.Clock.Now;
        await f.Db.SaveChangesAsync(); await f.Review();
        Assert.Single(f.Db.ProjectIncidents); Assert.Equal(deadline, incident.EscalateAt);
    }

    [Fact]
    public async Task RecordedProviderWaitIsNotMistakenForAnUnresponsiveWorkspaceCommand()
    {
        await using var f = await Fixture.Create();
        var task = f.Ticket(WorkTaskStatus.Running);
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString(),
            AgentInstallationId = f.Producer.AgentInstallationId!.Value, Name = "work.execution.run", Status = AgentWorkStatus.Leased,
            AttemptCount = 1, CreatedAt = f.Clock.Now, DeadlineAt = f.Clock.Now.AddHours(1) };
        var attempt = new AgentWorkAttempt { Id = Guid.NewGuid(), AgentWorkItemId = work.Id, Attempt = 1,
            ClaimedAt = f.Clock.Now, LeaseExpiresAt = f.Clock.Now.AddHours(1) };
        var model = new AgentRunLog { Id = Guid.NewGuid(), OrganizationId = f.Org, AgentInstallationId = work.AgentInstallationId,
            AgentWorkItemId = work.Id, AgentWorkAttemptId = attempt.Id, Status = "Generating", StartedAt = f.Clock.Now };
        f.Db.AddRange(task, work, attempt, model, new WorkExecutionContext { Id = Guid.NewGuid(), OrganizationId = f.Org,
            AgentInstallationId = work.AgentInstallationId, AgentWorkItemId = work.Id, RootWorkItemId = task.Id });
        await f.Db.SaveChangesAsync(); f.Clock.Now += TimeSpan.FromMinutes(16); await f.Review();
        Assert.Empty(f.Db.ProjectIncidents);
        model.Status = "Failed"; model.CompletedAt = f.Clock.Now;
        await f.Db.SaveChangesAsync(); f.Clock.Now += TimeSpan.FromMinutes(16); await f.Review();
        Assert.Single(f.Db.ProjectIncidents);
    }

    [Fact]
    public async Task UnresolvedEscalationReachesCeoWithoutModelReportOrInventedDecisionAndDeduplicates()
    {
        await using var f = await Fixture.Create(); var task = await f.FailTicket();
        var incident = Assert.Single(f.Db.ProjectIncidents);
        for (var hop = 0; hop < 3; hop++)
        {
            f.Clock.Now += TimeSpan.FromMinutes(16); await f.Review();
        }
        Assert.Equal(f.Ceo.Id, incident.CurrentRecipientId);
        Assert.Null(incident.OwnerReportJson);
        await f.Service.DeliverAsync(default); await f.Service.DeliverAsync(default);
        var message = Assert.Single(f.Db.CoreConversationMessages);
        Assert.Contains("no decision is requested", message.Content);
        Assert.Contains(task.Id.ToString(), message.Content);
        Assert.Equal("ProjectHealthStatus", Assert.Single(f.Db.UserNotifications).Category);
        Assert.Empty(f.Db.ExecutiveDecisions);
        // New evidence must not repeat the same informational escalation.
        f.Clock.Now += TimeSpan.FromMinutes(1); await f.Review(); await f.Service.DeliverAsync(default);
        Assert.Single(f.Db.CoreConversationMessages);
        task.Status = WorkTaskStatus.Completed; task.UpdatedAt = f.Clock.Now;
        await f.Db.SaveChangesAsync(); await f.Review(); await f.Service.DeliverAsync(default);
        Assert.Equal("Resolved", incident.Status);
        Assert.All(f.Db.UserNotifications.Where(x => x.Category == "ProjectHealthStatus"), x => Assert.NotNull(x.DismissedAt));
    }
}
