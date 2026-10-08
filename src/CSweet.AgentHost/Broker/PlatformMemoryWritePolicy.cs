using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Memory;

namespace CSweet.AgentHost.Broker;

/// <summary>Agent wire records are proposals, never evidence of user or application authority.</summary>
internal static class PlatformMemoryWritePolicy
{
    internal static MemoryEpisode Episode(AgentSession session, MemoryEpisode episode, DateTimeOffset now)
    {
        ValidateId(episode.Id);
        if (episode.TransferEvidence is not null) throw ReviewRequired();
        ValidateSensitivity(episode.Sensitivity);
        ArgumentException.ThrowIfNullOrWhiteSpace(episode.Content);
        if (episode.ExpiresAt <= episode.OccurredAt) throw new ArgumentException("Invalid episode validity window.");
        var scope = PlatformMemoryNamespacePolicy.Resolve(session, episode.Partition, PlatformMemoryAction.Propose);
        var clientKey = string.IsNullOrWhiteSpace(episode.IdempotencyKey)
            ? $"id:{episode.Id:D}" : $"key:{episode.IdempotencyKey}";
        var proposalId = ProposalId(session, episode.Partition, "episode", clientKey);
        return episode with
        {
            Id = proposalId,
            Scope = scope.Scope,
            Source = new MemorySource("agent-proposal", proposalId.ToString("D"), session.MemoryEmployeeId),
            // Agent-authored evidence is private by default; a Public label cannot publish it
            // into an organization/team/role recall channel with an Internal ceiling.
            Sensitivity = MemoryProvenance.Maximum(episode.Sensitivity, MemorySensitivity.Personal),
            Checksum = Hash(episode.Content),
            RecordedAt = now,
            // A proposal cannot reserve a trusted conversation-capture idempotency key.
            IdempotencyKey = $"agent-proposal:{session.InstallationId}:{Hash(clientKey)}",
            LegalHold = false,
            Metadata = new Dictionary<string, string>
            {
                ["installationId"] = session.InstallationId,
                ["employeeId"] = session.MemoryEmployeeId!
            },
            OperationalReferences = null
        };
    }

    internal static MemoryClaim Claim(AgentSession session, MemoryClaim claim, DateTimeOffset now)
    {
        ValidateId(claim.Id);
        ValidateSensitivity(claim.Sensitivity);
        ValidateWindow(claim.ValidFrom, claim.ValidTo);
        ArgumentException.ThrowIfNullOrWhiteSpace(claim.Predicate);
        if (!Enum.IsDefined(claim.Kind) || !double.IsFinite(claim.Confidence) || !double.IsFinite(claim.Importance) ||
            claim.Confidence is < 0 or > 1 || claim.Importance is < 0 or > 1) throw new ArgumentException("Invalid claim proposal.");
        if (claim.SupersedesClaimId is not null) throw ReviewRequired();
        return claim with
        {
            Id = ProposalId(session, claim.Partition, "claim", claim.Id.ToString("D")),
            Trust = MemoryTrustTier.AgentInference,
            Confirmation = MemoryConfirmationState.Pending,
            RecordedAt = now,
            ExtractorVersion = "platform-agent-proposal-v1"
        };
    }

    internal static ProceduralMemory Procedure(AgentSession session, ProceduralMemory procedure, DateTimeOffset now)
    {
        ValidateId(procedure.Id);
        ValidateWindow(procedure.ValidFrom, procedure.ValidTo);
        ArgumentException.ThrowIfNullOrWhiteSpace(procedure.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(procedure.Procedure);
        return procedure with { Id = ProposalId(session, procedure.Partition, "procedure", procedure.Id.ToString("D")),
            Trust = MemoryTrustTier.AgentInference, Confirmation = MemoryConfirmationState.Pending, RecordedAt = now };
    }

    internal static MemoryUse Use(AgentSession session, MemoryUse use, DateTimeOffset now)
    {
        ValidateId(use.Id);
        ValidateId(use.MemoryId);
        if (!Enum.IsDefined(use.Layer) || use.Outcome != MemoryUseOutcome.Supplied) throw ReviewRequired();
        // The current protocol has no server-issued citation receipt. Keep usage explicitly
        // agent-reported, bound to this invocation, and unable to assert human acceptance.
        return use with { InvocationId = $"agent-reported:{session.InstallationId}:{session.TickId}", RecordedAt = now };
    }

    internal static UnauthorizedAccessException ReviewRequired() => new(
        "This memory operation requires a trusted review workflow; an agent capability grant does not authorize it.");

    private static void ValidateId(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A memory record ID is required.");
    }

    private static void ValidateSensitivity(MemorySensitivity sensitivity)
    {
        if (!Enum.IsDefined(sensitivity)) throw new ArgumentException("Unknown memory sensitivity.");
    }

    private static void ValidateWindow(DateTimeOffset from, DateTimeOffset? to)
    {
        if (to <= from) throw new ArgumentException("Invalid memory validity window.");
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static Guid ProposalId(AgentSession session, MemoryPartition partition, string kind, string key)
    {
        // Serialize fields instead of using Partition.Key, whose legacy delimiter format aliases.
        var identity = JsonSerializer.Serialize(new { Domain = "csweet.agent-memory.proposal.v1", session.InstallationId,
            Partition = new { partition.TenantId, partition.ApplicationId, partition.AgentId, partition.UserId,
                partition.ConversationId, partition.CustomNamespace }, Kind = kind, Key = key });
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 16));
    }
}
