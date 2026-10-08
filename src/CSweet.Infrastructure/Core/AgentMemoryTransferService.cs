using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryTransferService(CSweetDbContext db, IMemoryStore memory, TimeProvider clock) : IAgentMemoryTransferService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Tables = ["episodes", "entities", "claims", "edges", "blocks", "procedures"];

    public async Task<MemoryTransferResult> PrepareAsync(Guid organizationId, Guid employeeId, Guid applicationUserId,
        PrepareMemoryTransferRequest request, CancellationToken token = default)
    {
        if (request.OperationId == Guid.Empty || request.TargetEmployeeId == Guid.Empty || request.TargetEmployeeId == employeeId ||
            request.Items is null || request.Items.Count > 32 || request.Items.Any(x => x is null || x.Id == Guid.Empty) ||
            request.Items.Distinct().Count() != request.Items.Count || request.Debrief is null || request.Debrief.Length > 16000 ||
            (request.Items.Count == 0 && string.IsNullOrWhiteSpace(request.Debrief)) ||
            !Enum.TryParse<MemorySensitivity>(request.DebriefSensitivity, out var sensitivity) || !Enum.IsDefined(sensitivity))
            throw new ArgumentException("Invalid transfer selection.");
        foreach (var item in request.Items) _ = Kind(item.Kind);
        await RequireBackendAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        try
        {
            var actor = await AuthorizePairAsync(organizationId, employeeId, request.TargetEmployeeId, applicationUserId, token);
            var (source, target) = Namespaces(organizationId, employeeId, request.TargetEmployeeId, actor, request.SourceScope, request.SourceAudienceId);
            await BarrierAsync(token);
            await AuthorizeScopesAsync(organizationId, employeeId, request.TargetEmployeeId, actor, source, target, token);
            var hash = Hash(new { organizationId, employeeId, applicationUserId, actor, request });
            if (await ReplayAsync(organizationId, request.OperationId, hash, token) is { } replay) return replay;
            await using var store = EnlistedStore();
            var items = await ReadSelectionAsync(store, source.Partition, request.Items, token);
            var now = clock.GetUtcNow();
            var package = new KnowledgeTransferPackage(Guid.NewGuid(), organizationId.ToString("D"), employeeId.ToString("D"),
                request.TargetEmployeeId.ToString("D"), [source], target, request.Debrief, items, sensitivity,
                KnowledgeTransferStatus.PendingApproval, now, actor.ToString("D"));
            if (MemoryTransferEvidence.RenderContent(package).Length > 32000) throw new ArgumentException("Transfer content is too large.");
            // Validate the complete snapshot before persisting a reviewable draft. This is not approval.
            var draftEvidence = await EvidenceAsync(store, package, token);
            await RequireInheritedAudiencesAsync(organizationId, employeeId, request.TargetEmployeeId, actor, draftEvidence, token);
            await store.WriteKnowledgeTransferAsync(package, token);
            var receipt = Receipt(organizationId, employeeId, request.TargetEmployeeId, applicationUserId, actor,
                request.OperationId, hash, "prepare", package);
            await SaveReceiptAsync(receipt, token);
            await transaction.CommitAsync(token);
            return Result(receipt, false);
        }
        catch { db.ChangeTracker.Clear(); throw; }
    }

    public async Task<MemoryTransferResponse> GetAsync(Guid organizationId, Guid employeeId, Guid packageId,
        Guid applicationUserId, CancellationToken token = default)
    {
        await RequireBackendAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var actor = await MemoryManagerAuthorization.RequireAsync(db, organizationId, employeeId, applicationUserId, true, token);
        await BarrierAsync(token);
        await using var store = EnlistedStore();
        var package = await AuthorizedPackageAsync(store, organizationId, employeeId, packageId, applicationUserId, token);
        var evidence = await TryEvidenceAsync(store, package, token);
        var current = evidence is not null && (package.Status == KnowledgeTransferStatus.PendingApproval ||
            JsonSerializer.Serialize(evidence, Json) == JsonSerializer.Serialize(package.ApprovedEvidence, Json));
        await RequireInheritedAudiencesAsync(organizationId, employeeId, Guid.Parse(package.TargetEmployeeId), actor, evidence ?? package.ApprovedEvidence, token);
        var reviewToken = await ReviewTokenAsync(package, evidence, actor, token);
        await transaction.CommitAsync(token);
        return new(package.Id, Guid.Parse(package.TargetEmployeeId), package.Status.ToString(), reviewToken,
            MemoryTransferEvidence.RenderContent(package), MemoryProvenance.Maximum(package.Items.Select(x => x.Sensitivity)
                .Append(package.DebriefSensitivity).ToArray()).ToString(), current && package.Status == KnowledgeTransferStatus.PendingApproval,
            current && package.Status == KnowledgeTransferStatus.Approved, package.Status != KnowledgeTransferStatus.Rejected, package.AppliedEpisodeId);
    }

    public async Task<MemoryTransferPage> ListAsync(Guid organizationId, Guid employeeId, Guid applicationUserId, Guid? cursor = null, CancellationToken token = default)
    {
        await MemoryManagerAuthorization.RequireAsync(db, organizationId, employeeId, applicationUserId, false, token);
        // Paginated recovery of the caller's preparations; every content read reauthorizes both employees.
        var query = db.MemoryTransferReceipts.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.EmployeeId == employeeId &&
            x.ActorApplicationUserId == applicationUserId && x.Action == "prepare");
        if (cursor.HasValue)
        {
            var before = await query.SingleOrDefaultAsync(x => x.Id == cursor.Value, token) ?? throw new ArgumentException("Invalid transfer cursor.");
            query = query.Where(x => x.CreatedAt < before.CreatedAt || (x.CreatedAt == before.CreatedAt && x.Id.CompareTo(before.Id) < 0));
        }
        var rows = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(21).ToListAsync(token);
        return new(rows.Take(20).Select(x => new MemoryTransferListItem(x.PackageId, x.TargetEmployeeId, x.CreatedAt)).ToArray(),
            rows.Count > 20 ? rows[19].Id : null);
    }

    public async Task<MemoryTransferResult> TransitionAsync(Guid organizationId, Guid employeeId, Guid packageId,
        Guid applicationUserId, TransitionMemoryTransferRequest request, CancellationToken token = default)
    {
        if (request.OperationId == Guid.Empty || request.ExpectedToken?.Length != 64 || request.Action is not ("approve" or "apply" or "reject"))
            throw new ArgumentException("Invalid transfer transition.");
        await RequireBackendAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        try
        {
            var actor = await MemoryManagerAuthorization.RequireAsync(db, organizationId, employeeId, applicationUserId, true, token);
            await BarrierAsync(token);
            await using var store = EnlistedStore();
            var package = await AuthorizedPackageAsync(store, organizationId, employeeId, packageId, applicationUserId, token);
            await RequireInheritedAudiencesAsync(organizationId, employeeId, Guid.Parse(package.TargetEmployeeId), actor, package.ApprovedEvidence, token);
            var hash = Hash(new { organizationId, employeeId, packageId, applicationUserId, actor, request });
            if (await ReplayAsync(organizationId, request.OperationId, hash, token) is { } replay) return replay;
            var evidence = await TryEvidenceAsync(store, package, token);
            await RequireInheritedAudiencesAsync(organizationId, employeeId, Guid.Parse(package.TargetEmployeeId), actor, evidence, token);
            if (request.ExpectedToken != await ReviewTokenAsync(package, evidence, actor, token)) throw Changed();
            if (request.Action == "reject")
            {
                if (package.Status == KnowledgeTransferStatus.Rejected) throw Changed();
                package = package with { Status = KnowledgeTransferStatus.Rejected };
                await store.WriteKnowledgeTransferAsync(package, token);
            }
            else
            {
                if (evidence is null) throw new InvalidOperationException("memory_transfer_source_unavailable");
                var access = new MemoryAccessContext(new(organizationId.ToString("D"), actor.ToString("D")), "employee-handoff", "review");
                // Every namespace and both current hierarchies have been authorized and locked above.
                var engine = new MemoryEngine(store, Options.Create(new AgentMemoryOptions { MaximumEpisodeCharacters = 32000 }),
                    authorizer: new AllowAllMemoryScopeAuthorizer());
                package = request.Action == "approve"
                    ? await engine.ApproveKnowledgeTransferAsync(new(package.Id, access, true), token)
                    : await engine.ApplyKnowledgeTransferAsync(new(package.Id, access), token);
                if (request.Action == "apply")
                {
                    await RegisterTargetAsync(organizationId, package, token);
                    var target = Guid.Parse(package.TargetEmployeeId);
                    var installation = await db.CoreOrganizationUsers.Where(x => x.Id == target && x.OrganizationId == organizationId)
                        .Select(x => x.AgentInstallationId).SingleAsync(token) ?? throw new UnauthorizedAccessException();
                    var episode = await ((IMemorySourceReader)store).GetEpisodeAsync(package.TargetNamespace.Partition,
                        package.AppliedEpisodeId!.Value, token) ?? throw new InvalidOperationException("memory_transfer_source_unavailable");
                    await AgentMemoryService.StageEpisodeJobAsync(db, episode, organizationId, target, installation, applicationUserId, token);
                }
            }
            var receipt = Receipt(organizationId, employeeId, Guid.Parse(package.TargetEmployeeId), applicationUserId, actor,
                request.OperationId, hash, request.Action, package);
            await SaveReceiptAsync(receipt, token);
            await transaction.CommitAsync(token);
            return Result(receipt, false);
        }
        catch { db.ChangeTracker.Clear(); throw; }
    }

    private async Task<KnowledgeTransferPackage> AuthorizedPackageAsync(PostgreSqlMemoryStore store, Guid organization,
        Guid employee, Guid id, Guid user, CancellationToken token)
    {
        var package = await store.GetKnowledgeTransferAsync(id, token) ?? throw new KeyNotFoundException();
        if (package.Id != id || package.TenantId != organization.ToString("D") || package.SourceEmployeeId != employee.ToString("D") ||
            !Guid.TryParse(package.TargetEmployeeId, out var targetId) || package.SourceNamespaces.Count != 1) throw new UnauthorizedAccessException();
        var actor = await AuthorizePairAsync(organization, employee, targetId, user, token);
        var source = package.SourceNamespaces[0];
        var scope = source.Audience switch { MemoryAudienceType.Employee => "Employee", MemoryAudienceType.UserRelationship => "Relationship",
            MemoryAudienceType.Organization => "Organization", MemoryAudienceType.Team => "Team", MemoryAudienceType.Role => "Role", _ => throw new UnauthorizedAccessException() };
        Guid? audience = scope is "Team" or "Role" && Guid.TryParseExact(source.AudienceId, "D", out var parsed) ? parsed : null;
        var expected = Namespaces(organization, employee, targetId, actor, scope, audience);
        if (source != expected.Source || package.TargetNamespace != expected.Target) throw new UnauthorizedAccessException();
        await AuthorizeScopesAsync(organization, employee, targetId, actor, source, package.TargetNamespace, token);
        return package;
    }

    private async Task<Guid> AuthorizePairAsync(Guid organization, Guid employee, Guid target, Guid user, CancellationToken token)
    {
        if (employee == target) throw new ArgumentException("Transfer requires a different employee.");
        var actor = await MemoryManagerAuthorization.RequireAsync(db, organization, employee, user, true, token);
        if (actor != await MemoryManagerAuthorization.RequireAsync(db, organization, target, user, true, token)) throw new UnauthorizedAccessException();
        return actor;
    }

    private async Task AuthorizeScopesAsync(Guid organization, Guid employee, Guid target, Guid actor,
        MemoryNamespace sourceScope, MemoryNamespace targetScope, CancellationToken token)
    {
        if (sourceScope.Audience is MemoryAudienceType.Team or MemoryAudienceType.Role)
        {
            await MemoryEpisodeOperatorAuthorization.RequirePartitionAsync(db, organization, employee, actor, sourceScope.Partition, token);
            await MemoryEpisodeOperatorAuthorization.RequirePartitionAsync(db, organization, target, actor, sourceScope.Partition, token);
        }
        else await MemoryManagerAuthorization.RequirePartitionAsync(db, organization, employee, actor, sourceScope.Partition, token);
        await MemoryManagerAuthorization.RequirePartitionAsync(db, organization, target, actor, targetScope.Partition, token);
    }

    private static (MemoryNamespace Source, MemoryNamespace Target) Namespaces(Guid organization, Guid employee, Guid target, Guid actor, string scope, Guid? audience = null)
    {
        var tenant = organization.ToString("D"); var from = employee.ToString("D"); var to = target.ToString("D");
        if (scope is "Team" or "Role" ? audience is null || audience == Guid.Empty : audience is not null)
            throw new ArgumentException("Invalid transfer audience identity.");
        return scope switch
        {
            "Employee" => (EmployeeMemoryNamespaces.Employee(tenant, from, "csweet"), EmployeeMemoryNamespaces.Employee(tenant, to, "csweet")),
            "Relationship" => (EmployeeMemoryNamespaces.UserRelationship(tenant, from, actor.ToString("D"), "csweet"),
                EmployeeMemoryNamespaces.UserRelationship(tenant, to, actor.ToString("D"), "csweet")),
            "Organization" => (EmployeeMemoryNamespaces.Organization(tenant, "csweet"), EmployeeMemoryNamespaces.Employee(tenant, to, "csweet")),
            "Team" => (EmployeeMemoryNamespaces.Team(tenant, audience!.Value.ToString("D"), "csweet"), EmployeeMemoryNamespaces.Employee(tenant, to, "csweet")),
            "Role" => (EmployeeMemoryNamespaces.Role(tenant, audience!.Value.ToString("D"), "csweet"), EmployeeMemoryNamespaces.Employee(tenant, to, "csweet")),
            _ => throw new ArgumentException("Unsupported transfer audience.")
        };
    }

    private async Task<MemoryTransferEvidence> EvidenceAsync(PostgreSqlMemoryStore store, KnowledgeTransferPackage package, CancellationToken token) =>
        await store.CaptureTransferEvidenceAsync(package with { Status = KnowledgeTransferStatus.Approved,
            ApprovedByEmployeeId = package.ApprovedByEmployeeId ?? package.CreatedByEmployeeId,
            ApprovedAt = package.ApprovedAt ?? package.CreatedAt }, token);

    private async Task<MemoryTransferEvidence?> TryEvidenceAsync(PostgreSqlMemoryStore store, KnowledgeTransferPackage package, CancellationToken token)
    {
        try { return await EvidenceAsync(store, package, token); }
        catch (InvalidOperationException) { return null; }
    }

    private async Task RequireBackendAsync(CancellationToken token)
    {
        if (!db.Database.IsNpgsql() || memory is not PostgreSqlMemoryStore) throw new NotSupportedException("Transfers require shared PostgreSQL transactions.");
        await memory.InitializeAsync(token);
    }

    // Serialize source and package writers while validating recursive cross-scope dependencies.
    // Keep this boundary short: no remote calls, model calls or unbounded exports are allowed inside it.
    private Task BarrierAsync(CancellationToken token) => MemoryReviewWriteBarrier.AcquireAsync(db, token);
    private PostgreSqlMemoryStore EnlistedStore() => new((NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction());
    private NpgsqlCommand Command(string sql) => new(sql, (NpgsqlConnection)db.Database.GetDbConnection(),
        (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction());

    private async Task<MemoryTransferResult?> ReplayAsync(Guid organization, Guid operation, string hash, CancellationToken token)
    {
        var receipt = await db.MemoryTransferReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organization && x.OperationId == operation, token);
        if (receipt is null) return null;
        if (receipt.RequestHash != hash) throw Changed();
        await using var store = EnlistedStore();
        var package = await AuthorizedPackageAsync(store, organization, receipt.EmployeeId, receipt.PackageId, receipt.ActorApplicationUserId, token);
        var evidence = await TryEvidenceAsync(store, package, token) ?? package.ApprovedEvidence;
        if (evidence is null && package.Items.Count > 0) throw new InvalidOperationException("memory_transfer_source_unavailable");
        await RequireInheritedAudiencesAsync(organization, receipt.EmployeeId, receipt.TargetEmployeeId, receipt.ActorOrganizationUserId, evidence, token);
        return Result(receipt, true);
    }

    private MemoryTransferReceipt Receipt(Guid organization, Guid employee, Guid target, Guid user, Guid actor,
        Guid operation, string hash, string action, KnowledgeTransferPackage package) => new()
    {
        Id = Guid.NewGuid(), OrganizationId = organization, EmployeeId = employee, TargetEmployeeId = target, PackageId = package.Id,
        ActorApplicationUserId = user, ActorOrganizationUserId = actor, OperationId = operation, RequestHash = hash,
        Action = action, Status = package.Status.ToString(), AppliedEpisodeId = package.AppliedEpisodeId, CreatedAt = clock.GetUtcNow()
    };
    private async Task SaveReceiptAsync(MemoryTransferReceipt receipt, CancellationToken token)
    {
        db.MemoryTransferReceipts.Add(receipt);
        db.QueueAudit(new AuditEventWriteRequest("memory.transfer.reviewed.v1", "Memory", OrganizationId: receipt.OrganizationId,
            EntityType: "MemoryTransfer", EntityId: receipt.PackageId, Summary: "A human reviewer changed a knowledge transfer.",
            MetadataJson: JsonSerializer.Serialize(new { receipt.Action, receipt.Status, receipt.TargetEmployeeId, receipt.AppliedEpisodeId }),
            OccurredAt: receipt.CreatedAt, CorrelationId: receipt.OperationId.ToString("D"),
            Actor: new AuditActor("Human", ApplicationUserId: receipt.ActorApplicationUserId, OrganizationUserId: receipt.ActorOrganizationUserId),
            EventId: receipt.Id, Employees: [new(receipt.EmployeeId, "Source"), new(receipt.TargetEmployeeId, "Target")], UseAmbientOrganization: false));
        await db.SaveChangesAsync(token);
    }
    private async Task RegisterTargetAsync(Guid organization, KnowledgeTransferPackage package, CancellationToken token)
    {
        var partition = package.TargetNamespace.Partition;
        var target = Guid.Parse(package.TargetEmployeeId);
        var user = partition.UserId is null ? (Guid?)null : Guid.Parse(partition.UserId);
        var now = clock.GetUtcNow();
        var count = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AgentMemoryNamespaces" ("Id", "OrganizationId", "EmployeeId", "UserId", "PartitionKey", "Scope", "CreatedAt", "UpdatedAt")
            VALUES ({Guid.NewGuid()}, {organization}, {target}, {user}, {partition.Key}, {package.TargetNamespace.Scope.ToString()}, {now}, {now})
            ON CONFLICT ("PartitionKey") DO UPDATE SET "UpdatedAt"=EXCLUDED."UpdatedAt"
            WHERE "AgentMemoryNamespaces"."OrganizationId"=EXCLUDED."OrganizationId"
                AND "AgentMemoryNamespaces"."EmployeeId"=EXCLUDED."EmployeeId"
                AND "AgentMemoryNamespaces"."UserId" IS NOT DISTINCT FROM EXCLUDED."UserId"
            """, token);
        if (count != 1) throw new InvalidOperationException("memory_transfer_namespace_conflict");
    }
    private static MemoryTransferResult Result(MemoryTransferReceipt receipt, bool replay) =>
        new(receipt.Id, receipt.PackageId, receipt.Status, receipt.AppliedEpisodeId, replay);
    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json))).ToLowerInvariant();
    private static DbUpdateConcurrencyException Changed() => new("memory_transfer_changed");

    private static MemoryRecordKind Kind(string kind) => kind switch
    {
        "Episode" => MemoryRecordKind.Episode, "Claim" => MemoryRecordKind.Claim, "Edge" => MemoryRecordKind.Edge,
        "Block" => MemoryRecordKind.Block, "Procedure" => MemoryRecordKind.Procedure, _ => throw new ArgumentException("Unsupported transfer item.")
    };

    private async Task<IReadOnlyList<KnowledgeTransferItem>> ReadSelectionAsync(PostgreSqlMemoryStore store, MemoryPartition partition,
        IReadOnlyList<MemoryTransferSelection> selection, CancellationToken token)
    {
        var rows = new Dictionary<(MemoryRecordKind Kind, Guid Id), JsonElement>();
        var pending = new Queue<(MemoryRecordKind Kind, Guid Id)>(selection.Select(x => (Kind(x.Kind), x.Id)));
        var characters = 0;
        while (pending.TryDequeue(out var key))
        {
            if (rows.ContainsKey(key)) continue;
            if (rows.Count >= MemoryTransferEvidence.MaximumRecords) throw new ArgumentException("Transfer has too many dependencies.");
            var table = "csweet_memory_" + Tables[(int)key.Kind];
            await using var command = Command($"SELECT payload::text FROM {table} r WHERE partition_key=@partition AND id=@id AND NOT EXISTS(SELECT 1 FROM csweet_memory_partition_migration_rows q WHERE q.table_name=@table AND q.record_id=r.id::text AND q.disposition='Quarantine')");
            command.Parameters.AddWithValue("partition", partition.StorageKey); command.Parameters.AddWithValue("id", key.Id); command.Parameters.AddWithValue("table", table);
            var payload = (string?)await command.ExecuteScalarAsync(token) ?? throw new InvalidOperationException("memory_transfer_source_unavailable");
            characters += payload.Length;
            if (characters > 1_048_576) throw new ArgumentException("Transfer evidence is too large.");
            using var document = JsonDocument.Parse(payload); var value = document.RootElement.Clone();
            if (value.GetProperty("id").GetGuid() != key.Id || value.GetProperty("partition").Deserialize<MemoryPartition>(Json) != partition)
                throw new InvalidOperationException("memory_transfer_source_unavailable");
            rows.Add(key, value);
            if (key.Kind == MemoryRecordKind.Episode && value.Deserialize<MemoryEpisode>(Json)?.CorrectionEvidence is { } correction)
            {
                if (correction.Sources is not { Count: > 0 and <= MemoryProvenance.MaximumSourceEpisodes })
                    throw new InvalidOperationException("memory_transfer_source_unavailable");
                foreach (var reference in correction.Sources) pending.Enqueue((MemoryRecordKind.Episode, reference.EpisodeId));
            }
            if (value.TryGetProperty("sourceEpisodeIds", out var sources))
            {
                if (sources.ValueKind != JsonValueKind.Array || sources.GetArrayLength() > MemoryProvenance.MaximumSourceEpisodes)
                    throw new InvalidOperationException("memory_transfer_source_unavailable");
                foreach (var id in sources.EnumerateArray()) pending.Enqueue((MemoryRecordKind.Episode, id.GetGuid()));
            }
            foreach (var property in key.Kind switch
            {
                MemoryRecordKind.Claim => new[] { "episodeId", "subjectEntityId", "objectEntityId" },
                MemoryRecordKind.Edge => new[] { "episodeId", "fromEntityId", "toEntityId" },
                MemoryRecordKind.Procedure => new[] { "episodeId" }, _ => Array.Empty<string>()
            })
                if (value.TryGetProperty(property, out var id) && id.ValueKind != JsonValueKind.Null)
                    pending.Enqueue((property == "episodeId" ? MemoryRecordKind.Episode : MemoryRecordKind.Entity, id.GetGuid()));
        }
        List<T> Records<T>(MemoryRecordKind kind) => rows.Where(x => x.Key.Kind == kind).Select(x => x.Value.Deserialize<T>(Json)!).ToList();
        var episodes = new List<MemoryEpisode>();
        foreach (var episode in Records<MemoryEpisode>(MemoryRecordKind.Episode))
            episodes.Add(await store.GetEpisodeAsync(partition, episode.Id, token) ?? throw new InvalidOperationException("memory_transfer_source_unavailable"));
        var export = new MemoryExport("1.0", episodes, Records<MemoryEntity>(MemoryRecordKind.Entity), Records<MemoryClaim>(MemoryRecordKind.Claim),
            Records<MemoryEdge>(MemoryRecordKind.Edge), Records<MemoryBlock>(MemoryRecordKind.Block), Records<ProceduralMemory>(MemoryRecordKind.Procedure));
        var projected = MemoryReadProjection.TransferItems(MemoryReadProjection.Create(export, partition, MemorySensitivity.Restricted, clock.GetUtcNow()), partition).ToArray();
        var selected = new List<KnowledgeTransferItem>();
        foreach (var item in selection)
        {
            var layer = Kind(item.Kind) switch { MemoryRecordKind.Episode => MemoryLayer.Episodic, MemoryRecordKind.Block => MemoryLayer.Core,
                MemoryRecordKind.Procedure => MemoryLayer.Procedural, _ => MemoryLayer.Semantic };
            var matches = projected.Where(x => x.MemoryId == item.Id && x.Layer == layer).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("memory_transfer_source_unavailable");
            selected.Add(matches[0]);
        }
        return selected;
    }
}
