using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Memory;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryReviewService
{
    private sealed record TransferRetention(bool IsTransferred, string? EvidenceHash, int Sources, int Held, string? Blocker,
        MemoryPartition[]? Audiences=null);
    private sealed class UnresolvedTransferRetention : Exception;

    // Generic extraction already verifies recall eligibility. Reuse the retained closure's
    // current audience/owner authorization without treating a hold as a processing ban.
    internal async Task RequireTransferAudienceAsync(Guid organization, Guid employee, Guid user, Guid actor,
        MemoryEpisode episode, CancellationToken token)
    {
        var evidence = await ReadTransferRetentionAsync(organization, employee, user, actor, episode, token);
        if (!evidence.IsTransferred || evidence.Blocker == "memory_transfer_retention_review_required")
            throw new InvalidOperationException("memory_transfer_source_unavailable");
    }

    private static string HoldToken(long revision, string payload, TransferRetention retention) => retention.IsTransferred
        ? Hash(new { revision, Payload = payload, retention.EvidenceHash }) : Hash(new { revision, Payload = payload });

    private async Task<TransferRetention> ReadTransferRetentionAsync(Guid organization, Guid employee, Guid user, Guid actor,
        MemoryEpisode root, CancellationToken token)
    {
        bool Copied(MemoryEpisode episode) => episode.TransferEvidence is not null || episode.CorrectionEvidence is not null ||
            episode.SourceFingerprint?.StartsWith("sha256-v3:", StringComparison.Ordinal) == true ||
            string.Equals(episode.Source?.Type, "knowledge-transfer", StringComparison.OrdinalIgnoreCase);
        if (!Copied(root)) return new(false, null, 0, 0, null);

        // Caller owns the episode and transfer writer barriers. Bind the complete retained
        // source inventory, including suppressed/expired evidence, rather than recall eligibility.
        // A release is a retention decision; it never re-approves the copied content.
        var snapshots = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var episodes = new Dictionary<(MemoryPartition Partition, Guid Id), MemoryEpisode>();
        var approvedEpisodes = new Dictionary<MemoryTransferRecord, MemoryEpisode>();
        var path = new HashSet<Guid>();
        var packages = new HashSet<Guid>();
        var audiences = new HashSet<(MemoryPartition Partition, Guid Owner)>();
        var bytes = 0;
        var held = 0;
        var sources = 0;
        void Snapshot(string key, string payload)
        {
            if (snapshots.ContainsKey(key)) return;
            bytes = checked(bytes + Encoding.UTF8.GetByteCount(payload));
            if (bytes > 32 * 1024 * 1024 || snapshots.Count >= 1600) throw new UnresolvedTransferRetention();
            snapshots.Add(key, payload);
        }
        async Task Authorize(MemoryPartition partition, Guid fallbackEmployee)
        {
            var owner = partition.AgentId is null ? fallbackEmployee : Guid.TryParse(partition.AgentId, out var parsed)
                ? parsed : throw new UnresolvedTransferRetention();
            if (owner != fallbackEmployee || partition.TenantId != organization.ToString("D")) throw new UnresolvedTransferRetention();
            if (!audiences.Add((partition, owner))) return;
            if (audiences.Count > 64) throw new UnresolvedTransferRetention();
            var current = await MemoryManagerAuthorization.RequireAsync(db, organization, owner, user, true, token, failOnLockContention: true);
            if (current != actor) throw new UnauthorizedAccessException();
            if (MemorySharedAudiences.IsCanonical(partition))
                await MemoryEpisodeOperatorAuthorization.RequirePartitionAsync(db, organization, owner, actor, partition, token);
            else await MemoryManagerAuthorization.RequirePartitionAsync(db, organization, owner, actor, partition, token, failOnLockContention: true);
        }
        void CanonicalAudience(MemoryNamespace value, Guid owner)
        {
            var tenant = organization.ToString("D"); var id = owner.ToString("D");
            var expected = value.Audience switch
            {
                MemoryAudienceType.Employee => EmployeeMemoryNamespaces.Employee(tenant, id, "csweet"),
                MemoryAudienceType.UserRelationship => EmployeeMemoryNamespaces.UserRelationship(tenant, id, actor.ToString("D"), "csweet"),
                MemoryAudienceType.Organization => EmployeeMemoryNamespaces.Organization(tenant, "csweet"),
                MemoryAudienceType.Team => EmployeeMemoryNamespaces.Team(tenant, Guid.Parse(value.AudienceId).ToString("D"), "csweet"),
                MemoryAudienceType.Role => EmployeeMemoryNamespaces.Role(tenant, Guid.Parse(value.AudienceId).ToString("D"), "csweet"),
                _ => throw new UnresolvedTransferRetention()
            };
            if (value != expected) throw new UnresolvedTransferRetention();
        }
        async Task Visit(MemoryEpisode episode, int depth)
        {
            if (!Copied(episode)) return;
            if (depth >= 3 || !path.Add(episode.Id)) throw new UnresolvedTransferRetention();
            try
            {
                if (episode.CorrectionEvidence is { } correction)
                {
                    if (episode.TransferEvidence is not null || !MemorySourceIntegrity.IsVerified(episode) || correction.ReviewOperationId == Guid.Empty ||
                        correction.Sources is not { Count: > 0 and <= MemoryProvenance.MaximumSourceEpisodes } ||
                        episode.Source.Type != "user" || episode.Source.Id != correction.ReviewOperationId.ToString("D") ||
                        !Guid.TryParseExact(episode.Source.Author, "D", out var reviewer)) throw new UnresolvedTransferRetention();
                    string receiptPayload;
                    await using (var command = Command("""
                        SELECT ((to_jsonb(r) - 'ClaimId' - 'ResultClaimId') ||
                            jsonb_build_object('MemoryId',r."ClaimId",'ResultMemoryId',r."ResultClaimId"))::text FROM "MemoryReviewReceipts" r
                        WHERE r."OrganizationId"=@organization AND r."OperationId"=@operation FOR SHARE
                        """))
                    {
                        command.Parameters.AddWithValue("organization", organization); command.Parameters.AddWithValue("operation", correction.ReviewOperationId);
                        receiptPayload = await command.ExecuteScalarAsync(token) as string ?? throw new UnresolvedTransferRetention();
                    }
                    var receipt = JsonSerializer.Deserialize<MemoryReviewReceipt>(receiptPayload, JsonOptions) ?? throw new UnresolvedTransferRetention();
                    var referenceType = receipt.RecordKind switch { "Claim" => "memory-claim", "Procedure" => "memory-procedure", "Block" => "memory-block", _ => null };
                    if (receipt.Action != "correct" || receipt.ActorOrganizationUserId != reviewer || receipt.OperationId != correction.ReviewOperationId ||
                        receipt.OrganizationId != organization || referenceType is null || episode.OperationalReferences is null ||
                        !episode.OperationalReferences.Any(x => x.Type == referenceType && x.Id == receipt.MemoryId.ToString("D") &&
                            x.Version == receipt.PreviousRevision.ToString(System.Globalization.CultureInfo.InvariantCulture))) throw new UnresolvedTransferRetention();
                    Snapshot("correction-review:" + correction.ReviewOperationId.ToString("D"), receiptPayload);
                    await Authorize(episode.Partition, receipt.EmployeeId);
                    var contributors = new List<MemoryEpisode>(); var ids = new HashSet<Guid>();
                    foreach (var reference in correction.Sources)
                    {
                        if (reference is null || reference.EpisodeId == Guid.Empty || !ids.Add(reference.EpisodeId) || path.Contains(reference.EpisodeId))
                            throw new UnresolvedTransferRetention();
                        var key = (episode.Partition, reference.EpisodeId);
                        if (!episodes.TryGetValue(key, out var source))
                        {
                            if (episodes.Count >= MemoryTransferEvidence.MaximumRecords) throw new UnresolvedTransferRetention();
                            LegacyEpisode retained;
                            try { retained = await LockLegacyEpisodeAsync(reference.EpisodeId, token); }
                            catch (KeyNotFoundException) { throw new UnresolvedTransferRetention(); }
                            source = retained.Episode;
                            if (source.Partition != episode.Partition || !MemorySourceIntegrity.IsVerified(source)) throw new UnresolvedTransferRetention();
                            await Authorize(source.Partition, receipt.EmployeeId); episodes.Add(key, source);
                            var revision = await RevisionAsync(source.Partition, source.Id, token, MemoryRecordKind.Episode);
                            Snapshot("episode:" + source.Partition.StorageKey + ":" + source.Id.ToString("D"), JsonSerializer.Serialize(new { retained.Payload, revision }, JsonOptions));
                            sources++; if (source.LegalHold) held++;
                        }
                        if (source.SourceFingerprint != reference.SourceFingerprint) throw new UnresolvedTransferRetention();
                        await Visit(source, depth + 1); contributors.Add(source);
                    }
                    var inheritedShared = MemorySharedAudiences.Merge(MemorySharedAudiences.FromSources(contributors)
                        .Concat(MemorySharedAudiences.IsCanonical(episode.Partition) ? [episode.Partition] : []));
                    if (inheritedShared.Length == 0 ? correction.RequiredSharedPartitions is not null :
                        correction.RequiredSharedPartitions is null || !inheritedShared.SequenceEqual(correction.RequiredSharedPartitions))
                        throw new UnresolvedTransferRetention();
                    foreach (var partition in inheritedShared) await Authorize(partition, receipt.EmployeeId);
                    return;
                }
                var evidence = episode.TransferEvidence ?? throw new UnresolvedTransferRetention();
                if (!MemorySourceIntegrity.IsVerified(episode) || evidence.PackageId == Guid.Empty || evidence.Records is null ||
                    evidence.Records.Count > MemoryTransferEvidence.MaximumRecords) throw new UnresolvedTransferRetention();
                packages.Add(evidence.PackageId);
                if (packages.Count > 32) throw new UnresolvedTransferRetention();
                string payload;
                await using (var command = Command("""
                    SELECT payload::text FROM csweet_memory_transfers WHERE id=@id AND octet_length(payload::text)<=33554432
                        AND NOT EXISTS(SELECT 1 FROM csweet_memory_partition_migration_rows WHERE table_name='csweet_memory_transfers'
                            AND record_id=CAST(@id AS text) AND disposition='Quarantine') FOR SHARE
                    """))
                {
                    command.Parameters.AddWithValue("id", evidence.PackageId);
                    payload = await command.ExecuteScalarAsync(token) as string ?? throw new UnresolvedTransferRetention();
                }
                Snapshot("package:" + evidence.PackageId.ToString("D"), payload);
                var package = JsonSerializer.Deserialize<KnowledgeTransferPackage>(payload, JsonOptions) ?? throw new UnresolvedTransferRetention();
                if (package.Id != evidence.PackageId || package.TenantId != organization.ToString("D") ||
                    package.Status is not (KnowledgeTransferStatus.Applied or KnowledgeTransferStatus.Rejected) ||
                    !Guid.TryParse(package.SourceEmployeeId, out var origin) || !Guid.TryParse(package.TargetEmployeeId, out var target) ||
                    origin == target || package.Items is not { Count: <= 32 } || !Enum.IsDefined(package.DebriefSensitivity) ||
                    string.IsNullOrWhiteSpace(package.ApprovedByEmployeeId) || package.ApprovedAt is null || package.ApprovedAt > clock.GetUtcNow() ||
                    package.SourceNamespaces is not { Count: 1 } || package.TargetNamespace is null ||
                    package.TargetNamespace.Audience is not (MemoryAudienceType.Employee or MemoryAudienceType.UserRelationship) ||
                    package.TargetNamespace.Partition != episode.Partition || package.TargetNamespace.Scope != episode.Scope ||
                    package.AppliedEpisodeId != episode.Id || package.AppliedAt is null ||
                    episode.Source != new MemorySource("knowledge-transfer", package.Id.ToString("D"), package.SourceEmployeeId) ||
                    episode.Content != MemoryTransferEvidence.RenderContent(package) ||
                    evidence.PackageFingerprint != MemoryTransferEvidence.Fingerprint(package) || package.ApprovedEvidence is null ||
                    JsonSerializer.Serialize(package.ApprovedEvidence, JsonOptions) != JsonSerializer.Serialize(evidence, JsonOptions))
                    throw new UnresolvedTransferRetention();
                await Authorize(package.TargetNamespace.Partition, target);
                await Authorize(package.SourceNamespaces[0].Partition, origin);
                CanonicalAudience(package.TargetNamespace, target);
                CanonicalAudience(package.SourceNamespaces[0], origin);
                foreach (var partition in MemorySharedAudiences.Merge(evidence.RequiredSharedPartitions ?? []))
                    await Authorize(partition, target);
                if (package.SourceNamespaces[0].Audience == MemoryAudienceType.UserRelationship &&
                    package.SourceNamespaces[0].Partition.UserId != package.TargetNamespace.Partition.UserId)
                    throw new UnresolvedTransferRetention();
                if (package.Items.Any(x => x is null || x.SourcePartition != package.SourceNamespaces[0].Partition ||
                    !Enum.IsDefined(x.Sensitivity) || !Enum.IsDefined(x.Trust))) throw new UnresolvedTransferRetention();
                var keys = new HashSet<(MemoryPartition, MemoryRecordKind, Guid)>();
                var inherited = new List<MemoryPartition>();
                foreach (var reference in evidence.Records.OrderBy(x => x?.Partition?.StorageKey, StringComparer.Ordinal).ThenBy(x => x?.Kind).ThenBy(x => x?.Id))
                {
                    if (reference is null || reference.Partition is null || reference.Id == Guid.Empty || reference.Revision <= 0 ||
                        reference.Kind is < MemoryRecordKind.Episode or > MemoryRecordKind.Procedure ||
                        reference.Partition != package.SourceNamespaces[0].Partition || !keys.Add((reference.Partition, reference.Kind, reference.Id)))
                        throw new UnresolvedTransferRetention();
                    if (reference.Kind != MemoryRecordKind.Episode) continue;
                    if (path.Contains(reference.Id)) throw new UnresolvedTransferRetention();
                    var key = (reference.Partition, reference.Id);
                    if (!episodes.TryGetValue(key, out var source))
                    {
                        if (episodes.Count >= MemoryTransferEvidence.MaximumRecords) throw new UnresolvedTransferRetention();
                        LegacyEpisode retained;
                        try { retained = await LockLegacyEpisodeAsync(reference.Id, token); }
                        catch (KeyNotFoundException) { throw new UnresolvedTransferRetention(); }
                        source = retained.Episode;
                        if (source.Partition != reference.Partition || !MemorySourceIntegrity.IsVerified(source)) throw new UnresolvedTransferRetention();
                        await Authorize(source.Partition, origin);
                        episodes.Add(key, source);
                        var revision = await RevisionAsync(source.Partition, source.Id, token, MemoryRecordKind.Episode);
                        Snapshot("episode:" + source.Partition.StorageKey + ":" + source.Id.ToString("D"),
                            JsonSerializer.Serialize(new { retained.Payload, revision }, JsonOptions));
                        sources++;
                        if (source.LegalHold) held++;
                        await Visit(source, depth + 1);
                    }
                    // Policy-only revisions may change, but the immutable source must
                    // still match the exact historical episode approved for this copy.
                    if (!approvedEpisodes.TryGetValue(reference, out var approvedSource))
                    {
                        string approvedPayload;
                        await using (var command = Command("""
                            SELECT payload::text FROM csweet_memory_revisions
                            WHERE partition_key=@partition AND kind=0 AND record_id=@id AND revision=@revision
                                AND octet_length(payload::text)<=33554432
                            """))
                        {
                            command.Parameters.AddWithValue("partition", reference.Partition.StorageKey);
                            command.Parameters.AddWithValue("id", reference.Id); command.Parameters.AddWithValue("revision", reference.Revision);
                            approvedPayload = await command.ExecuteScalarAsync(token) as string ?? throw new UnresolvedTransferRetention();
                        }
                        approvedSource = JsonSerializer.Deserialize<MemoryEpisode>(approvedPayload, JsonOptions) ?? throw new UnresolvedTransferRetention();
                        Snapshot("approved-episode:" + reference.Partition.StorageKey + ":" + reference.Id.ToString("D") + ":" + reference.Revision, approvedPayload);
                        approvedEpisodes.Add(reference, approvedSource);
                    }
                    if (approvedSource.Id != reference.Id || approvedSource.Partition != reference.Partition ||
                        !MemorySourceIntegrity.IsVerified(approvedSource) || approvedSource.SourceFingerprint != source.SourceFingerprint)
                        throw new UnresolvedTransferRetention();
                    inherited.AddRange(MemorySharedAudiences.Required(source) ?? []);
                }
                var expectedShared = MemorySharedAudiences.Merge(inherited.Concat(package.SourceNamespaces
                    .Where(x => x.Audience is MemoryAudienceType.Team or MemoryAudienceType.Role).Select(x => x.Partition)));
                if (expectedShared.Length == 0 ? evidence.RequiredSharedPartitions is not null :
                    evidence.RequiredSharedPartitions is null || !expectedShared.SequenceEqual(evidence.RequiredSharedPartitions))
                    throw new UnresolvedTransferRetention();
                if (evidence.LegalHold && !evidence.Records.Any(x => x.Kind == MemoryRecordKind.Episode)) throw new UnresolvedTransferRetention();
            }
            finally { path.Remove(episode.Id); }
        }
        try
        {
            await Authorize(root.Partition, employee);
            await Visit(root, 0);
            var sharedAuthority = await MemorySharedAudienceAuthorization.AuthorityHashAsync(db, organization,
                audiences.Select(x => x.Owner).Append(actor), audiences.Select(x => x.Partition).Where(MemorySharedAudiences.IsCanonical), token);
            if (sharedAuthority is not null) Snapshot("shared-authority", sharedAuthority);
            var snapshotBytes = JsonSerializer.SerializeToUtf8Bytes(snapshots, JsonOptions);
            if (snapshotBytes.Length > 32 * 1024 * 1024) throw new UnresolvedTransferRetention();
            var digest = Convert.ToHexString(SHA256.HashData(snapshotBytes)).ToLowerInvariant();
            return new(true, digest, sources, held, held > 0 ? "memory_transfer_upstream_held" : null,
                audiences.Select(x=>x.Partition).Distinct().OrderBy(x=>x.StorageKey,StringComparer.Ordinal).ToArray());
        }
        catch (Exception error) when (error is UnresolvedTransferRetention or JsonException or ArgumentException or NullReferenceException)
        {
            // Never convert authorization, lock contention or database failure into permission.
            return new(true, null, sources, held, "memory_transfer_retention_review_required");
        }
    }
}
