using System.Text;
using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Compute;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;

namespace CSweet.UnitTests;

public sealed partial class EmployeeAuditTimelineTests
{
    [Theory]
    [InlineData("receipt")]
    [InlineData("native-inputs")]
    [InlineData("legacy-chat")]
    [InlineData("broker")]
    [InlineData("retained-chat")]
    [InlineData("legacy-runtime")]
    [InlineData("missing-runtime")]
    [InlineData("historical")]
    [InlineData("attempt-bound")]
    public async Task MemoryWorkAuditOmitsInputsOutputsErrorsAndArbitraryMetadata(string evidence)
    {
        await using var f = new Fixture();
        const string secret = "private-memory-never-copy";
        var protector = f.Db.AuditProtection!.CreateProtector("CSweet.AgentWorkInbox.v1");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { text = secret });
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString("D"), AgentInstallationId = Guid.NewGuid(),
            Name = secret, LastError = secret, CorrelationId = secret, CausationId = secret, SourceType = "ordinary", SourceId = secret,
            IdempotencyKey = secret, ProtectedPayload = protector.Protect(bytes), ProtectedResult = protector.Protect(bytes),
            PayloadHash = new string('a', 64), ResultHash = new string('b', 64), AttemptCount = evidence == "historical" ? 0 : 1 };
        if (evidence == "receipt") work.MemoryRecallReceiptJson = "{\"malformed\":\"" + secret + "\"}"; // Still withhold.
        if (evidence == "native-inputs") work.NativeWorkInputReceiptJson = "{\"malformed\":\"" + secret + "\"}";
        if (evidence == "legacy-chat") work.SourceType = "chat-turn";
        f.Db.AgentWorkItems.Add(work);
        var runtime = new AgentRuntimeInstance { Id = Guid.NewGuid(), AgentInstallationId = work.AgentInstallationId,
            MemoryReadEvidenceVersion = evidence == "legacy-runtime" ? 0 : AgentRuntimeInstance.CurrentMemoryReadEvidenceVersion };
        if (evidence != "missing-runtime") f.Db.AgentRuntimeInstances.Add(runtime);
        var attempt = new AgentWorkAttempt { Id = Guid.NewGuid(), AgentWorkItemId = work.Id, RuntimeInstanceId = runtime.Id,
            Error = secret, Attempt = 1, ClaimedAt = DateTimeOffset.UtcNow, CompletionHash = new string('c', 64) };
        f.Db.AgentWorkAttempts.Add(attempt);
        if (evidence == "attempt-bound")
            for (var i = 0; i < 128; i++) f.Db.AgentWorkAttempts.Add(new() { Id = Guid.NewGuid(), AgentWorkItemId = work.Id, RuntimeInstanceId = runtime.Id });
        if (evidence == "broker") f.Db.AgentMemoryReadReceipts.Add(new() { Id = Guid.NewGuid(), RuntimeId = runtime.Id,
            WorkId = Guid.NewGuid(), OrganizationId = f.Org, EvidenceJson = secret });
        if (evidence == "retained-chat")
        {
            var earlier = new AgentWorkItem { Id = Guid.NewGuid(), SourceType = "chat-turn", OrganizationId = work.OrganizationId,
                AgentInstallationId = work.AgentInstallationId };
            f.Db.AgentWorkItems.Add(earlier);
            f.Db.AgentWorkAttempts.Add(new() { Id = Guid.NewGuid(), AgentWorkItemId = earlier.Id, RuntimeInstanceId = runtime.Id });
        }
        var progress = new AgentWorkProgress { Id = Guid.NewGuid(), AgentWorkItemId = work.Id, AgentWorkAttemptId = attempt.Id,
            ProtectedValue = protector.Protect(bytes), SizeBytes = bytes.Length, Sequence = 4, OccurredAt = DateTimeOffset.UtcNow };
        f.Db.AgentWorkProgress.Add(progress);
        foreach (var entity in new object[] { work, attempt, progress })
        {
            var request = f.Db.DescribeAuditSource(f.Db.Entry(entity), historical: evidence == "historical")!;
            Assert.NotNull(request);
            var payload = Encoding.UTF8.GetString(request.Payload!.Value.Span);
            Assert.DoesNotContain(secret, payload + request.Summary + request.CorrelationId + request.MetadataJson);
            Assert.Contains("memory-content-omitted-v1", payload);
            Assert.Contains(entity switch { AgentWorkItem x => x.Id.ToString(), AgentWorkAttempt x => x.Id.ToString(), AgentWorkProgress x => x.Id.ToString(), _ => "" }, payload);
        }
    }

    [Fact]
    public async Task MemoryWorkAuditPersistsContentFreeEvidenceWithValidLedgerIntegrity()
    {
        await using var f = new Fixture();
        const string secret = "private-memory-omit-from-ledger";
        var protector = f.Db.AuditProtection!.CreateProtector("CSweet.AgentWorkInbox.v1");
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString("D"), AgentInstallationId = Guid.NewGuid(),
            Name = secret, LastError = secret, CorrelationId = secret, MemoryRecallReceiptJson = "{}",
            ProtectedPayload = protector.Protect(JsonSerializer.SerializeToUtf8Bytes(new { text = secret })), PayloadHash = new string('a', 64) };
        f.Db.Add(work); await f.Db.SaveChangesAsync();
        await new AuditOutboxDispatcher(f.Db, f.Writer, TimeProvider.System).DispatchAsync(default);
        var item = Assert.Single((await f.Reader.BrowseAsync(f.Org, new())).Items);
        var detail = (await f.Reader.GetAsync(f.Org, item.Id))!;
        Assert.Equal("Verified", detail.IntegrityStatus);
        Assert.Contains("memory-content-omitted-v1", detail.PayloadContent);
        Assert.DoesNotContain(secret, detail.PayloadContent + detail.Summary);
        // The audit decision does not silently alter the authorized operational work item.
        Assert.Contains(secret, Encoding.UTF8.GetString(protector.Unprotect(work.ProtectedPayload)));
    }

    [Fact]
    public async Task MemoryWorkAuditKeepsOrdinaryUndeliveredWorkDiagnosticsAvailable()
    {
        await using var f = new Fixture();
        var protector = f.Db.AuditProtection!.CreateProtector("CSweet.AgentWorkInbox.v1");
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString("D"), AgentInstallationId = Guid.NewGuid(),
            Name = "setup", ProtectedPayload = protector.Protect(JsonSerializer.SerializeToUtf8Bytes(new { text = "ordinary diagnostic" })) };
        f.Db.Add(work);
        var request = f.Db.DescribeAuditSource(f.Db.Entry(work))!;
        Assert.Contains("ordinary diagnostic", Encoding.UTF8.GetString(request.Payload!.Value.Span));
    }
}
