using System.Text.Json;
using CSweet.Memory;
using CSweet.Infrastructure.Core;

namespace CSweet.AgentHost.Broker;

public sealed class PlatformMemoryCapabilityHandler
{
    private const int MaximumRequestBytes = 1_048_576;
    private const int MaximumResponseBytes = 4_194_304;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new MemoryLayerSetConverter() }
    };
    private readonly IMemoryStore _store;
    private readonly IKnowledgeTransferStore _transfers;
    private readonly ILogger<PlatformMemoryCapabilityHandler> _logger;
    private readonly IAgentMemoryIdentityResolver? _identityResolver;
    private readonly IPlatformMemoryReadEvidence? _readEvidence;
    private readonly IAgentMemoryIngestion? _ingestion;

    public PlatformMemoryCapabilityHandler(IMemoryStore store, ILogger<PlatformMemoryCapabilityHandler> logger)
        : this(store, logger, null)
    {
    }

    public PlatformMemoryCapabilityHandler(
        IMemoryStore store,
        ILogger<PlatformMemoryCapabilityHandler> logger,
        IAgentMemoryIdentityResolver? identityResolver,
        IPlatformMemoryReadEvidence? readEvidence = null,
        IAgentMemoryIngestion? ingestion = null)
    {
        _store = store;
        _transfers = store as IKnowledgeTransferStore
            ?? throw new InvalidOperationException("The platform memory store must support knowledge transfer.");
        _logger = logger;
        _identityResolver = identityResolver;
        _readEvidence = readEvidence;
        _ingestion = ingestion;
    }

    public static bool IsPlatformMemoryCapability(string capability) => capability is
        CSweetMemoryCapabilities.Query or CSweetMemoryCapabilities.Write or
        CSweetMemoryCapabilities.Manage or CSweetMemoryCapabilities.Export;

    public async Task<CapabilityResult> HandleAsync(
        AgentSession session,
        RequestCapability request,
        CancellationToken cancellationToken)
    {
        if (request.Payload.Length > MaximumRequestBytes)
            return Failure(request.RequestId, "The memory request exceeds the 1 MB limit.");
        if (!string.Equals(request.ContentType, "application/json", StringComparison.OrdinalIgnoreCase))
            return Failure(request.RequestId, "Platform memory requests must use application/json.");
        if (!session.Grant.RequestedCapabilities.Contains(request.Capability))
            return Failure(request.RequestId, $"The installation is not granted {request.Capability}.");

        try
        {
            var command = JsonSerializer.Deserialize<CSweetMemoryCommand>(request.Payload.Span, JsonOptions)
                ?? throw new JsonException("The memory command is empty.");
            if (_identityResolver is null)
                throw new UnauthorizedAccessException("Server memory identity resolution is required.");
            var identity = await _identityResolver.ResolveAsync(session, cancellationToken)
                ?? throw new UnauthorizedAccessException("The installation is not linked to one active agent employee.");
            session.MemoryTenantId = identity.TenantId;
            session.MemoryEmployeeId = identity.EmployeeId;
            MemoryReadInvocation? invocation = null;
            if (request.Capability is CSweetMemoryCapabilities.Query or CSweetMemoryCapabilities.Export)
                invocation = await (_readEvidence ?? throw new UnauthorizedAccessException("Memory read evidence tracking is required."))
                    .BeginAsync(session, request.Capability, cancellationToken);
            await _store.InitializeAsync(cancellationToken);
            var result = request.Capability switch
            {
                CSweetMemoryCapabilities.Query => await HandleQueryAsync(session, command, cancellationToken),
                CSweetMemoryCapabilities.Write => await HandleWriteAsync(session, command, cancellationToken),
                CSweetMemoryCapabilities.Manage => await HandleManageAsync(session, command, cancellationToken),
                CSweetMemoryCapabilities.Export => await HandleExportAsync(session, command, cancellationToken),
                _ => throw new InvalidOperationException("Unsupported platform memory capability.")
            };
            var payload = JsonSerializer.SerializeToUtf8Bytes(result, result?.GetType() ?? typeof(object), JsonOptions);
            if (payload.Length > MaximumResponseBytes)
                return Failure(request.RequestId, "The memory response exceeds the 4 MB limit; narrow the requested scope.");
            if (invocation is not null)
                await _readEvidence!.RecordAsync(session, request.Capability, invocation, result,
                    command.Operation == "search" ? Read<MemorySearchRequest>(command).Partition : null, cancellationToken);
            return Success(request.RequestId, payload);
        }
        catch (JsonException exception)
        {
            return Failure(request.RequestId, $"The memory request is invalid: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogWarning("Denied memory operation {RequestId} from agent {AgentId}: {Reason}", request.RequestId, session.AgentId, exception.Message);
            return Failure(request.RequestId, exception.Message, "memory_policy_denied", false);
        }
        catch (KeyNotFoundException)
        {
            return Failure(request.RequestId, "The requested memory record was not found.");
        }
        catch (InvalidOperationException exception) when (exception.Message == "memory_write_conflict")
        {
            return Failure(request.RequestId, "That memory write ID already has different content or review state.", "memory_write_conflict", false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning(exception, "Memory operation {RequestId} failed for agent {AgentId}.", request.RequestId, session.AgentId);
            return Failure(request.RequestId, "The platform could not complete the memory operation.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Platform memory is unavailable for request {RequestId} from agent {AgentId}.", request.RequestId, session.AgentId);
            return Failure(request.RequestId, "Long-term memory is temporarily unavailable; the agent can continue without recalled memory.");
        }
    }

    private async Task<object?> HandleQueryAsync(AgentSession session, CSweetMemoryCommand command, CancellationToken cancellationToken) => command.Operation switch
    {
        "find-entity-by-application-key" => await FindEntityByApplicationKeyAsync(session, Read<FindEntityByApplicationKeyInput>(command), cancellationToken),
        "find-entity" => await FindEntityAsync(session, Read<FindEntityInput>(command), cancellationToken),
        "search" => await SearchAsync(session, Read<MemorySearchRequest>(command), cancellationToken),
        "get-claim" => await GetClaimAsync(session, Read<GetClaimInput>(command).ClaimId, cancellationToken),
        "list-claims" => await ListClaimsAsync(session, Read<MemoryPartition>(command), cancellationToken),
        "get-knowledge-transfer" => await GetTransferAsync(session, Read<GetTransferInput>(command).PackageId, cancellationToken),
        _ => throw new InvalidOperationException("Unsupported memory query operation.")
    };

    private async Task<object> HandleWriteAsync(AgentSession session, CSweetMemoryCommand command, CancellationToken cancellationToken)
    {
        switch (command.Operation)
        {
            case "append-episode":
                var episode = Read<MemoryEpisode>(command); await AuthorizeAsync(session, episode.Partition, PlatformMemoryAction.Propose, cancellationToken);
                episode = PlatformMemoryWritePolicy.Episode(session, episode, DateTimeOffset.UtcNow);
                return _ingestion is null ? await _store.AppendEpisodeAsync(episode, cancellationToken)
                    : await _ingestion.AcceptProposalAsync(Guid.Parse(session.MemoryTenantId!), Guid.Parse(session.MemoryEmployeeId!),
                        Guid.Parse(session.InstallationId), episode, cancellationToken);
            case "upsert-entity":
            case "write-block":
            case "write-edge":
                // Entity/block upserts can overwrite trusted records by name/key. Edges have
                // no pending-review state. The store-shaped protocol cannot express safe proposals.
                throw PlatformMemoryWritePolicy.ReviewRequired();
            case "write-claim":
                var claim = Read<MemoryClaim>(command); await AuthorizeAsync(session, claim.Partition, PlatformMemoryAction.Propose, cancellationToken);
                claim = PlatformMemoryWritePolicy.Claim(session, claim, DateTimeOffset.UtcNow);
                claim = await ResolveClaimAsync(claim, cancellationToken) ?? throw InvalidReference();
                return await _store.WriteClaimAsync(claim, cancellationToken);
            case "write-procedure":
                var procedure = Read<ProceduralMemory>(command); await AuthorizeAsync(session, procedure.Partition, PlatformMemoryAction.Propose, cancellationToken);
                procedure = PlatformMemoryWritePolicy.Procedure(session, procedure, DateTimeOffset.UtcNow);
                await RequiredSourceAsync(procedure.Partition, procedure.EpisodeId, cancellationToken);
                await RequiredContributorsAsync(procedure.Partition, procedure.SourceEpisodeIds, cancellationToken);
                return await _store.WriteProcedureAsync(procedure, cancellationToken);
            case "write-embedding":
                var embedding = Read<MemoryEmbedding>(command); await AuthorizeAsync(session, embedding.Partition, PlatformMemoryAction.Propose, cancellationToken);
                // The current vector retrieval channel supports episodes only.
                if (embedding.Layer != MemoryLayer.Episodic) throw InvalidReference();
                await RequiredSourceAsync(embedding.Partition, embedding.MemoryId, cancellationToken);
                return await _store.WriteEmbeddingAsync(embedding, cancellationToken);
            case "record-use":
                var use = Read<MemoryUse>(command); await AuthorizeAsync(session, use.Partition, PlatformMemoryAction.Propose, cancellationToken);
                use = PlatformMemoryWritePolicy.Use(session, use, DateTimeOffset.UtcNow);
                await _store.RecordUseAsync(use, cancellationToken);
                return new MemoryWriteResult(use.Id, true);
            default:
                throw new InvalidOperationException("Unsupported memory write operation.");
        }
    }

    private async Task<object> HandleManageAsync(AgentSession session, CSweetMemoryCommand command, CancellationToken cancellationToken)
    {
        switch (command.Operation)
        {
            case "supersede-claim":
            case "set-confirmation":
            case "write-knowledge-transfer":
                // No authenticated reviewer, expected revision, or atomic create-only transfer
                // operation exists on this wire contract. Even PendingApproval can overwrite an
                // approved package through the store's upsert, so do not forward any state.
                throw PlatformMemoryWritePolicy.ReviewRequired();
            case "delete-scope":
                var partition = Read<MemoryPartition>(command);
                await AuthorizeAsync(session, partition, PlatformMemoryAction.Manage, cancellationToken);
                // Raw deletion still lacks reviewed suppression and lifecycle propagation.
                throw PlatformMemoryWritePolicy.ReviewRequired();
            default:
                throw new InvalidOperationException("Unsupported memory management operation.");
        }
    }

    private async Task<object> HandleExportAsync(AgentSession session, CSweetMemoryCommand command, CancellationToken cancellationToken)
    {
        if (command.Operation != "export") throw new InvalidOperationException("Unsupported memory export operation.");
        var partition = Read<MemoryPartition>(command);
        await AuthorizeAsync(session, partition, PlatformMemoryAction.Read, cancellationToken);
        return await ProjectExportAsync(partition, cancellationToken);
    }

    private async Task<object?> FindEntityByApplicationKeyAsync(AgentSession session, FindEntityByApplicationKeyInput input, CancellationToken cancellationToken)
    {
        await AuthorizeAsync(session, input.Partition, PlatformMemoryAction.Read, cancellationToken);
        var entity = await _store.FindEntityByApplicationKeyAsync(input.Partition, input.ApplicationKey, cancellationToken);
        return EligibleEntity(entity, input.Partition) ? entity : null;
    }

    private async Task<object?> FindEntityAsync(AgentSession session, FindEntityInput input, CancellationToken cancellationToken)
    {
        await AuthorizeAsync(session, input.Partition, PlatformMemoryAction.Read, cancellationToken);
        var entity = await _store.FindEntityAsync(input.Partition, input.CanonicalName, cancellationToken);
        return EligibleEntity(entity, input.Partition) ? entity : null;
    }

    private async Task<object> SearchAsync(AgentSession session, MemorySearchRequest request, CancellationToken cancellationToken)
    {
        await AuthorizeAsync(session, request.Partition, PlatformMemoryAction.Read, cancellationToken);
        var asOf = request.AsOf ?? DateTimeOffset.UtcNow;
        var candidates = await _store.SearchAsync(request with
        {
            Limit = Math.Clamp(request.Limit, 1, 100), AsOf = asOf,
            IncludePending = false, IncludeSuperseded = false
        }, cancellationToken);
        return candidates.Where(item => MemoryRecallPolicy.IsEligible(item,
            MemoryRecallPolicy.MaximumSensitivity(request.Partition), asOf)).ToList();
    }

    private async Task<object?> GetClaimAsync(AgentSession session, Guid claimId, CancellationToken cancellationToken)
    {
        var claim = await _store.GetClaimAsync(claimId, cancellationToken);
        if (claim is null) return null;
        await AuthorizeAsync(session, claim.Partition, PlatformMemoryAction.Read, cancellationToken);
        claim = await ResolveClaimAsync(claim, cancellationToken);
        if (claim is null) return null;
        return MemoryRecallPolicy.IsEligible(claim, MemoryRecallPolicy.MaximumSensitivity(claim.Partition),
            DateTimeOffset.UtcNow) ? claim : null;
    }

    private async Task<object> ListClaimsAsync(AgentSession session, MemoryPartition partition, CancellationToken cancellationToken)
    {
        await AuthorizeAsync(session, partition, PlatformMemoryAction.Read, cancellationToken);
        var claims = new List<MemoryClaim>();
        foreach (var claim in await _store.ListClaimsAsync(partition, cancellationToken))
        {
            if (claim.Partition != partition) continue;
            var resolved = await ResolveClaimAsync(claim, cancellationToken);
            if (resolved is not null && MemoryRecallPolicy.IsEligible(resolved,
                MemoryRecallPolicy.MaximumSensitivity(partition), DateTimeOffset.UtcNow)) claims.Add(resolved);
        }
        return claims;
    }

    private async Task<object?> GetTransferAsync(AgentSession session, Guid packageId, CancellationToken cancellationToken)
    {
        var package = await _transfers.GetKnowledgeTransferAsync(packageId, cancellationToken);
        if (package is null) return null;
        await AuthorizeAsync(session, package.TargetNamespace.Partition, PlatformMemoryAction.Read, cancellationToken);
        if (package.TenantId != session.MemoryTenantId || !Enum.IsDefined(package.Status) ||
            !Enum.IsDefined(package.DebriefSensitivity)) return null;
        var maximum = MemoryRecallPolicy.MaximumSensitivity(package.TargetNamespace.Partition);
        var sources = new Dictionary<MemoryPartition, Dictionary<(MemoryLayer, Guid), KnowledgeTransferItem>>();
        foreach (var source in package.SourceNamespaces)
        {
            await AuthorizeAsync(session, source.Partition, PlatformMemoryAction.Read, cancellationToken);
            var projection = await ProjectExportAsync(source.Partition, cancellationToken);
            sources[source.Partition] = MemoryReadProjection.TransferItems(projection, source.Partition)
                .ToDictionary(x => (x.Layer, x.MemoryId));
        }
        var items = new List<KnowledgeTransferItem>();
        foreach (var item in package.Items)
        {
            if (!sources.TryGetValue(item.SourcePartition, out var records) ||
                !records.TryGetValue((item.Layer, item.MemoryId), out var verified) ||
                item.Content != verified.Content || !item.EpisodeIds.SequenceEqual(verified.EpisodeIds)) return null;
            var sensitivity = MemoryProvenance.Maximum(item.Sensitivity, verified.Sensitivity);
            if (sensitivity > maximum) return null;
            items.Add(verified with { Sensitivity = sensitivity });
        }
        // A debrief can summarize any item, so withholding one item must also withhold the debrief.
        var debriefSensitivity = MemoryProvenance.Maximum(items.Select(x => x.Sensitivity).Append(package.DebriefSensitivity).ToArray());
        return debriefSensitivity <= maximum ? package with { Items = items, DebriefSensitivity = debriefSensitivity } : null;
    }

    private IMemorySourceReader Sources => _store as IMemorySourceReader
        ?? throw new InvalidOperationException("The memory store does not support provenance validation.");

    private async Task<MemoryClaim?> ResolveClaimAsync(MemoryClaim claim, CancellationToken cancellationToken)
    {
        if (!MemoryProvenance.HasBoundedSources(claim.SourceEpisodeIds)) return null;
        var contributors = new Dictionary<Guid, MemoryEpisode>();
        foreach (var sourceId in claim.SourceEpisodeIds.Distinct())
            if (await Sources.GetEpisodeAsync(claim.Partition, sourceId, cancellationToken) is { } source)
                contributors[sourceId] = source;
        return MemoryProvenance.ResolveClaim(claim,
            await Sources.GetEpisodeAsync(claim.Partition, claim.EpisodeId, cancellationToken),
            await Sources.GetEntityAsync(claim.Partition, claim.SubjectEntityId, cancellationToken),
            claim.ObjectEntityId is { } id ? await Sources.GetEntityAsync(claim.Partition, id, cancellationToken) : null,
            DateTimeOffset.UtcNow, contributors);
    }

    private async Task<IReadOnlyDictionary<Guid, MemoryEpisode>> RequiredContributorsAsync(MemoryPartition partition,
        IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        MemoryProvenance.ValidateSourceEpisodes(ids);
        var contributors = new Dictionary<Guid, MemoryEpisode>();
        foreach (var id in ids.Distinct())
            contributors[id] = await RequiredSourceAsync(partition, id, cancellationToken);
        return contributors;
    }

    private async Task<MemoryEpisode> RequiredSourceAsync(MemoryPartition partition, Guid id, CancellationToken cancellationToken)
    {
        var source = await Sources.GetEpisodeAsync(partition, id, cancellationToken);
        return MemoryProvenance.IsCurrent(source, partition, id, DateTimeOffset.UtcNow) ? source! : throw InvalidReference();
    }

    private async Task<MemoryExport> ProjectExportAsync(MemoryPartition partition, CancellationToken cancellationToken) =>
        MemoryReadProjection.Create(await _store.ExportAsync(partition, cancellationToken), partition,
            MemoryRecallPolicy.MaximumSensitivity(partition), DateTimeOffset.UtcNow);

    private static bool EligibleEntity(MemoryEntity? entity, MemoryPartition partition) =>
        entity is not null && entity.Partition == partition && Enum.IsDefined(entity.Sensitivity) &&
        entity.Sensitivity <= MemoryRecallPolicy.MaximumSensitivity(partition);

    private static UnauthorizedAccessException InvalidReference() => new("The memory source or referenced record is unavailable in this namespace.");

    private static T Read<T>(CSweetMemoryCommand command) => command.Payload.Deserialize<T>(JsonOptions)
        ?? throw new JsonException($"Operation '{command.Operation}' has an empty payload.");

    private Task AuthorizeAsync(AgentSession session, MemoryPartition partition, PlatformMemoryAction action,
        CancellationToken cancellationToken) =>
        _identityResolver?.AuthorizeAsync(session, partition, action, cancellationToken)
        ?? throw new UnauthorizedAccessException("Server memory identity resolution is required.");

    private static CapabilityResult Success(string requestId, byte[] payload) => new()
    {
        RequestId = requestId, Succeeded = true, ContentType = "application/json",
        Payload = JsonPayload.From(payload), HasMore = false
    };

    private static CapabilityResult Failure(string requestId, string error, string? code = null, bool? retryable = null) => new()
    {
        RequestId = requestId, Succeeded = false, ContentType = "application/json", Error = error,
        FailureCode = code, Retryable = retryable, HasMore = false
    };

    private sealed record FindEntityByApplicationKeyInput(MemoryPartition Partition, string ApplicationKey);
    // System.Text.Json cannot instantiate IReadOnlySet<T> from a non-null wire array.
    // Keep the public request contract while adapting its bounded layer filter here.
    private sealed class MemoryLayerSetConverter : System.Text.Json.Serialization.JsonConverter<IReadOnlySet<MemoryLayer>>
    {
        public override IReadOnlySet<MemoryLayer> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Memory layers must be an array.");
            var layers = new HashSet<MemoryLayer>();
            var count = 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray) return layers;
                if (++count > 4 || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var value) ||
                    !Enum.IsDefined((MemoryLayer)value)) throw new JsonException("Invalid memory layer filter.");
                layers.Add((MemoryLayer)value);
            }
            throw new JsonException("Incomplete memory layer filter.");
        }

        public override void Write(Utf8JsonWriter writer, IReadOnlySet<MemoryLayer> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var layer in value.Order()) writer.WriteNumberValue((int)layer);
            writer.WriteEndArray();
        }
    }
    private sealed record FindEntityInput(MemoryPartition Partition, string CanonicalName);
    private sealed record GetClaimInput(Guid ClaimId);
    private sealed record GetTransferInput(Guid PackageId);
}
