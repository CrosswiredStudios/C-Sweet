using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.Infrastructure.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

/// <summary>Retrieves a bounded historical tool snapshot from its existing protected request evidence.</summary>
public sealed class HarnessContextCapabilityHandler(CSweetDbContext db, IDataProtectionProvider protection) : IPlatformCapabilityHandler
{
    public const string Capability = "platform.harness.context.read";
    public bool CanHandle(string capability) => capability == Capability;
    public sealed record ReadRequest(Guid RunId, string CallId, int Offset = 0, int Limit = 8000);
    public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request,
        [EnumeratorCancellation] CancellationToken token)
    { yield return await ReadAsync(session, request, token); }

    private async Task<CapabilityResult> ReadAsync(AgentSession session, RequestCapability request, CancellationToken token)
    {
        try
        {
            var input = JsonSerializer.Deserialize<ReadRequest>(request.Payload.Span, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new ArgumentException("Missing context reference.");
            if (input.Offset < 0 || input.Limit is < 1 or > 16000 || string.IsNullOrWhiteSpace(input.CallId) || input.CallId.Length > 300) throw new ArgumentException("Use a bounded context range.");
            if (!session.Grant.RequestedCapabilities.Contains(Capability) || !Guid.TryParse(session.InstallationId, out var installation) ||
                !Guid.TryParse(session.RuntimeInstanceId, out var runtime) || !Guid.TryParse(session.TickId, out var tick) || InferenceExecutionAttribution.Current is not { } attribution)
                throw new UnauthorizedAccessException("Context retrieval requires the original authenticated work attempt.");
            var now = DateTimeOffset.UtcNow;
            if (!await db.AgentInstallations.AsNoTracking().AnyAsync(x => x.Id == installation && x.IsEnabled && x.BusinessId == session.BusinessId && x.Grant!.GrantRevision == session.Grant.Revision, token) ||
                !await db.AgentRuntimeInstances.AsNoTracking().AnyAsync(x => x.Id == runtime && x.TickId == tick && x.AgentInstallationId == installation && x.Status == AgentRuntimeStatus.Running && x.MemoryResetRequestedAt == null && (x.RuntimeDeadlineAt == null || x.RuntimeDeadlineAt > now), token))
                throw new UnauthorizedAccessException();
            var work = await db.AgentWorkItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == attribution.WorkId && x.AgentInstallationId == installation && x.OrganizationId == session.BusinessId && x.Status == AgentWorkStatus.Leased && x.DeadlineAt > now && x.MemoryErasedAt == null, token) ?? throw new UnauthorizedAccessException();
            var attempt = await db.AgentWorkAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.AgentWorkItemId == work.Id && x.Attempt == attribution.Attempt && x.RuntimeInstanceId == runtime && x.FinishedAt == null && x.LeaseExpiresAt > now, token) ?? throw new UnauthorizedAccessException();
            var run = await db.AgentRunLogs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.RunId && x.AgentInstallationId == installation && x.AgentWorkItemId == work.Id && x.AgentWorkAttemptId == attempt.Id && x.MemoryErasedAt == null, token) ?? throw new UnauthorizedAccessException();
            var employee = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.AgentInstallationId == installation && x.IsActive && x.ArchivedAt == null).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(token);
            if (db.Database.IsNpgsql())
            {
                await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, token, attribution.Attempt);
                await new MemoryRecallDispatchEvidence(db).AuthorizeWorkAsync(work, employee?.ToString("D"), token);
            }
            var evidenceId = CSweetDbContext.AuditSourceId($"source:AgentRunLog:{run.Id:D}");
            byte[] bytes;
            var pending = await db.AuditOutbox.AsNoTracking().SingleOrDefaultAsync(x => x.Id == evidenceId, token);
            if (pending?.ProtectedRequest is { Length: > 0 } encrypted)
            {
                var write = JsonSerializer.Deserialize<AuditEventWriteRequest>(protection.CreateProtector("CSweet.AuditOutbox.v1").Unprotect(encrypted)) ?? throw new InvalidOperationException("Context evidence is unavailable.");
                if (write.EventId != evidenceId || write.EntityId != run.Id) throw new CryptographicException();
                bytes = write.Payload?.ToArray() ?? throw new InvalidOperationException("Context evidence is unavailable.");
            }
            else
            {
                var audit = await db.AuditEvents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == evidenceId, token) ?? throw new InvalidOperationException("Context evidence is unavailable.");
                var payload = await db.AuditEventPayloads.AsNoTracking().SingleAsync(x => x.AuditEventId == audit.Id, token);
                bytes = protection.CreateProtector("CSweet.AuditPayload.v1").Unprotect(payload.ProtectedContent);
                if (Convert.ToHexString(SHA256.HashData(bytes)) != audit.EvidenceSha256 || audit.IntegrityVersion > 0 &&
                    (AuditIntegrity.ComputeRecordHash(audit) != audit.RecordHash || protection.CreateProtector("CSweet.SecurityAuditLedger.v1").Unprotect(audit.IntegritySeal!) != audit.RecordHash)) throw new CryptographicException();
            }
            using var json = JsonDocument.Parse(bytes);
            var evidence = json.RootElement.GetProperty("evidence");
            var scope = evidence.GetProperty("contextScope");
            if (scope.GetProperty("runtimeId").GetString() != session.RuntimeInstanceId || scope.GetProperty("tickId").GetString() != session.TickId) throw new UnauthorizedAccessException();
            var results = evidence.GetProperty("request").GetProperty("messages").EnumerateArray()
                .Where(x => x.TryGetProperty("contents", out var parts) && parts.ValueKind == JsonValueKind.Array)
                .SelectMany(x => x.GetProperty("contents").EnumerateArray()).Where(x =>
                    x.TryGetProperty("kind", out var kind) && kind.GetString() == "function_result" && x.TryGetProperty("callId", out var call) && call.GetString() == input.CallId).ToArray();
            if (results.Length != 1) throw new InvalidOperationException("The context result is unavailable or ambiguous.");
            var text = results[0].GetProperty("result").GetRawText();
            var offset = Math.Min(input.Offset, text.Length); var count = Math.Min(input.Limit, text.Length - offset);
            return new() { RequestId = request.RequestId, Succeeded = true, ContentType = "application/json", Payload = JsonPayload.From(new { input.RunId, input.CallId, offset, totalCharacters = text.Length, text = text.Substring(offset, count), hasMore = offset + count < text.Length, historicalSnapshot = true, sanitized = true }) };
        }
        catch (Exception error) when (error is ArgumentException or JsonException or UnauthorizedAccessException or InvalidOperationException or CryptographicException or KeyNotFoundException)
        { return new() { RequestId = request.RequestId, Succeeded = false, Error = "This historical context is unavailable to the current work attempt.", ContentType = "application/json", Payload = JsonPayload.From(new { error = "context_unavailable" }) }; }
    }
}
