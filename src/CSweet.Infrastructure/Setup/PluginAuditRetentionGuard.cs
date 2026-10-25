using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Verifies content omission; never edits or deletes the immutable security ledger.</summary>
internal static class PluginAuditRetentionGuard
{
    internal static bool ContentFree(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var json=JsonDocument.Parse(bytes);var root=json.RootElement;
            if(root.ValueKind!=JsonValueKind.Object || root.EnumerateObject().Count()!=4 ||
                root.EnumerateObject().Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()!=4 ||
                root.GetProperty("evidence").EnumerateObject().Count()!=1 ||
                root.GetProperty("evidence").GetProperty("contentPolicy").GetString()!="memory-content-omitted-v1" ||
                root.GetProperty("historical").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                root.GetProperty("historyNotice").ValueKind!=JsonValueKind.Null && root.GetProperty("historyNotice").GetString()!=
                    "Imported source snapshot; missing transitions and original versions cannot be reconstructed.")return false;
            var source=root.GetProperty("source");
            if(source.ValueKind!=JsonValueKind.Object || source.EnumerateObject().Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()!=source.EnumerateObject().Count())return false;
            return source.EnumerateObject().All(p=>CSweetDbContext.IsContentFreeModelAuditField(p.Name) && (p.Value.ValueKind switch
            {
                JsonValueKind.Null=>true,
                JsonValueKind.Number=>p.Value.TryGetDecimal(out _),
                JsonValueKind.String=>Guid.TryParse(p.Value.GetString(),out _) || p.Value.TryGetDateTimeOffset(out _) ||
                    p.Value.GetString() is {Length:64} digest && digest.All(Uri.IsHexDigit) ||
                    p.Name.Contains("Token",StringComparison.Ordinal) && p.Value.GetString()=="[REDACTED]",
                _=>false
            }));
        }
        catch(Exception e) when(e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException){return false;}
    }
    private static bool Metadata(string? json)
    {
        if(json==null)return true;
        try
        {
            using var d=JsonDocument.Parse(json);
            return d.RootElement.ValueKind==JsonValueKind.Object && d.RootElement.EnumerateObject().Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()==d.RootElement.EnumerateObject().Count() && d.RootElement.EnumerateObject().All(p=>
                p.Name is "activityWorkItemId" or "activityConversationId" or "activityPerspectiveEmployeeId" &&
                (p.Value.ValueKind==JsonValueKind.Null || p.Value.ValueKind==JsonValueKind.String && Guid.TryParse(p.Value.GetString(),out _)));
        }catch(JsonException){return false;}
    }
    private static bool KnownEvent(string type,string name)=>name=="audit.source.snapshot" || (type switch
    {
        "ConversationMessage"=>name is "communication.message.sent" or "communication.message.edited" or "communication.message.deleted",
        "AgentWorkItem"=>Enum.GetNames<AgentWorkStatus>().Any(s=>name=="agent.work."+s.ToLowerInvariant()),
        "AgentWorkAttempt"=>name is "agent.work.attempt.started" or "agent.work.attempt.stopped",
        "AgentWorkProgress"=>name=="agent.work.progress",
        "AgentRunLog"=>name is "model.call.started" or "model.call.completed" or "model.response.chunk",
        "ChatTurn"=>Enum.GetNames<CSweet.Domain.Core.ChatTurnStatus>().Any(s=>name=="chat.turn."+s.ToLowerInvariant()),
        "ChatTurnTraceEvent"=>name=="chat.trace.recorded",
        "WorkTask"=>Enum.GetNames<CSweet.Domain.Core.WorkTaskStatus>().Any(s=>name=="work.item."+s.ToLowerInvariant()),
        "AgentRuntimeEvent"=>Enum.GetNames<AgentRuntimeStatus>().Any(s=>name=="agent.runtime."+s.ToLowerInvariant()),
        "AgentPlatformEventOutboxItem"=>Enum.GetNames<CSweet.Domain.Core.AgentPlatformEventOutboxStatus>().Any(s=>name=="agent.event."+s.ToLowerInvariant()),
        _=>false
    });
    private static bool Headers(string outcome,string? correlation,string? externalMessage,string? externalRequest)=>
        externalMessage==null && externalRequest==null && (correlation==null || Guid.TryParse(correlation,out _)) &&
        (outcome is "" or "Completed" or "Succeeded" or "Failed" or "Cancelled" or "Running" or "Recorded" ||
         Enum.GetNames<AgentWorkStatus>().Contains(outcome,StringComparer.Ordinal) || Enum.GetNames<AgentRuntimeStatus>().Contains(outcome,StringComparer.Ordinal) ||
         Enum.GetNames<CSweet.Domain.Core.WorkTaskStatus>().Contains(outcome,StringComparer.Ordinal) || Enum.GetNames<CSweet.Domain.Core.ChatTurnStatus>().Contains(outcome,StringComparer.Ordinal));
    internal static async Task<bool> VerifyAsync(CSweetDbContext db,IDataProtectionProvider protection,Guid organization,
        IReadOnlyList<PluginAccountDataCopy> copies,CancellationToken ct)
    {
        var sources=copies.Where(c=>!c.Store.StartsWith("external:") && c.Store is not ("consumer-origin" or "memory-origin")).ToArray();
        if(sources.Length>4096 || sources.Any(c=>c.OrganizationId!=organization))return false;
        var ids=sources.Select(c=>c.RecordId).ToArray();
        var outbox=await db.AuditOutbox.AsNoTracking().Where(x=>x.SourceEntityId!=null && ids.Contains(x.SourceEntityId.Value)).Take(8193).ToArrayAsync(ct);
        var ledger=await db.AuditEvents.AsNoTracking().Where(x=>x.EntityId!=null && ids.Contains(x.EntityId.Value)).Take(8193).ToArrayAsync(ct);
        if(outbox.Length+ledger.Length>8192)return false;
        long length=0;
        bool Safe(Guid? org,string type,Guid? id,string eventType,string? summary,string? metadata,string? error,string? code,ReadOnlyMemory<byte> body)
        {
            length+=body.Length;
            return length<=33554432 && org==organization && id!=null && sources.Any(c=>c.RecordId==id && c.Store==type) &&
                KnownEvent(type,eventType) && (summary==eventType || type==nameof(AgentRunLog) && eventType=="model.response.chunk" && summary==null) &&
                error==null && code==null && Metadata(metadata) &&
                (type==nameof(AgentRunLog) ? eventType is "model.call.started" or "model.call.completed" or "model.response.chunk" or "audit.source.snapshot" &&
                    CSweet.Infrastructure.Core.MemoryWorkErasure.SafeModelEvidence(Encoding.UTF8.GetString(body.Span),eventType) : ContentFree(body));
        }
        try
        {
            foreach(var pending in outbox)
            {
                var json=pending.ProtectedRequest is {Length:>0} encrypted
                    ? protection.CreateProtector("CSweet.AuditOutbox.v1").Unprotect(encrypted) : Encoding.UTF8.GetBytes(pending.RequestJson);
                if(json.Length>1048576)return false;
                var request=JsonSerializer.Deserialize<AuditEventWriteRequest>(json);
                if(request?.Payload is not {} body || request.EventId!=pending.Id || !Headers(request.Outcome,request.CorrelationId,request.ExternalMessageId,request.ExternalRequestId) || !Safe(request.OrganizationId,request.EntityType ?? "",request.EntityId,
                    request.EventType,request.Summary,request.MetadataJson,request.ErrorMessage,request.ErrorCode,body))return false;
            }
            foreach(var item in ledger)
            {
                var payload=await db.AuditEventPayloads.AsNoTracking().SingleOrDefaultAsync(x=>x.AuditEventId==item.Id,ct);
                var body=payload==null ? Encoding.UTF8.GetBytes(item.PayloadPreview ?? "") : protection.CreateProtector("CSweet.AuditPayload.v1").Unprotect(payload.ProtectedContent);
                if(!Headers(item.Outcome,item.CorrelationId,item.ExternalMessageId,item.ExternalRequestId) || !Safe(item.OrganizationId,item.EntityType,item.EntityId,item.EventType,item.Summary,item.MetadataJson,item.ErrorMessage,item.ErrorCode,body))return false;
            }
            return true;
        }
        catch(Exception e) when(e is CryptographicException or JsonException or ArgumentException){return false;}
    }
}
