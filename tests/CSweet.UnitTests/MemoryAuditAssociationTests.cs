using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class MemoryAuditAssociationTests
{
    [Fact]
    public void Current_primary_links_and_legacy_null_metadata_are_recognized()
    {
        var work = Guid.NewGuid(); var conversation = Guid.NewGuid(); var employee = Guid.NewGuid();
        Assert.True(MemoryAuditAssociations.Matches(null, work, conversation, employee));
        Assert.True(MemoryAuditAssociations.Matches(JsonSerializer.Serialize(new
        {
            activityWorkItemId = work, activityConversationId = conversation, activityPerspectiveEmployeeId = employee
        }), work, conversation, employee));
        Assert.True(MemoryAuditAssociations.Matches(JsonSerializer.Serialize(new
        {
            activityWorkItemId = (Guid?)null, activityConversationId = conversation, activityPerspectiveEmployeeId = employee
        }), null, conversation, employee));
    }

    [Theory]
    [InlineData("work")]
    [InlineData("conversation")]
    [InlineData("perspective")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("invalid-guid")]
    [InlineData("object")]
    [InlineData("array")]
    [InlineData("null")]
    [InlineData("malformed")]
    public void Unclassified_or_misattributed_metadata_requires_review(string changed)
    {
        var conversation = Guid.NewGuid(); var employee = Guid.NewGuid();
        var fields = new Dictionary<string, object?>
        {
            ["activityWorkItemId"] = null, ["activityConversationId"] = conversation,
            ["activityPerspectiveEmployeeId"] = employee
        };
        if (changed == "work") fields["activityWorkItemId"] = Guid.NewGuid();
        if (changed == "conversation") fields["activityConversationId"] = Guid.NewGuid();
        if (changed == "perspective") fields["activityPerspectiveEmployeeId"] = Guid.NewGuid();
        if (changed == "missing") fields.Remove("activityWorkItemId");
        if (changed == "extra") fields["details"] = "private recalled text";
        if (changed == "invalid-guid") fields["activityConversationId"] = "private recalled text";
        if (changed == "object") fields["activityWorkItemId"] = new { details = "private recalled text" };
        var metadata = JsonSerializer.Serialize(fields);
        if (changed == "duplicate") metadata = metadata[..^1] + ",\"activityWorkItemId\":null}";
        if (changed == "array") metadata = "[]";
        if (changed == "null") metadata = "null";
        if (changed == "malformed") metadata = "{";
        Assert.False(MemoryAuditAssociations.Matches(metadata, null, conversation, employee));
    }
}

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresTheory]
    [InlineData("work")]
    [InlineData("conversation")]
    [InlineData("perspective")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    public async Task ErasureRejectsChangedActivityAssociationsEvenWithContentFreePayload(string changed)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, turn, _) = await SeedErasureWorkflowAsync(fixture);
        await using var db = fixture.Context();
        var source = await db.AuditOutbox.AsNoTracking().FirstAsync(x => x.SourceEntityId == turn.Id);
        var original = JsonSerializer.Deserialize<AuditEventWriteRequest>(source.RequestJson)!;
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(original.MetadataJson!)!;
        if (changed is "work" or "conversation" or "perspective")
            fields[changed switch { "work" => "activityWorkItemId", "conversation" => "activityConversationId", _ => "activityPerspectiveEmployeeId" }] = JsonSerializer.SerializeToElement(Guid.NewGuid());
        if (changed == "extra") fields["details"] = JsonSerializer.SerializeToElement("private recalled text");
        var metadata = JsonSerializer.Serialize(fields);
        if (changed == "duplicate") metadata = metadata[..^1] + ",\"activityWorkItemId\":null}";
        db.QueueAudit(original with { EventId = Guid.NewGuid(), MetadataJson = metadata });
        await db.SaveChangesAsync();
        var preview = await ErasureService(fixture, db).GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor);
        Assert.Equal("memory_erasure_diagnostics_review_required", preview.ApplyBlockedReason);
        Assert.Null(preview.EvidenceToken); Assert.Empty(await db.MemoryErasureReceipts.ToListAsync());
    }
}
