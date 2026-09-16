using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Security;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Compute;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CSweet.UnitTests;

public sealed class EmployeeAuditTimelineTests
{
    [Fact]
    public async Task SingleLedgerEventAppearsForActorAndRecipientWithFullProtectedEvidence()
    {
        await using var f = new Fixture();
        var id = Guid.NewGuid(); var actor = Guid.NewGuid(); var recipient = Guid.NewGuid();
        var text = new string('x', 80000);
        var request = new AuditEventWriteRequest("communication.message.sent", OrganizationId: f.Org,
            EventId: id, ContentType: "application/json", Payload: Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { content = text, access_token = "never-display" })),
            Employees: [new(actor, "Actor"), new(recipient, "Recipient")]);
        await f.Writer.AppendAsync(request); await f.Writer.AppendAsync(request);
        Assert.Equal(1, await f.Db.AuditEvents.CountAsync());
        foreach (var employee in new[] { actor, recipient })
        {
            var page = await f.Reader.BrowseAsync(f.Org, new(EmployeeId: employee, OrderByOccurrence: true));
            Assert.Equal(id, Assert.Single(page.Items).Id);
        }
        var detail = (await f.Reader.GetAsync(f.Org, id))!;
        Assert.Contains(text, detail.PayloadContent);
        Assert.DoesNotContain("never-display", detail.PayloadContent);
        Assert.Equal("Verified", detail.IntegrityStatus);
        Assert.Equal("Available", detail.PayloadAvailability);
        Assert.True(detail.PayloadTruncated);
        Assert.DoesNotContain(text, Encoding.UTF8.GetString((await f.Db.AuditEventPayloads.SingleAsync()).ProtectedContent));
        Assert.Empty((await f.Reader.BrowseAsync(Guid.NewGuid(), new(EmployeeId: actor))).Items);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Writer.AppendAsync(request with { Employees = [new(Guid.NewGuid())] }));
    }

    [Fact]
    public async Task OccurrenceCursorHandlesTiesAndLateArrivalsWithoutRepeatingRows()
    {
        await using var f = new Fixture(); var employee = Guid.NewGuid(); var time = DateTimeOffset.UtcNow;
        for (var i = 0; i < 4; i++) await f.Writer.AppendAsync(new("test", OrganizationId: f.Org, OccurredAt: time.AddSeconds(i / 2), Employees: [new(employee)]));
        var first = await f.Reader.BrowseAsync(f.Org, new(Limit: 2, EmployeeId: employee, OrderByOccurrence: true));
        await f.Writer.AppendAsync(new("late", OrganizationId: f.Org, OccurredAt: time.AddHours(-1), Employees: [new(employee)]));
        var second = await f.Reader.BrowseAsync(f.Org, new(Cursor: first.NextCursor, Limit: 10, EmployeeId: employee, OrderByOccurrence: true));
        Assert.Equal(5, first.Items.Concat(second.Items).Select(x => x.Id).Distinct().Count());
        Assert.Equal("late", second.Items.Last().EventType);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Reader.BrowseAsync(f.Org, new(Cursor: "bad", OrderByOccurrence: true)));
    }

    [Fact]
    public async Task SourceChangesAndOutboxAreSavedTogetherAndLeaseRenewalsDoNotDuplicateEvents()
    {
        await using var f = new Fixture(); var install = Guid.NewGuid();
        var agent = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = f.Org, EmployeeType = EmployeeType.Agent, AgentInstallationId = install };
        f.Db.Add(agent); await f.Db.SaveChangesAsync();
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString(), AgentInstallationId = install, CreatedAt = DateTimeOffset.UtcNow, CorrelationId = "turn" };
        f.Db.Add(work); await f.Db.SaveChangesAsync();
        Assert.Single(await f.Db.AuditOutbox.ToListAsync());
        work.AvailableAt = DateTimeOffset.UtcNow; await f.Db.SaveChangesAsync();
        Assert.Single(await f.Db.AuditOutbox.ToListAsync());
        work.Status = AgentWorkStatus.Leased; work.AttemptCount = 1; await f.Db.SaveChangesAsync();
        Assert.Equal(2, await f.Db.AuditOutbox.CountAsync());
        await new AuditOutboxDispatcher(f.Db, f.Writer, TimeProvider.System).DispatchAsync(default);
        var page = await f.Reader.BrowseAsync(f.Org, new(EmployeeId: agent.Id));
        Assert.Equal(2, page.Items.Count);
        Assert.All(page.Items, x => Assert.Equal("Verified", x.IntegrityStatus));
    }

    [Fact]
    public async Task HistoricalImportIsResumableAndLabelsSnapshots()
    {
        await using var f = new Fixture();
        // Seed as pre-feature data by removing only pending delivery receipts before import.
        var agent = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = f.Org, EmployeeType = EmployeeType.Agent };
        f.Db.Add(agent);
        var ticket = new WorkTask { Id = Guid.NewGuid(), OrganizationId = f.Org, AssignedEmployeeId = agent.Id, Title = "Old ticket", CreatedAt = DateTimeOffset.UtcNow.AddDays(-120), UpdatedAt = DateTimeOffset.UtcNow.AddDays(-110) };
        f.Db.Add(ticket); await f.Db.SaveChangesAsync();
        f.Db.AuditOutbox.RemoveRange(await f.Db.AuditOutbox.ToListAsync()); await f.Db.SaveChangesAsync();
        var importer = new AuditHistoryImporter(f.Db);
        Assert.True(await importer.ImportBatchAsync(default) > 0);
        await new AuditOutboxDispatcher(f.Db, f.Writer, TimeProvider.System).DispatchAsync(default);
        Assert.Equal(0, await importer.ImportBatchAsync(default));
        var item = Assert.Single((await f.Reader.BrowseAsync(f.Org, new(EmployeeId: agent.Id))).Items);
        Assert.Equal("audit.source.snapshot", item.EventType);
        Assert.Contains("missing transitions", (await f.Reader.GetAsync(f.Org, item.Id))!.PayloadContent);
    }

    [Fact]
    public void DiagnosticAccessIsHumanHierarchyScopedAndRevokedImmediately()
    {
        var org = Guid.NewGuid();
        var owner = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org, EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner };
        var manager = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org, EmployeeType = EmployeeType.Human };
        var other = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org, EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Manager };
        var agent = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org, EmployeeType = EmployeeType.Agent, ReportsToOrganizationUserId = manager.Id };
        OrganizationUser[] people = [owner, manager, other, agent];
        Assert.True(EmployeeAuditAccess.CanRead(people, agent.Id, owner.Id));
        Assert.True(EmployeeAuditAccess.CanRead(people, agent.Id, manager.Id));
        Assert.False(EmployeeAuditAccess.CanRead(people, agent.Id, other.Id));
        Assert.False(EmployeeAuditAccess.CanRead(people, agent.Id, agent.Id));
        manager.IsActive = false; Assert.False(EmployeeAuditAccess.CanRead(people, agent.Id, manager.Id));
        manager.IsActive = true; manager.ReportsToOrganizationUserId = agent.Id;
        Assert.False(EmployeeAuditAccess.CanRead(people, agent.Id, manager.Id));
    }

    [Fact]
    public async Task SealedEvidenceAndAssociationsCannotBeUpdated()
    {
        await using var f = new Fixture();
        var id = await f.Writer.AppendAsync(new("test", OrganizationId: f.Org, Employees: [new(Guid.NewGuid())], Payload: Encoding.UTF8.GetBytes("hello"), ContentType: "text/plain"));
        var association = await f.Db.AuditEventEmployees.SingleAsync(); association.Role = "Recipient";
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Db.SaveChangesAsync());
        f.Db.ChangeTracker.Clear();
        var payload = await f.Db.AuditEventPayloads.SingleAsync(); payload.ProtectedContent = [1,2,3];
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Db.SaveChangesAsync());
        var row = await f.Db.AuditEvents.SingleAsync(); var original = AuditIntegrity.ComputeRecordHash(row);
        row.EmployeesJson = "[]"; Assert.NotEqual(original, AuditIntegrity.ComputeRecordHash(row));
    }

    [Fact]
    public async Task MessageVersionsRemainInspectableAndCurrentRecipientsAreCapturedAtomically()
    {
        await using var f = new Fixture();
        var sender = Guid.NewGuid(); var recipient = Guid.NewGuid(); var departed = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var chat = new Conversation { Id = Guid.NewGuid(), OrganizationId = f.Org, Kind = ConversationKind.Team, CreatedAt = now };
        var membership = new ConversationParticipant { Id = Guid.NewGuid(), ConversationId = chat.Id, OrganizationUserId = departed, JoinedAt = now.AddMinutes(-1) };
        f.Db.AddRange(chat, membership, new ConversationParticipant { Id = Guid.NewGuid(), ConversationId = chat.Id, OrganizationUserId = recipient, JoinedAt = now.AddMinutes(-1) });
        await f.Db.SaveChangesAsync();
        membership.LeftAt = now;
        var message = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = chat.Id, SenderOrganizationUserId = sender, Content = "Original", CreatedAt = now.AddSeconds(1), CorrelationId = Guid.NewGuid() };
        f.Db.Add(message); await f.Db.SaveChangesAsync();
        message.Content = "Edited"; await f.Db.SaveChangesAsync();
        message.Content = "Original"; await f.Db.SaveChangesAsync();
        message.Content = "Edited"; await f.Db.SaveChangesAsync();
        f.Db.Remove(message); await f.Db.SaveChangesAsync();
        await new AuditOutboxDispatcher(f.Db, f.Writer, TimeProvider.System).DispatchAsync(default);
        var messages = (await f.Reader.BrowseAsync(f.Org, new(EmployeeId: recipient))).Items.Where(x => x.EntityType == "ConversationMessage").ToList();
        Assert.Equal(5, messages.Count);
        Assert.All(messages, x => Assert.Equal("Recipient", x.EmployeeRole));
        Assert.DoesNotContain((await f.Reader.BrowseAsync(f.Org, new(EmployeeId: departed))).Items, x => x.EntityType == "ConversationMessage");
        var original = messages.Single(x => x.EventType == "communication.message.sent");
        Assert.Contains("Original", (await f.Reader.GetAsync(f.Org, original.Id))!.PayloadContent);
        Assert.Contains("Edited", (await f.Reader.GetAsync(f.Org, messages.First(x => x.EventType == "communication.message.edited").Id))!.PayloadContent);
    }

    [Fact]
    public void SharedRedactionProtectsNestedJsonAndPreservesUsageEvidence()
    {
        var raw = JsonSerializer.SerializeToUtf8Bytes(new { tokenInputCount = 42, detailsJson = "{\"apiKey\":\"never-display\"}", protectedData = "opaque-provider-data" });
        var safe = AuditPayloadSanitizer.Capture(raw, "application/json").FullContent!;
        Assert.Contains("42", safe);
        Assert.DoesNotContain("never-display", safe);
        Assert.DoesNotContain("opaque-provider-data", safe);
        Assert.Equal("Authorization: [REDACTED]", AuditPayloadSanitizer.RedactText("Authorization: Bearer abc.def"));
    }

    [Fact]
    public async Task HistoricalActivityDoesNotAttributePastWorkToTodaysAssignee()
    {
        await using var f = new Fixture();
        var agent = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = f.Org, EmployeeType = EmployeeType.Agent };
        var ticket = new WorkTask { Id = Guid.NewGuid(), OrganizationId = f.Org, AssignedEmployeeId = agent.Id, Title = "Reassigned ticket" };
        var activity = new CSweet.Domain.WorkManagement.WorkItemActivity { Id = Guid.NewGuid(), OrganizationId = f.Org, WorkItemId = ticket.Id, EventType = "work.changed", OccurredAt = DateTimeOffset.UtcNow.AddDays(-100) };
        f.Db.AddRange(agent, ticket, activity); await f.Db.SaveChangesAsync();
        f.Db.AuditOutbox.RemoveRange(await f.Db.AuditOutbox.ToListAsync()); await f.Db.SaveChangesAsync();
        await new AuditHistoryImporter(f.Db).ImportBatchAsync(default);
        await new AuditOutboxDispatcher(f.Db, f.Writer, TimeProvider.System).DispatchAsync(default);
        Assert.DoesNotContain((await f.Reader.BrowseAsync(f.Org, new(EmployeeId: agent.Id))).Items, x => x.EntityType == "WorkItemActivity");
        Assert.Contains((await f.Reader.BrowseAsync(f.Org, new())).Items, x => x.EntityType == "WorkItemActivity");
    }

    [Fact]
    public async Task ModelResponsesGroupBeforePagingAndAssembleOnlyThisRunAndEmployee()
    {
        await using var f = new Fixture();
        var employee = Guid.NewGuid(); var run = Guid.NewGuid(); var otherRun = Guid.NewGuid();
        var time = DateTimeOffset.UtcNow;
        async Task<Guid> Chunk(Guid id, int sequence, string text, Guid? who = null) => await f.Writer.AppendAsync(new(
            "model.response.chunk", "Model", Outcome: "Running", OrganizationId: f.Org, EntityType: "AgentRunLog", EntityId: id,
            OccurredAt: time.AddMilliseconds(sequence), CorrelationId: "shared-turn", Employees: [new(who ?? employee)],
            ContentType: "application/json", Payload: JsonSerializer.SerializeToUtf8Bytes(new { sequence, text,
                contents = new[] { new { kind = "text", text } }, inputTokens = sequence == 104 ? (int?)12 : null, outputTokens = sequence == 104 ? (int?)34 : null })));
        // Several raw pages, saved out of order, including empty role-only updates.
        for (var i = 104; i >= 0; i--) await Chunk(run, i, i == 0 ? "Hello " : i == 104 ? "world" : "");
        await Chunk(otherRun, 110, "Another call");
        await Chunk(run, 120, "Not this employee", Guid.NewGuid());
        var completed = await f.Writer.AppendAsync(new("model.call.completed", "Model", Outcome: "Completed", OrganizationId: f.Org,
            EntityType: "AgentRunLog", EntityId: run, OccurredAt: time.AddSeconds(1), Employees: [new(employee)]));
        var query = new SecurityEventQuery(Limit: 1, EmployeeId: employee, OrderByOccurrence: true, GroupModelResponses: true);
        var first = await f.Reader.BrowseAsync(f.Org, query);
        var item = Assert.Single(first.Items);
        Assert.Equal(completed, item.Id); Assert.Equal("model.response", item.EventType);
        Assert.Equal(105, item.ModelResponseChunkCount); Assert.Equal("Completed", item.Outcome);
        var second = await f.Reader.BrowseAsync(f.Org, query with { Cursor = first.NextCursor });
        Assert.Equal(otherRun, Assert.Single(second.Items).EntityId); Assert.Null(second.NextCursor);
        Assert.Single((await f.Reader.BrowseAsync(f.Org, query with { Outcome = "Completed" })).Items);
        var response = (await f.Reader.GetAsync(f.Org, completed, groupModelResponse: true, employeeId: employee))!.ModelResponse!;
        Assert.Equal("Hello world", response.Text); Assert.Equal(105, response.ChunkCount);
        Assert.Equal(103, response.EmptyChunkCount); Assert.Empty(response.Contents);
        Assert.Equal(12, response.InputTokens); Assert.Equal(34, response.OutputTokens);
        Assert.Equal("Verified", response.IntegrityStatus); Assert.False(response.Incomplete);
        Assert.Equal(100, response.Sources.Count);
        // The shared audit API still exposes every original chunk.
        var raw = await f.Reader.BrowseAsync(f.Org, query with { GroupModelResponses = false, Limit = 200 });
        Assert.Equal(107, raw.Items.Count); Assert.Null((await f.Reader.GetAsync(f.Org, completed))!.ModelResponse);
    }

    [Fact]
    public async Task ModelResponsePreservesStructuredContentAndReportsMissingEvidence()
    {
        await using var f = new Fixture(); var employee = Guid.NewGuid(); var run = Guid.NewGuid();
        var first = await f.Writer.AppendAsync(new("model.response.chunk", "Model", OrganizationId: f.Org,
            EntityType: "AgentRunLog", EntityId: run, Employees: [new(employee)], ContentType: "application/json",
            Payload: Encoding.UTF8.GetBytes("{\"sequence\":0,\"text\":\"\",\"contents\":[{\"kind\":\"reasoning\",\"text\":\"Recorded reasoning\"},{\"kind\":\"function_call\",\"name\":\"lookup\"}]}")));
        await f.Writer.AppendAsync(new("model.response.chunk", "Model", OrganizationId: f.Org, EntityType: "AgentRunLog", EntityId: run, Employees: [new(employee)]));
        var response = (await f.Reader.GetAsync(f.Org, first, groupModelResponse: true, employeeId: employee))!.ModelResponse!;
        Assert.Equal(2, response.Contents.Count); Assert.Contains("lookup", response.Contents[1].ToString());
        Assert.True(response.Incomplete); Assert.Equal(2, response.ChunkCount);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Guid Org { get; } = Guid.NewGuid();
        private readonly ServiceProvider _services;
        public CSweetDbContext Db { get; }
        public AuditEventWriter Writer { get; }
        public SecurityAuditService Reader { get; }
        public Fixture()
        {
            var options = new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
            var protection = new EphemeralDataProtectionProvider();
            _services = new ServiceCollection().AddScoped(_ => new CSweetDbContext(options, protection)).BuildServiceProvider();
            Db = new(options, protection);
            Writer = new(_services.GetRequiredService<IServiceScopeFactory>(), new AuditExecutionContextAccessor(), protection);
            Reader = new(Db, protection);
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await _services.DisposeAsync(); }
    }
}
