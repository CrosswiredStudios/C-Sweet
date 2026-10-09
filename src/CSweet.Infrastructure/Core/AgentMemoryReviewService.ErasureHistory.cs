using System.Text;
using System.Text.Json;
using CSweet.Contracts.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryReviewService
{
    private const int MaximumErasureInventoryBytes = 2 * 1024 * 1024;

    public async Task<MemoryErasureOperationPage> ListErasureOperationsAsync(Guid organizationId, Guid employeeId,
        Guid applicationUserId, Guid? beforeReceiptId = null, int limit = 10, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 20 || beforeReceiptId == Guid.Empty) throw new ArgumentException("Invalid erasure operation page.");
        await RequireBackendAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var actor = await RequireHoldAuthorityAsync(organizationId, employeeId, applicationUserId, cancellationToken);
        var own = db.MemoryErasureReceipts.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
            x.EmployeeId == employeeId && x.ActorApplicationUserId == applicationUserId && x.ActorOrganizationUserId == actor);
        DateTimeOffset? beforeTime = null;
        if (beforeReceiptId is { } cursor)
        {
            beforeTime = await own.Where(x => x.Id == cursor).Select(x => (DateTimeOffset?)x.CreatedAt).SingleOrDefaultAsync(cancellationToken)
                ?? throw new ArgumentException("Invalid erasure operation cursor.");
        }
        // Bound inventory bytes in SQL before materialization. No source IDs, counts,
        // audience/producer identities, runtime IDs or inventory bytes leave this route.
        var rows = new List<(Guid Id, Guid Operation, DateTimeOffset Created, string? Inventory)>();
        await using (var command = Command("""
            SELECT "Id","OperationId","CreatedAt",
                CASE WHEN octet_length("InventoryJson")<=@bytes THEN "InventoryJson" ELSE NULL END
            FROM "MemoryErasureReceipts"
            WHERE "OrganizationId"=@organization AND "EmployeeId"=@employee AND
                "ActorApplicationUserId"=@user AND "ActorOrganizationUserId"=@actor AND
                (@before IS NULL OR "CreatedAt"<@before OR ("CreatedAt"=@before AND "Id"<@cursor))
            ORDER BY "CreatedAt" DESC,"Id" DESC LIMIT @limit FOR SHARE
            """))
        {
            command.Parameters.AddWithValue("bytes", MaximumErasureInventoryBytes);
            command.Parameters.AddWithValue("organization", organizationId); command.Parameters.AddWithValue("employee", employeeId);
            command.Parameters.AddWithValue("user", applicationUserId); command.Parameters.AddWithValue("actor", actor);
            command.Parameters.AddWithValue("before", NpgsqlTypes.NpgsqlDbType.TimestampTz, (object?)beforeTime ?? DBNull.Value);
            command.Parameters.AddWithValue("cursor", beforeReceiptId ?? Guid.Empty); command.Parameters.AddWithValue("limit", limit + 1);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetFieldValue<DateTimeOffset>(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        var summaries = new List<MemoryErasureOperationSummary>();
        foreach (var row in rows.Take(limit))
        {
            var availability = "available";
            try
            {
                var inventory = ReadErasureReceiptInventory(row.Inventory);
                await AuthorizeErasureAudiencesAsync(organizationId, employeeId, applicationUserId, actor, inventory.Audiences, cancellationToken);
                await AuthorizeErasureOwnersAsync(organizationId, applicationUserId, actor, inventory.Owners!, cancellationToken);
            }
            catch (UnauthorizedAccessException) { availability = "permission-required"; }
            catch (InvalidOperationException error) when (error.Message == "memory_erasure_ownership_review_required")
            { availability = "ownership-review-required"; }
            catch (InvalidOperationException error) when (error.Message == "memory_erasure_evidence_review_required")
            { availability = "evidence-review-required"; }
            // These are the original human's own operation IDs. Revoked nested rights
            // withhold status/content; they do not expose another human's operation history.
            summaries.Add(new(row.Operation, row.Created, availability));
        }
        await transaction.CommitAsync(cancellationToken);
        return new(summaries, rows.Count > limit ? rows[limit - 1].Id : null);
    }

    private static ErasureReceiptInventory ReadErasureReceiptInventory(string? payload)
    {
        if (payload is null || Encoding.UTF8.GetByteCount(payload) > MaximumErasureInventoryBytes)
            throw new InvalidOperationException("memory_erasure_evidence_review_required");
        ErasureReceiptInventory inventory;
        try { inventory = JsonSerializer.Deserialize<ErasureReceiptInventory>(payload, JsonOptions) ?? throw new JsonException(); }
        catch (JsonException error) { throw new InvalidOperationException("memory_erasure_evidence_review_required", error); }
        if (inventory.OwnershipVersion != 1 || inventory.Owners is not { Length: > 0 })
            throw new InvalidOperationException("memory_erasure_ownership_review_required");
        if (inventory.Audiences is not { Length: > 0 and <= 64 } || inventory.Audiences.Any(x => x is null) ||
            inventory.Owners.Length > 1024 || inventory.Owners.Any(x => x is null || x.EmployeeId == Guid.Empty || x.Partition is null) ||
            inventory.PendingRuntimes is not { Length: <= 1024 } || inventory.PendingRuntimes.Any(x => x is null || x.Id == Guid.Empty || x.RequestedAt == default) ||
            inventory.PendingRuntimes.Select(x => x.Id).Distinct().Count() != inventory.PendingRuntimes.Length ||
            inventory.TurnIds is not { Length: <= 1024 } || inventory.TurnIds.Any(x => x == Guid.Empty))
            throw new InvalidOperationException("memory_erasure_evidence_review_required");
        return inventory;
    }
}
