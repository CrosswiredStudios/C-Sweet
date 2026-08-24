using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Application.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.Agent.SDK;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

public sealed class PluginOperationsCapabilityHandler(
    CSweetDbContext db,
    IAuditEventWriter audit,
    IPluginStandingPolicyService standingPolicies,
    IConversationService conversations,
    AgentWorkInbox? workInbox = null) : IPlatformCapabilityHandler
{
    public const string ManagedAction = "platform.managed-action.execute.v1";
    public const string ManagedActionDecide = "platform.managed-action.decide.v1";
    public const string ManagedActionApprovalRequestedEvent = "com.csweet.managed-action.approval-requested.v1";
    public const string EngagementInbox = "platform.engagement-inbox.upsert.v1";
    public const string MetricSnapshot = "platform.metric-snapshot.write.v1";
    public const string SyncCheckpoint = "platform.synchronization-checkpoint.v1";
    public const string AgentOperatingStateRead = "platform.agent-operating-state.read.v1";
    public const string AgentOperatingStateWrite = "platform.agent-operating-state.write.v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlySet<string> ServerHardGateActions = new HashSet<string>(StringComparer.Ordinal)
    {
        "delete-permanently", "playlist-delete", "caption-delete", "ban-user", "go-live",
        "content-id-claim", "content-id-policy", "ownership-change", "monetization-change", "ad-change"
    };
    private static readonly IReadOnlySet<string> Capabilities = new HashSet<string>(StringComparer.Ordinal)
        { ManagedAction, ManagedActionDecide, EngagementInbox, MetricSnapshot, SyncCheckpoint,
            AgentOperatingStateRead, AgentOperatingStateWrite };

    public bool CanHandle(string capability) => Capabilities.Contains(capability);

    public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(session.BusinessId, out var organizationId) ||
            !Guid.TryParse(session.InstallationId, out var installationId))
        {
            yield return Failure(request.RequestId, "The installation identity is invalid.");
            yield break;
        }
        CapabilityResult result;
        try
        {
            result = request.Capability == AgentOperatingStateRead
                ? await ReadOperatingStateAsync(request, organizationId, installationId, cancellationToken)
                : request.Capability == AgentOperatingStateWrite
                    ? await WriteOperatingStateAsync(request, organizationId, installationId, cancellationToken)
                : request.Capability == ManagedAction
                    ? await HandleManagedActionAsync(request, organizationId, installationId, cancellationToken)
                : request.Capability == ManagedActionDecide
                    ? await HandleManagedActionDecisionAsync(request, organizationId, installationId, cancellationToken)
                : request.Capability == EngagementInbox
                    ? await HandleEngagementAsync(request, organizationId, installationId, cancellationToken)
                    : await HandleStateAsync(request, organizationId, installationId, cancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            result = Failure(request.RequestId, exception.Message);
        }
        yield return result;
    }

    private async Task<CapabilityResult> ReadOperatingStateAsync(
        RequestCapability request,
        Guid organizationId,
        Guid installationId,
        CancellationToken cancellationToken)
    {
        var input = JsonSerializer.Deserialize<AgentOperatingStateReadRequest>(request.Payload.Span, JsonOptions)
            ?? throw new JsonException("The operating-state read payload is empty.");
        ValidateStateKey(input.StateKey);
        var state = await db.PluginOperationalStates.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.AgentInstallationId == installationId &&
            x.Kind == "agent-operating-state" && x.ExternalKey == input.StateKey, cancellationToken);
        return Success(request.RequestId, new AgentOperatingStateReadResponse(
            state is null ? null : ToOperatingStateResponse(state)));
    }

    private async Task<CapabilityResult> WriteOperatingStateAsync(
        RequestCapability request,
        Guid organizationId,
        Guid installationId,
        CancellationToken cancellationToken)
    {
        var input = JsonSerializer.Deserialize<AgentOperatingStateWriteRequest>(request.Payload.Span, JsonOptions)
            ?? throw new JsonException("The operating-state write payload is empty.");
        ValidateOperatingState(input);
        var state = await db.PluginOperationalStates.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.AgentInstallationId == installationId &&
            x.Kind == "agent-operating-state" && x.ExternalKey == input.StateKey, cancellationToken);
        if (state is not null)
        {
            var stored = JsonSerializer.Deserialize<StoredAgentOperatingState>(state.PayloadJson, JsonOptions);
            if (stored is not null && string.Equals(stored.IdempotencyKey, input.IdempotencyKey, StringComparison.Ordinal))
                return Success(request.RequestId, ToOperatingStateResponse(state));
            if (input.ExpectedRevision != state.Revision)
                return Failure(request.RequestId, PlatformCapabilityErrorCode.Conflict,
                    $"Expected operating-state revision {input.ExpectedRevision?.ToString() ?? "none"}; current revision is {state.Revision}.");
        }
        else if (input.ExpectedRevision is not null and not 0)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Conflict,
                "The operating-state record does not exist at the expected revision.");
        }

        var now = DateTimeOffset.UtcNow;
        if (state is null)
        {
            state = new PluginOperationalState
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, AgentInstallationId = installationId,
                Kind = "agent-operating-state", ExternalKey = input.StateKey, CreatedAt = now
            };
            db.PluginOperationalStates.Add(state);
        }
        state.Revision++;
        state.PayloadJson = JsonSerializer.Serialize(new StoredAgentOperatingState(
            input.SchemaId, input.SchemaVersion, input.Status, input.SourceRevisions,
            input.ConditionCodes, input.DecisionFingerprint, input.OpenCommitmentCorrelations,
            input.AttentionReviewId, input.Payload.Clone(), input.IdempotencyKey), JsonOptions);
        state.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Conflict,
                "The operating-state record changed during this review. Reread authoritative state and reassess.");
        }
        catch (DbUpdateException)
        {
            // Concurrent first writers can race on the scoped unique key. Treat that
            // the same as a compare-and-swap conflict instead of leaking persistence details.
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Conflict,
                "The operating-state record was created by another review. Reread authoritative state and reassess.");
        }
        await audit.WriteAsync("agent.operating-state.updated", nameof(PluginOperationalState), state.Id,
            $"Updated operating state '{state.ExternalKey}' to revision {state.Revision}.", cancellationToken: cancellationToken);
        return Success(request.RequestId, ToOperatingStateResponse(state));
    }

    private static AgentOperatingStateResponse ToOperatingStateResponse(PluginOperationalState state)
    {
        var stored = JsonSerializer.Deserialize<StoredAgentOperatingState>(state.PayloadJson, JsonOptions)
            ?? throw new InvalidOperationException("The persisted operating state is invalid.");
        return new AgentOperatingStateResponse(
            state.Id, state.ExternalKey, stored.SchemaId, stored.SchemaVersion, stored.Status,
            stored.SourceRevisions, stored.ConditionCodes, stored.DecisionFingerprint,
            stored.OpenCommitmentCorrelations, stored.AttentionReviewId, stored.Payload,
            state.Revision, state.CreatedAt, state.UpdatedAt);
    }

    private static void ValidateOperatingState(AgentOperatingStateWriteRequest input)
    {
        ValidateStateKey(input.StateKey);
        if (string.IsNullOrWhiteSpace(input.SchemaId) || input.SchemaId.Length > 160 || input.SchemaVersion < 1)
            throw new InvalidOperationException("Operating state requires a bounded schema ID and positive schema version.");
        if (string.IsNullOrWhiteSpace(input.Status) || input.Status.Length > 80 ||
            string.IsNullOrWhiteSpace(input.DecisionFingerprint) || input.DecisionFingerprint.Length > 128 ||
            string.IsNullOrWhiteSpace(input.IdempotencyKey) || input.IdempotencyKey.Length > 160)
            throw new InvalidOperationException("Operating-state status, fingerprint, and idempotency key are required and bounded.");
        if (input.SourceRevisions.Count > 32 || input.SourceRevisions.Any(x =>
                string.IsNullOrWhiteSpace(x.Key) || x.Key.Length > 80 || x.Value.Length > 160) ||
            input.ConditionCodes.Count > 32 || input.ConditionCodes.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 80) ||
            input.OpenCommitmentCorrelations.Count > 32 || input.OpenCommitmentCorrelations.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 200) ||
            input.Payload.GetRawText().Length > 65_536)
            throw new InvalidOperationException("Operating-state revisions, conditions, commitments, or payload exceed platform bounds.");
    }

    private static void ValidateStateKey(string stateKey)
    {
        if (string.IsNullOrWhiteSpace(stateKey) || stateKey.Length > 160 ||
            stateKey.Any(x => !(char.IsLetterOrDigit(x) || x is '.' or '-' or '_' or '/' or ':')))
            throw new InvalidOperationException("Operating-state key is invalid.");
    }

    private async Task<CapabilityResult> HandleManagedActionAsync(RequestCapability request, Guid organizationId,
        Guid installationId, CancellationToken cancellationToken)
    {
        var input = JsonSerializer.Deserialize<ManagedActionInput>(request.Payload.Span, JsonOptions)
            ?? throw new JsonException("The managed action payload is empty.");
        if (!Guid.TryParse(input.InstallationId, out var claimedInstallation) || claimedInstallation != installationId ||
            string.IsNullOrWhiteSpace(input.ChannelId) || string.IsNullOrWhiteSpace(input.ActionType) ||
            string.IsNullOrWhiteSpace(input.PayloadHash) || string.IsNullOrWhiteSpace(input.IdempotencyKey) ||
            input.ResourceId?.Length > 512)
            throw new InvalidOperationException("The managed action binding is incomplete.");
        var boundChannel = await db.PluginConnections.AsNoTracking().AnyAsync(x =>
            x.AgentInstallationId == installationId && x.Status == PluginConnectionStatus.Connected &&
            x.BoundResourceId == input.ChannelId, cancellationToken);
        if (!boundChannel)
            throw new InvalidOperationException("The managed action is not bound to this installation's confirmed channel.");
        var canonicalHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.Payload.GetRawText()))).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(canonicalHash), Encoding.ASCII.GetBytes(input.PayloadHash.ToLowerInvariant())))
            throw new InvalidOperationException("The managed action payload hash is invalid.");
        var policyDecision = await standingPolicies.EvaluateAsync(new ManagedActionPolicyInput(
            organizationId, installationId, input.ChannelId, input.ActionType, input.Payload,
            canonicalHash, input.IdempotencyKey), cancellationToken);
        if (policyDecision.Authorized)
            return Success(request.RequestId, new
            {
                status = "AuthorizedByStandingPolicy", authorized = true,
                policyId = policyDecision.PolicyId, policyRevision = policyDecision.PolicyRevision
            });
        var existing = await db.ActionProposals.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.AgentInstallationId == installationId &&
            x.IdempotencyKey == input.IdempotencyKey, cancellationToken);
        var stored = JsonSerializer.Serialize(input, JsonOptions);
        if (existing is not null)
        {
            var prior = JsonSerializer.Deserialize<ManagedActionInput>(existing.PayloadJson, JsonOptions)
                ?? throw new InvalidOperationException("The existing managed action is invalid.");
            if (prior.ChannelId != input.ChannelId || prior.ActionType != input.ActionType ||
                prior.PayloadHash != input.PayloadHash || prior.ExpectedRevision != input.ExpectedRevision ||
                prior.ResourceId != input.ResourceId)
                throw new InvalidOperationException("The idempotency key is bound to different managed action content.");
            return Success(request.RequestId, new
            {
                status = existing.Status.ToString(), approvalId = existing.Id.ToString("D"),
                authorized = existing.Status == ProposalStatus.Approved
            });
        }
        var proposal = new ActionProposal
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, AgentInstallationId = installationId,
            ActionType = $"youtube.{input.ActionType}", Summary = $"{input.ActionType} on confirmed channel {input.ChannelId}",
            PayloadJson = stored,
            RiskClass = input.AlwaysRequiresApproval || ServerHardGateActions.Contains(input.ActionType)
                ? "AlwaysApproval" : "PublicMutation",
            IdempotencyKey = input.IdempotencyKey, CreatedAt = DateTimeOffset.UtcNow
        };
        db.ActionProposals.Add(proposal);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("managed-action.proposed", nameof(ActionProposal), proposal.Id,
            proposal.Summary, JsonSerializer.Serialize(new { organizationId, installationId, input.ChannelId,
                input.ActionType, input.ResourceId, input.PayloadHash, input.ExpectedRevision, input.IdempotencyKey }), cancellationToken);
        await NotifyAgentApproverAsync(proposal, input, cancellationToken);
        return Success(request.RequestId, new { status = "Pending", approvalId = proposal.Id.ToString("D"), authorized = false });
    }

    private async Task<CapabilityResult> HandleManagedActionDecisionAsync(RequestCapability request,
        Guid organizationId, Guid approverInstallationId, CancellationToken cancellationToken)
    {
        var input = JsonSerializer.Deserialize<ManagedActionDecisionInput>(request.Payload.Span, JsonOptions)
            ?? throw new JsonException("The managed action decision payload is empty.");
        if (input.ProposalId == Guid.Empty || string.IsNullOrWhiteSpace(input.DecisionIdempotencyKey) ||
            input.DecisionIdempotencyKey.Length > 160 || input.ResourceId?.Length > 512 ||
            input.Decision is not ("Approve" or "Request revision" or "Reject"))
            throw new InvalidOperationException("The managed action decision is invalid.");
        var approver = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.AgentInstallationId == approverInstallationId && x.IsActive,
            cancellationToken) ?? throw new InvalidOperationException("The approver agent employee was not found.");
        var proposal = await db.ActionProposals.SingleOrDefaultAsync(x =>
            x.Id == input.ProposalId && x.OrganizationId == organizationId, cancellationToken)
            ?? throw new InvalidOperationException("The managed action proposal was not found.");
        var requestingAgent = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.AgentInstallationId == proposal.AgentInstallationId && x.IsActive,
            cancellationToken);
        var configurationJson = await db.AgentInstallationConfigurations.AsNoTracking()
            .Where(x => x.AgentInstallationId == proposal.AgentInstallationId).Select(x => x.SettingsJson)
            .SingleOrDefaultAsync(cancellationToken);
        var approvalMode = ReadApprovalMode(configurationJson);
        var authorized = approvalMode == "Manager Approval"
            ? requestingAgent?.ReportsToOrganizationUserId == approver.Id
            : approver.PermissionLevel == OrganizationPermissionLevel.Owner;
        if (!authorized) throw new InvalidOperationException("This agent is not the proposal's assigned approver.");
        using var storedPayload = JsonDocument.Parse(proposal.PayloadJson);
        var root = storedPayload.RootElement;
        var payloadHash = root.GetProperty("payloadHash").GetString();
        var actionKey = root.GetProperty("idempotencyKey").GetString();
        var resourceId = root.TryGetProperty("resourceId", out var resourceNode) &&
            resourceNode.ValueKind == JsonValueKind.String ? resourceNode.GetString() : null;
        var revision = root.TryGetProperty("expectedRevision", out var revisionNode) &&
            revisionNode.ValueKind == JsonValueKind.Number ? revisionNode.GetInt64() : (long?)null;
        if (!string.Equals(payloadHash, input.PayloadHash, StringComparison.Ordinal) ||
            !string.Equals(actionKey, input.ActionIdempotencyKey, StringComparison.Ordinal) ||
            !string.Equals(resourceId, input.ResourceId, StringComparison.Ordinal) || revision != input.ExpectedRevision)
            throw new InvalidOperationException("The managed action changed after it was reviewed.");
        var receipt = await db.PluginOperationalStates.SingleOrDefaultAsync(x =>
            x.AgentInstallationId == proposal.AgentInstallationId && x.Kind == "managed-action-decision" &&
            x.ExternalKey == input.DecisionIdempotencyKey, cancellationToken);
        var receiptJson = JsonSerializer.Serialize(new { input.ProposalId, input.Decision, input.PayloadHash,
            input.ResourceId, input.ExpectedRevision, input.ActionIdempotencyKey }, JsonOptions);
        if (receipt is not null)
        {
            if (!string.Equals(receipt.PayloadJson, receiptJson, StringComparison.Ordinal))
                throw new InvalidOperationException("The decision idempotency key is bound to different content.");
            return Success(request.RequestId, new { proposal.Id, status = proposal.Status.ToString(), idempotent = true });
        }
        if (proposal.Status != ProposalStatus.Pending)
            throw new InvalidOperationException("The managed action proposal is no longer pending.");
        proposal.Status = input.Decision switch
        {
            "Approve" => ProposalStatus.Approved,
            "Reject" => ProposalStatus.Rejected,
            _ => ProposalStatus.Cancelled
        };
        proposal.DecidedAt = DateTimeOffset.UtcNow;
        db.PluginOperationalStates.Add(new PluginOperationalState
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId,
            AgentInstallationId = proposal.AgentInstallationId, Kind = "managed-action-decision",
            ExternalKey = input.DecisionIdempotencyKey, PayloadJson = receiptJson,
            CreatedAt = proposal.DecidedAt.Value, UpdatedAt = proposal.DecidedAt.Value
        });
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("managed-action.agent-decided", nameof(ActionProposal), proposal.Id,
            $"Approver installation {approverInstallationId:D} recorded {input.Decision}.",
            JsonSerializer.Serialize(new { organizationId, approverInstallationId, input.PayloadHash,
                input.ResourceId, input.ExpectedRevision, input.ActionIdempotencyKey,
                input.DecisionIdempotencyKey }, JsonOptions), cancellationToken);
        return Success(request.RequestId, new { proposal.Id, status = proposal.Status.ToString(), idempotent = false });
    }

    private async Task NotifyAgentApproverAsync(ActionProposal proposal, ManagedActionInput input,
        CancellationToken cancellationToken)
    {
        if (workInbox is null) return;
        var requestingAgent = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == proposal.OrganizationId && x.AgentInstallationId == proposal.AgentInstallationId &&
            x.IsActive, cancellationToken);
        var configurationJson = await db.AgentInstallationConfigurations.AsNoTracking()
            .Where(x => x.AgentInstallationId == proposal.AgentInstallationId).Select(x => x.SettingsJson)
            .SingleOrDefaultAsync(cancellationToken);
        var approvalMode = ReadApprovalMode(configurationJson);
        OrganizationUser? approver = null;
        if (approvalMode == "Manager Approval" && requestingAgent?.ReportsToOrganizationUserId is { } managerId)
            approver = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == managerId && x.OrganizationId == proposal.OrganizationId && x.IsActive,
                cancellationToken);
        else if (approvalMode != "Manager Approval")
            approver = await db.CoreOrganizationUsers.AsNoTracking().Where(x =>
                    x.OrganizationId == proposal.OrganizationId && x.IsActive &&
                    x.PermissionLevel == OrganizationPermissionLevel.Owner)
                .OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (approver?.EmployeeType != EmployeeType.Agent || approver.AgentInstallationId is not { } targetInstallationId)
            return;
        var payload = JsonSerializer.SerializeToElement(new
        {
            proposalId = proposal.Id, proposal.ActionType, input.ChannelId, input.ResourceId,
            input.PayloadHash, input.ExpectedRevision, actionIdempotencyKey = input.IdempotencyKey,
            decisionCapability = ManagedActionDecide
        }, JsonOptions);
        try
        {
            await workInbox.EnqueueAsync(proposal.OrganizationId.ToString("D"), targetInstallationId,
                CSweet.Domain.Setup.AgentWorkKind.Event, ManagedActionApprovalRequestedEvent, payload,
                $"managed-action-approval:{proposal.Id:N}", DateTimeOffset.UtcNow.AddDays(7),
                sourceType: "managed-action", sourceId: proposal.Id.ToString("D"),
                cancellationToken: cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            await audit.WriteAsync("managed-action.agent-approver-unavailable", nameof(ActionProposal), proposal.Id,
                "The assigned agent approver could not receive its durable approval event.",
                JsonSerializer.Serialize(new { targetInstallationId, reason = exception.Message }, JsonOptions),
                cancellationToken);
        }
    }

    private static string ReadApprovalMode(string? configurationJson)
    {
        try
        {
            using var configuration = JsonDocument.Parse(configurationJson ?? "{}");
            return configuration.RootElement.TryGetProperty("approvalMode", out var value)
                ? value.GetString() ?? "Manager Approval" : "Manager Approval";
        }
        catch (JsonException) { return "Manager Approval"; }
    }

    private async Task<CapabilityResult> HandleStateAsync(RequestCapability request, Guid organizationId,
        Guid installationId, CancellationToken cancellationToken)
    {
        var payload = request.Payload.ToElement();
        var kind = request.Capability switch
        {
            EngagementInbox => "engagement",
            MetricSnapshot => "metric",
            _ => "checkpoint"
        };
        var externalKey = kind == "checkpoint" && payload.TryGetProperty("source", out var source)
            ? source.GetString() ?? "default"
            : Convert.ToHexString(SHA256.HashData(request.Payload.Span)).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        var state = await db.PluginOperationalStates.SingleOrDefaultAsync(x =>
            x.AgentInstallationId == installationId && x.Kind == kind && x.ExternalKey == externalKey,
            cancellationToken);
        if (state is null)
        {
            state = new PluginOperationalState { Id = Guid.NewGuid(), OrganizationId = organizationId,
                AgentInstallationId = installationId, Kind = kind, ExternalKey = externalKey, CreatedAt = now };
            db.PluginOperationalStates.Add(state);
        }
        state.PayloadJson = payload.GetRawText();
        state.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return Success(request.RequestId, new { persisted = true, stateId = state.Id, externalKey, updatedAt = now });
    }

    private async Task<CapabilityResult> HandleEngagementAsync(RequestCapability request, Guid organizationId,
        Guid installationId, CancellationToken cancellationToken)
    {
        var payload = request.Payload.ToElement();
        var channelId = payload.TryGetProperty("channelId", out var channel) ? channel.GetString() : null;
        if (string.IsNullOrWhiteSpace(channelId) || !payload.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Engagement records require a channel and items array.");
        if (!await db.PluginConnections.AsNoTracking().AnyAsync(x =>
                x.AgentInstallationId == installationId && x.Status == PluginConnectionStatus.Connected &&
                x.BoundResourceId == channelId, cancellationToken))
            throw new InvalidOperationException("Engagement records are not bound to the confirmed channel.");
        var count = 0;
        var now = DateTimeOffset.UtcNow;
        var urgentExcerpts = new List<string>();
        foreach (var item in items.EnumerateArray())
        {
            var externalId = item.TryGetProperty("externalId", out var external) ? external.GetString() : null;
            if (string.IsNullOrWhiteSpace(externalId) || externalId.Length > 160)
                throw new InvalidOperationException("Each engagement record requires a bounded external ID.");
            var externalKey = $"{channelId}:{externalId}";
            var state = await db.PluginOperationalStates.SingleOrDefaultAsync(x =>
                x.AgentInstallationId == installationId && x.Kind == "engagement" && x.ExternalKey == externalKey,
                cancellationToken);
            if (state is null)
            {
                state = new PluginOperationalState
                {
                    Id = Guid.NewGuid(), OrganizationId = organizationId, AgentInstallationId = installationId,
                    Kind = "engagement", ExternalKey = externalKey, CreatedAt = now
                };
                db.PluginOperationalStates.Add(state);
                if (item.TryGetProperty("urgent", out var urgent) && urgent.ValueKind == JsonValueKind.True &&
                    item.TryGetProperty("excerpt", out var excerpt) && excerpt.ValueKind == JsonValueKind.String &&
                    excerpt.GetString() is { } text)
                    urgentExcerpts.Add(SanitizeNotificationText(text, 240));
            }
            state.PayloadJson = item.GetRawText();
            state.UpdatedAt = now;
            count++;
        }
        await db.SaveChangesAsync(cancellationToken);
        var conversationId = await FindProtectedConversationAsync(installationId, cancellationToken);
        if (conversationId.HasValue && urgentExcerpts.Count > 0)
        {
            var lines = urgentExcerpts.Take(5).Select(x => $"- {x}");
            await conversations.AppendMessageAsync(conversationId.Value, ConversationRole.Assistant,
                $"Potentially urgent YouTube engagement needs review:\n{string.Join("\n", lines)}",
                cancellationToken);
        }
        if (conversationId.HasValue && payload.TryGetProperty("digest", out var digest) &&
            digest.ValueKind == JsonValueKind.Object)
        {
            var digestKey = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
            var priorDigest = await db.PluginOperationalStates.AnyAsync(x =>
                x.AgentInstallationId == installationId && x.Kind == "engagement-digest" &&
                x.ExternalKey == digestKey, cancellationToken);
            if (!priorDigest)
            {
                var total = digest.TryGetProperty("total", out var totalNode) && totalNode.TryGetInt32(out var totalValue)
                    ? Math.Clamp(totalValue, 0, 100_000) : 0;
                var urgent = digest.TryGetProperty("urgent", out var urgentNode) && urgentNode.TryGetInt32(out var urgentValue)
                    ? Math.Clamp(urgentValue, 0, total) : 0;
                db.PluginOperationalStates.Add(new PluginOperationalState
                {
                    Id = Guid.NewGuid(), OrganizationId = organizationId, AgentInstallationId = installationId,
                    Kind = "engagement-digest", ExternalKey = digestKey,
                    PayloadJson = JsonSerializer.Serialize(new { total, urgent }, JsonOptions),
                    CreatedAt = now, UpdatedAt = now
                });
                await db.SaveChangesAsync(cancellationToken);
                await conversations.AppendMessageAsync(conversationId.Value, ConversationRole.Assistant,
                    $"YouTube daily engagement digest: {total} recent comments synchronized; " +
                    $"{urgent} flagged for prompt human review. Reply drafts remain approval-governed.", cancellationToken);
            }
        }
        return Success(request.RequestId, new { persisted = true, count, updatedAt = now });
    }

    private Task<Guid?> FindProtectedConversationAsync(Guid installationId, CancellationToken cancellationToken) =>
        (from onboarding in db.AgentOnboardingEventOutbox.AsNoTracking()
         join employee in db.CoreOrganizationUsers.AsNoTracking()
             on onboarding.AgentOrganizationUserId equals employee.Id
         where employee.AgentInstallationId == installationId
         orderby onboarding.OccurredAt descending
         select (Guid?)onboarding.ConversationId).FirstOrDefaultAsync(cancellationToken);

    private static string SanitizeNotificationText(string value, int maximumLength)
    {
        var clean = new string(value.Where(x => !char.IsControl(x) || x is '\r' or '\n' or '\t').ToArray()).Trim();
        return clean.Length <= maximumLength ? clean : clean[..maximumLength] + "…";
    }

    private static CapabilityResult Success<T>(string requestId, T value) => new()
    {
        RequestId = requestId, Succeeded = true, ContentType = "application/json",
        Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions))
    };
    private static CapabilityResult Failure(string requestId, string error) => new()
    {
        RequestId = requestId, Succeeded = false, ContentType = "application/json", Error = error,
        Payload = JsonPayload.FromUtf8("{\"isError\":true}")
    };
    private static CapabilityResult Failure(string requestId, PlatformCapabilityErrorCode code, string error) => new()
    {
        RequestId = requestId, Succeeded = false, ContentType = "application/json", Error = error,
        FailureCode = code.ToString(), Payload = JsonPayload.FromUtf8("{\"isError\":true}")
    };
    private sealed record StoredAgentOperatingState(
        string SchemaId,
        int SchemaVersion,
        string Status,
        IReadOnlyDictionary<string, string> SourceRevisions,
        IReadOnlyList<string> ConditionCodes,
        string DecisionFingerprint,
        IReadOnlyList<string> OpenCommitmentCorrelations,
        Guid AttentionReviewId,
        JsonElement Payload,
        string IdempotencyKey);
    private sealed record ManagedActionInput(string InstallationId, string ChannelId, string ActionType,
        JsonElement Payload, string PayloadHash, string IdempotencyKey, string? ApprovalId,
        long? ExpectedRevision, bool AlwaysRequiresApproval, string? ResourceId = null);
    private sealed record ManagedActionDecisionInput(Guid ProposalId, string Decision, string? Comment,
        string PayloadHash, long? ExpectedRevision, string ActionIdempotencyKey,
        string DecisionIdempotencyKey, string? ResourceId = null);
}
