using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;
public sealed partial class CurrentActivityTests
{
    [Fact]
    public async Task LinkedTrackingRecordsShareAnActivityWithoutHidingExecutingWorkOrUnrelatedWork()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var commission = Guid.NewGuid();
        f.Ticket.PersonalWorkContextJson = JsonSerializer.Serialize(new Wire.PersonalTodoWorkContext(IssueCommissionId: commission), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var tracking = new WorkTask { Id = Guid.NewGuid(), OrganizationId = f.Org, BoardId = f.Ticket.BoardId,
            Title = "Obtain scoped advice", Status = WorkTaskStatus.Blocked, AssignedEmployeeId = f.Agent.Id,
            AssignedAgentInstallationId = f.Agent.AgentInstallationId, PersonalWorkContextJson = f.Ticket.PersonalWorkContextJson };
        var independent = new WorkTask { Id = Guid.NewGuid(), OrganizationId = f.Org, BoardId = f.Ticket.BoardId,
            Title = "Independent scaffold", Status = WorkTaskStatus.Blocked, AssignedEmployeeId = f.Agent.Id, AssignedAgentInstallationId = f.Agent.AgentInstallationId };
        f.Db.AddRange(tracking, independent); await f.Db.SaveChangesAsync();
        var page = await f.Service.ReadAsync(f.Org, f.User);
        Assert.Equal(2, page.Total);
        var combined = Assert.Single(page.Items, r => r.WorkItemId == f.Ticket.Id);
        Assert.Equal(tracking.Id, Assert.Single(combined.RelatedWork).WorkItemId);
        Assert.Contains(page.Items, r => r.WorkItemId == independent.Id);
        f.Grants.Allow = false; Assert.Empty((await f.Service.ReadAsync(f.Org, f.User)).Items);
    }
}
