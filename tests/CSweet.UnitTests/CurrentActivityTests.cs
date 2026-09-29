using System.Text.Json;
using CSweet.Application.Security;
using CSweet.Application.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CSweet.UnitTests;

public sealed class CurrentActivityTests
{
    [Fact]
    public async Task CurrentExecutorWinsOverDirectAssignmentAndRowsAreNotDuplicated()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        f.Ticket.AssignedEmployeeId = Guid.NewGuid();
        await f.Db.SaveChangesAsync();
        var page = await f.Service.ReadAsync(f.Org, f.User);
        var row = Assert.Single(page.Items);
        Assert.Equal(f.Agent.Id, row.EmployeeId);
        Assert.Equal(f.Attempt.Id, row.AttemptId);
        Assert.Equal(f.Ticket.Id, row.WorkItemId);
        Assert.Equal("Executing", row.State);
        Assert.True(row.CanInspect);
    }

    [Fact]
    public async Task BoardAndDiagnosticPermissionsAreIndependentAndRechecked()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        f.Grants.Allow = false;
        Assert.Empty((await f.Service.ReadAsync(f.Org, f.User)).Items);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, f.Ticket.Id, 0, default));
        f.Grants.Allow = true;
        f.Owner.PermissionLevel = OrganizationPermissionLevel.Contributor;
        await f.Db.SaveChangesAsync();
        var row = Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items);
        Assert.False(row.CanInspect); Assert.Empty(row.PreviousSteps); Assert.Null(row.Provider);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, f.Ticket.Id, 0, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ReadAsync(Guid.NewGuid(), f.User));
    }

    [Fact]
    public async Task FeedUsesExactAttemptAndTaskNeverLatestCallForEmployee()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var ours = await f.RunAsync(f.Attempt.Id, f.Ticket.Id);
        var otherTask = await f.RunAsync(f.Attempt.Id, Guid.NewGuid());
        var previousAttempt = await f.RunAsync(Guid.NewGuid(), f.Ticket.Id);
        await f.ChunkAsync(ours, 0, "Visible reasoning");
        await f.ChunkAsync(otherTask, 0, "Different task must not leak");
        await f.ChunkAsync(previousAttempt, 0, "Earlier attempt must not leak");
        var page = await f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, f.Ticket.Id, 0, default);
        Assert.Equal("Visible reasoning", Assert.Single(page.Entries).Text);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, null, 0, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, Guid.NewGuid(), 0, default));
        var empty = await f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, f.Ticket.Id, page.NextSequence, default);
        Assert.Empty(empty.Entries);
        await f.ChunkAsync(ours, 1, "Next fragment");
        var next = await f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, f.Ticket.Id, page.NextSequence, default);
        Assert.Equal("Next fragment", Assert.Single(next.Entries).Text);
        Assert.Equal(page.Entries[0].Key, next.Entries[0].Key);
        Assert.Equal(1, next.Entries[0].StreamSequence);
    }

    [Fact]
    public async Task FeedRedactsSecretsAndDoesNotExposeProtectedReasoningOrDuplicateText()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var run = await f.RunAsync(f.Attempt.Id, f.Ticket.Id);
        await f.Writer.AppendAsync(new("model.response.chunk", "Model", OrganizationId: f.Org, EntityType: "AgentRunLog", EntityId: run,
            ContentType: "application/json", Payload: JsonSerializer.SerializeToUtf8Bytes(new
            {
                sequence = 0, text = "Output once", contents = new object[]
                {
                    new { kind = "text", text = "Output once" },
                    new { kind = "reasoning", text = "Check validation; password=secret123", protectedData = "private-thought" },
                    new { kind = "function_call", name = "validate", callId = "call-1" },
                    new { kind = "function_result", callId = "call-1", result = new { apiKey = "abc-private", success = true } }
                }
            })));
        var page = await f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, f.Ticket.Id, 0, default);
        var json = JsonSerializer.Serialize(page);
        Assert.DoesNotContain("secret123", json); Assert.DoesNotContain("private-thought", json); Assert.DoesNotContain("abc-private", json);
        Assert.Equal("Output once", Assert.Single(page.Entries, x => x.Kind == "Output").Text);
        Assert.Contains(page.Entries, x => x.Label == "Tool requested");
        Assert.Contains(page.Entries, x => x.Label == "Tool result");
    }

    [Fact]
    public async Task InitialFeedIsBoundedAndLateRecordedChunksKeepTheirSourceSequence()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var run = await f.RunAsync(f.Attempt.Id, f.Ticket.Id);
        for (var i = 0; i < 152; i++) await f.ChunkAsync(run, i, $"fragment {i}");
        var page = await f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, f.Ticket.Id, 0, default);
        Assert.Equal(150, page.Entries.Count); Assert.True(page.EarlierEntriesOmitted); Assert.False(page.HasMore);
        await f.ChunkAsync(run, 1, "Late fragment");
        var next = await f.Service.FeedAsync(f.Org, f.User, f.Agent.Id, f.Attempt.Id, f.Ticket.Id, page.NextSequence, default);
        Assert.Equal(1, Assert.Single(next.Entries).StreamSequence);
    }

    [Fact]
    public async Task LeaseExpiryAndWaitingAreNotReportedAsExecuting()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        f.Attempt.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await f.Db.SaveChangesAsync();
        Assert.Equal("Recovering", Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items).State);
        f.Ticket.Status = WorkTaskStatus.WaitingForApproval;
        await f.Db.SaveChangesAsync();
        Assert.Equal("Waiting", Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items).State);
    }

    [Fact]
    public async Task BackgroundWorkIsDiscoverableWithoutPretendingItBelongsToAProject()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        f.Db.AgentWorkItems.Add(new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString(),
            AgentInstallationId = f.Agent.AgentInstallationId!.Value, Name = "Background review", Status = AgentWorkStatus.Pending });
        await f.Db.SaveChangesAsync();
        var page = await f.Service.ReadAsync(f.Org, f.User);
        Assert.Equal(2, page.Total);
        Assert.Contains(page.Items, x => x.Category == "Background" && x.State == "Waiting");
        Assert.Single((await f.Service.ReadAsync(f.Org, f.User, projectsOnly: true)).Items);
    }

    [Fact]
    public async Task ModelCallsOutsideInboxHaveTheirOwnAuthorizedFeed()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var run = new AgentRunLog { Id = Guid.NewGuid(), OrganizationId = f.Org, AgentInstallationId = f.Agent.AgentInstallationId,
            EmployeeId = f.Agent.Id, StartedAt = DateTimeOffset.UtcNow, Status = "Running", Model = "background-model" };
        f.Db.Add(run); await f.Db.SaveChangesAsync();
        await f.ChunkAsync(run.Id, 0, "Background explanation");
        var row = Assert.Single((await f.Service.ReadAsync(f.Org, f.User)).Items, x => x.ModelRunId == run.Id);
        Assert.Equal("Background", row.Category); Assert.Null(row.WorkItemId);
        var page = await f.Service.ModelFeedAsync(f.Org, f.User, f.Agent.Id, run.Id, 0, default);
        Assert.Equal("Background explanation", Assert.Single(page.Entries).Text);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ModelFeedAsync(Guid.NewGuid(), f.User, f.Agent.Id, run.Id, 0, default));
    }

    [Fact]
    public async Task WorkPaginationDoesNotDropOtherEmployeesOrProjects()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        for (var i = 0; i < 55; i++) f.Db.AgentWorkItems.Add(new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString(),
            AgentInstallationId = f.Agent.AgentInstallationId!.Value, Status = AgentWorkStatus.Pending, Name = $"Background {i}", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(i) });
        await f.Db.SaveChangesAsync();
        var first = await f.Service.ReadAsync(f.Org, f.User);
        var second = await f.Service.ReadAsync(f.Org, f.User, first.NextOffset!.Value);
        Assert.Equal(56, first.Total); Assert.Equal(50, first.Items.Count); Assert.Equal(6, second.Items.Count); Assert.Null(second.NextOffset);
        Assert.Equal(56, first.Items.Concat(second.Items).Select(x => x.Key).Distinct().Count());
        Assert.Equal("Project", first.Items[0].Category);
    }

    private sealed class Grants : IScopedActionAuthorizationService
    {
        public bool Allow { get; set; } = true;
        public Task<ScopedAuthorizationDecision> AuthorizeAsync(Guid org, GrantSubjectKind subjectKind, Guid subject,
            string action, GrantScopeKind scope, Guid? id, CancellationToken token = default) => Task.FromResult(new ScopedAuthorizationDecision(Allow, action));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Guid Org { get; } = Guid.NewGuid();
        public Guid User { get; } = Guid.NewGuid();
        public CSweetDbContext Db { get; }
        public Grants Grants { get; } = new();
        public CurrentActivityService Service { get; }
        public AuditEventWriter Writer { get; }
        public OrganizationUser Owner { get; private set; } = null!;
        public OrganizationUser Agent { get; private set; } = null!;
        public WorkTask Ticket { get; private set; } = null!;
        public AgentWorkAttempt Attempt { get; private set; } = null!;
        private readonly ServiceProvider services;
        public Fixture()
        {
            var protection = new EphemeralDataProtectionProvider();
            var options = new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
            Db = new(options, protection);
            services = new ServiceCollection().AddScoped(_ => new CSweetDbContext(options, protection)).BuildServiceProvider();
            Writer = new(services.GetRequiredService<IServiceScopeFactory>(), new AuditExecutionContextAccessor(), protection);
            Service = new(Db, Grants, new(Db, protection), TimeProvider.System);
        }
        public async Task SeedAsync()
        {
            var now = DateTimeOffset.UtcNow;
            Owner = new() { Id = Guid.NewGuid(), OrganizationId = Org, ApplicationUserId = User, EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner };
            Agent = new() { Id = Guid.NewGuid(), OrganizationId = Org, AgentInstallationId = Guid.NewGuid(), DisplayName = "Test engineer", EmployeeType = EmployeeType.Agent };
            var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = Org, WorkstreamId = Guid.NewGuid(), Name = "Project board" };
            Ticket = new() { Id = Guid.NewGuid(), OrganizationId = Org, BoardId = board.Id, Title = "Implement keyboard navigation", Status = WorkTaskStatus.Running,
                AssignedEmployeeId = Agent.Id, AssignedAgentInstallationId = Agent.AgentInstallationId, ClaimEventId = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now };
            Db.AddRange(Owner, Agent, board, Ticket); await Db.SaveChangesAsync();
            var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = Org.ToString(), AgentInstallationId = Agent.AgentInstallationId.Value,
                Kind = AgentWorkKind.Event, SourceId = Ticket.ClaimEventId.ToString(), Status = AgentWorkStatus.Leased, AttemptCount = 1, CreatedAt = now };
            Db.Add(work); await Db.SaveChangesAsync();
            Attempt = new() { Id = Guid.NewGuid(), AgentWorkItemId = work.Id, Attempt = 1, ClaimedAt = now, LeaseExpiresAt = now.AddMinutes(3) };
            Db.Add(Attempt); await Db.SaveChangesAsync();
        }
        public async Task<Guid> RunAsync(Guid attempt, Guid? task)
        {
            var run = new AgentRunLog { Id = Guid.NewGuid(), OrganizationId = Org, EmployeeId = Agent.Id, AgentInstallationId = Agent.AgentInstallationId,
                AgentWorkAttemptId = attempt, WorkItemId = task, StartedAt = DateTimeOffset.UtcNow, Status = "Running" };
            Db.Add(run); await Db.SaveChangesAsync(); return run.Id;
        }
        public Task<Guid> ChunkAsync(Guid run, long sequence, string text) => Writer.AppendAsync(new("model.response.chunk", "Model", OrganizationId: Org,
            EntityType: "AgentRunLog", EntityId: run, ContentType: "application/json", Payload: JsonSerializer.SerializeToUtf8Bytes(new { sequence, contents = new[] { new { kind = "reasoning", text } } })));
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await services.DisposeAsync(); }
    }
}
