using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class DecisionContentTests
{
    internal static string IncidentText()
    {
        using var reader = new StreamReader(typeof(DecisionContentTests).Assembly.GetManifestResourceStream("prism-break-planning-decision.txt")!);
        return reader.ReadToEnd().Trim();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecisionSchemaBrokerPersistenceAndEventsRetainCompleteContent(bool large)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var installation = Guid.NewGuid(); var project = Guid.NewGuid();
        db.CoreOrganizationUsers.Add(new() { Id = actor, OrganizationId = org, AgentInstallationId = installation,
            DisplayName = "Planning lead", EmployeeType = EmployeeType.Agent, IsActive = true });
        db.Workstreams.Add(new() { Id = project, OrganizationId = org, Name = "Project", AccountableManagerOrganizationUserId = actor });
        db.WorkstreamAuthorityEnvelopes.Add(new() { Id = Guid.NewGuid(), OrganizationId = org, WorkstreamId = project,
            AgentAuthorizedActionKeysJson = "[\"work-planning\",\"gate:release:decide\"]" });
        var gate = new WorkstreamGateRecord { Id = Guid.NewGuid(), OrganizationId = org, WorkstreamId = project,
            Key = "release", Status = W.WorkstreamGateStatuses.Submitted, Revision = 1 };
        db.WorkstreamGates.Add(gate);
        await db.SaveChangesAsync();
        var text = IncidentText(); Assert.Equal(3790, text.Length);
        if (large) text = string.Concat(Enumerable.Repeat(text + "\n🎮", 20));
        var impact = large ? text : "Affected implementation waits for direction.";
        var request = new W.DecisionRequest(project, "project.direction", text, "work-planning",
            [new("proceed", "Proceed", text), new("revise", "Revise", null)], "proceed", [], null, impact, null, "planning-direction",
            large ? JsonSerializer.SerializeToElement(new { detail = text }) : null);
        var handler = new WorkstreamGovernanceCapabilityHandler(db, new TestAuditEventWriter(), new(db), TimeProvider.System);
        var session = new AgentSession("session", "planner", installation.ToString(), org.ToString(), "runtime", "tick",
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), new HashSet<string> {
                W.DecisionCapabilityNames.RequestV1, W.DecisionCapabilityNames.ReadV1, W.DecisionCapabilityNames.DecideV1,
                W.WorkstreamCapabilityNames.GateDecideV1 }, 1));
        async Task<JsonElement> Execute(string capability, object command)
        {
            var wire = JsonSerializer.SerializeToElement(command, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var schema = Assert.Single(new McpToolCatalog([]).List(new HashSet<string> { capability })).InputSchema;
            JsonSchemaValidator.Validate(wire, schema);
            var results = new List<CapabilityResult>();
            await foreach (var result in handler.HandleAsync(session, new() { RequestId = Guid.NewGuid().ToString(), Capability = capability,
                Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(wire)) }, default)) results.Add(result);
            var response = Assert.Single(results); Assert.True(response.Succeeded, response.Error);
            return JsonSerializer.Deserialize<JsonElement>(response.Payload.ToByteArray());
        }
        var first = await Execute(W.DecisionCapabilityNames.RequestV1, request);
        await Execute(W.DecisionCapabilityNames.RequestV1, request);
        db.ChangeTracker.Clear();
        var stored = Assert.Single(await db.WorkstreamDecisions.ToListAsync());
        Assert.Equal(text, stored.Summary); Assert.Equal(impact, stored.BlockingImpact);
        Assert.Equal(stored.Id, first.GetProperty("id").GetGuid());
        Assert.Single(await db.AgentPlatformEventOutbox.Where(x => x.EventType == W.WorkstreamEventNames.DecisionRequestedV1).ToListAsync());
        var read = await Execute(W.DecisionCapabilityNames.ReadV1, new W.ReadDecisionRequest(DecisionId: stored.Id));
        Assert.Equal(text, read[0].GetProperty("summary").GetString());
        Assert.Equal(actor, read[0].GetProperty("requestedByOrganizationUserId").GetGuid());
        Assert.Equal(JsonValueKind.Null, read[0].GetProperty("decidedByOrganizationUserId").ValueKind);
        var rationale = string.Concat(Enumerable.Repeat("Preserve the approved criterion and document the evidence. 🎮\n", 200));
        await Execute(W.DecisionCapabilityNames.DecideV1, new W.DecideDecisionRequest(stored.Id, stored.Revision, "proceed", rationale, "approve-direction"));
        db.ChangeTracker.Clear();
        stored = await db.WorkstreamDecisions.SingleAsync();
        Assert.Equal(rationale.Trim(), stored.Rationale); Assert.Equal("Decided", stored.Status);
        var decided = await Execute(W.DecisionCapabilityNames.ReadV1, new W.ReadDecisionRequest(DecisionId: stored.Id));
        Assert.Equal(actor, decided[0].GetProperty("decidedByOrganizationUserId").GetGuid());
        var notification = Assert.Single(await db.AgentPlatformEventOutbox.Where(x => x.EventType == W.WorkstreamEventNames.DecisionDecidedV1).ToListAsync());
        using var notificationJson = JsonDocument.Parse(notification.DataJson);
        Assert.Equal(rationale.Trim(), notificationJson.RootElement.GetProperty("metadata").GetProperty("rationale").GetString());
        await Execute(W.WorkstreamCapabilityNames.GateDecideV1, new { workstreamId = project, gateId = gate.Id,
            expectedRevision = 1, decision = "approved", rationale, findings = Array.Empty<object>(), idempotencyKey = "gate-decision" });
        db.ChangeTracker.Clear();
        Assert.Equal(rationale.Trim(), (await db.WorkstreamGates.SingleAsync()).DecisionRationale);
    }

    [Fact]
    public void AuthoredDecisionColumnsHaveNoConfiguredLengthCaps()
    {
        using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql("Host=localhost;Database=unused").Options);
        foreach (var (type, names) in new[] {
            (typeof(WorkstreamDecisionRecord), new[] { "Summary", "BlockingImpact", "Rationale" }),
            (typeof(WorkstreamGateRecord), new[] { "DecisionRationale" }),
            (typeof(ExecutiveDecision), new[] { "Prompt", "FreeTextAnswer" }),
            (typeof(StaffingCorrection), new[] { "Reason" }) })
        foreach (var name in names)
        {
            var property = db.Model.FindEntityType(type)!.FindProperty(name)!;
            Assert.Null(property.GetMaxLength()); Assert.Equal("text", property.GetColumnType());
        }
    }
}
