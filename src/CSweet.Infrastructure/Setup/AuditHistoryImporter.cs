using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Bounded, resumable import into the existing ledger. Outbox source receipts are the checkpoint.</summary>
public sealed class AuditHistoryImporter(CSweetDbContext db)
{
    public async Task<int> ImportBatchAsync(CancellationToken token)
    {
        var imported = 0;
        imported += await ImportAsync<ConversationMessage>(token);
        imported += await ImportAsync<ChatTurn>(token);
        imported += await ImportAsync<ChatTurnTraceEvent>(token);
        imported += await ImportAsync<AgentWorkItem>(token);
        imported += await ImportAsync<AgentWorkAttempt>(token);
        imported += await ImportAsync<AgentWorkProgress>(token);
        imported += await ImportAsync<WorkTask>(token);
        imported += await ImportAsync<WorkItemActivity>(token);
        imported += await ImportAsync<WorkOrchestrationEvent>(token);
        imported += await ImportAsync<AgentRunLog>(token);
        imported += await ImportAsync<AgentRuntimeEvent>(token);
        // Existing sealed records are reused; their recorded identity remains the source of truth.
        var legacy = await db.AuditEvents.Where(x => x.IntegrityVersion < 2 &&
            !db.AuditOutbox.Any(o => o.SourceEntityType == "AuditEvent" && o.SourceEntityId == x.Id))
            .OrderBy(x => x.Sequence).Take(50).ToListAsync(token);
        foreach (var record in legacy)
        {
            var employees = new HashSet<Guid>();
            if (record.ActorOrganizationUserId is Guid actor) employees.Add(actor);
            if (record.MetadataJson is not null)
            {
                try
                {
                    using var metadata = JsonDocument.Parse(record.MetadataJson);
                    if (metadata.RootElement.ValueKind == JsonValueKind.Object && metadata.RootElement.TryGetProperty("recipients", out var recipients) && recipients.ValueKind == JsonValueKind.Array)
                        foreach (var recipient in recipients.EnumerateArray())
                            if (recipient.ValueKind == JsonValueKind.Object && (recipient.TryGetProperty("Id", out var id) || recipient.TryGetProperty("id", out id)) && id.ValueKind == JsonValueKind.String && id.TryGetGuid(out var employee)) employees.Add(employee);
                }
                catch (JsonException) { /* Malformed historical metadata is not reconstructed. */ }
            }
            if (record.OrganizationId is Guid org)
                foreach (var employee in employees)
                    if (!await db.AuditEventEmployees.AnyAsync(x => x.AuditEventId == record.Id && x.EmployeeId == employee, token))
                        db.AuditEventEmployees.Add(new() { AuditEventId = record.Id, OrganizationId = org, EmployeeId = employee, Role = "RecordedLegacyIdentity" });
            Receipt("AuditEvent", record.Id);
        }
        await db.SaveChangesAsync(token);
        return imported + legacy.Count;
    }

    private async Task<int> ImportAsync<T>(CancellationToken token) where T : class
    {
        var type = typeof(T).Name;
        var rows = await db.Set<T>().Where(x =>
            !db.AuditEvents.Any(a => a.EntityType == type && a.EntityId == EF.Property<Guid>(x, "Id")) &&
            !db.AuditOutbox.Any(a => a.SourceEntityType == type && a.SourceEntityId == EF.Property<Guid>(x, "Id")))
            .OrderBy(x => EF.Property<Guid>(x, "Id")).Take(25).ToListAsync(token);
        foreach (var row in rows)
        {
            var entry = db.Entry(row);
            var id = (Guid)entry.Property("Id").CurrentValue!;
            var request = db.DescribeAuditSource(entry, historical: true);
            if (request is null) Receipt(type, id);
            else db.QueueAudit(request);
        }
        await db.SaveChangesAsync(token);
        return rows.Count;
    }

    private void Receipt(string type, Guid id) => db.AuditOutbox.Add(new()
    {
        Id = CSweetDbContext.AuditSourceId($"audit-import:{type}:{id:D}"), SourceEntityType = type, SourceEntityId = id,
        CreatedAt = DateTimeOffset.UtcNow, DeliveredAt = DateTimeOffset.UtcNow,
        LastError = "Historical import inspected; only recorded evidence is attributable."
    });
}
