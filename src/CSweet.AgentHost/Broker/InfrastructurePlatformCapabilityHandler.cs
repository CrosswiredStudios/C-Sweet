using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Infrastructure;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Infrastructure;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

public sealed class InfrastructurePlatformCapabilityHandler(
    CSweetDbContext db,
    IInfrastructureProviderGateway providers,
    IInfrastructureChangeExecutionService changes,
    IAuditEventWriter audit) : IPlatformCapabilityHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlySet<string> Capabilities = new HashSet<string>(StringComparer.Ordinal)
    {
        InfrastructureCapabilityNames.EnvironmentRead, InfrastructureCapabilityNames.StateWrite,
        InfrastructureCapabilityNames.ChangePropose, InfrastructureCapabilityNames.ChangeRead,
        InfrastructureCapabilityNames.OperationExecute, InfrastructureCapabilityNames.Reconcile,
        InfrastructureCapabilityNames.DeploymentContractPublish, InfrastructureCapabilityNames.FileTransfer
    };

    public bool CanHandle(string capability) => Capabilities.Contains(capability);

    public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(session.BusinessId, out var organizationId) ||
            !Guid.TryParse(session.InstallationId, out var installationId))
        {
            yield return Failure(request.RequestId, "The infrastructure installation identity is invalid.");
            yield break;
        }
        CapabilityResult result;
        try
        {
            object response;
            if (request.Capability == InfrastructureCapabilityNames.EnvironmentRead)
                response = await ReadEnvironmentsAsync(organizationId, installationId,
                    Deserialize<InfrastructureEnvironmentReadRequest>(request), cancellationToken);
            else if (request.Capability == InfrastructureCapabilityNames.StateWrite)
                response = await WriteStateAsync(organizationId, installationId,
                    Deserialize<InfrastructureStateWriteRequest>(request), cancellationToken);
            else if (request.Capability == InfrastructureCapabilityNames.ChangePropose)
                response = await ProposeAsync(organizationId, installationId,
                    Deserialize<InfrastructureChangeProposalRequest>(request), cancellationToken);
            else if (request.Capability == InfrastructureCapabilityNames.ChangeRead)
                response = await ReadChangesAsync(organizationId, installationId,
                    Deserialize<InfrastructureChangeReadRequest>(request), cancellationToken);
            else if (request.Capability == InfrastructureCapabilityNames.OperationExecute)
                response = await ReadOrExecuteAsync(organizationId, installationId,
                    Deserialize<InfrastructureOperationExecuteRequest>(request), cancellationToken);
            else if (request.Capability == InfrastructureCapabilityNames.Reconcile)
                response = await ReconcileAsync(organizationId, installationId,
                    Deserialize<InfrastructureReconcileRequest>(request), cancellationToken);
            else if (request.Capability == InfrastructureCapabilityNames.DeploymentContractPublish)
                response = await PublishDeploymentContractAsync(organizationId, installationId,
                    Deserialize<InfrastructureDeploymentContractPublishRequest>(request), cancellationToken);
            else if (request.Capability == InfrastructureCapabilityNames.FileTransfer)
                response = await TransferAsync(organizationId, installationId,
                    Deserialize<InfrastructureFileTransferRequest>(request), cancellationToken);
            else
                throw new InvalidOperationException("Unsupported infrastructure capability.");
            result = Success(request.RequestId, response);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            result = Failure(request.RequestId, exception.Message);
        }
        yield return result;
    }

    private async Task<IReadOnlyList<InfrastructureEnvironment>> ReadEnvironmentsAsync(Guid organizationId,
        Guid installationId, InfrastructureEnvironmentReadRequest request, CancellationToken token)
    {
        var records = await db.PluginOperationalStates.Where(x => x.OrganizationId == organizationId &&
            x.AgentInstallationId == installationId && x.Kind == "infrastructure-environment").ToListAsync(token);
        if (records.Count == 0 && !string.IsNullOrWhiteSpace(request.Provider))
        {
            var now = DateTimeOffset.UtcNow;
            var provider = NormalizeKey(request.Provider);
            var stored = new StoredEnvironment(provider, $"connection:{provider}-oauth", "production",
                null, null, now.AddDays(7));
            var record = new PluginOperationalState
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, AgentInstallationId = installationId,
                Kind = "infrastructure-environment", ExternalKey = $"{provider}:production", Revision = 1,
                PayloadJson = JsonSerializer.Serialize(stored, JsonOptions), CreatedAt = now, UpdatedAt = now
            };
            db.PluginOperationalStates.Add(record);
            await db.SaveChangesAsync(token);
            records.Add(record);
        }
        return records.Where(x => request.EnvironmentId is null || x.Id == request.EnvironmentId)
            .Select(MapEnvironment).ToArray();
    }

    private async Task<InfrastructureStateRevision> WriteStateAsync(Guid organizationId, Guid installationId,
        InfrastructureStateWriteRequest request, CancellationToken token)
    {
        ValidateIdempotency(request.IdempotencyKey);
        if (request.Kind is not ("desired" or "observed")) throw new InvalidOperationException("State kind must be desired or observed.");
        if (request.SchemaVersion < 1 || string.IsNullOrWhiteSpace(request.SchemaId) || request.State.GetRawText().Length > 262_144)
            throw new InvalidOperationException("The infrastructure state schema or payload is invalid.");
        var environment = await RequireEnvironmentAsync(organizationId, installationId, request.EnvironmentId, token);
        if (environment.Revision != request.ExpectedEnvironmentRevision)
            throw new InvalidOperationException("The infrastructure environment changed; reread it before writing state.");
        var existing = await db.PluginOperationalStates.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.AgentInstallationId == installationId &&
            x.Kind == "infrastructure-state-revision" && x.ExternalKey == request.IdempotencyKey, token);
        if (existing is not null) return MapStateRevision(existing);

        var now = DateTimeOffset.UtcNow;
        var stateId = Guid.NewGuid();
        var contentHash = Hash(request.State.GetRawText());
        var revision = new InfrastructureStateRevision(stateId, environment.Id, request.Kind,
            request.SchemaId, request.SchemaVersion, request.State.Clone(), contentHash, environment.Revision + 1, now);
        db.PluginOperationalStates.Add(new PluginOperationalState
        {
            Id = stateId, OrganizationId = organizationId, AgentInstallationId = installationId,
            Kind = "infrastructure-state-revision", ExternalKey = request.IdempotencyKey,
            PayloadJson = JsonSerializer.Serialize(revision, JsonOptions), Revision = revision.Revision,
            CreatedAt = now, UpdatedAt = now
        });
        var stored = JsonSerializer.Deserialize<StoredEnvironment>(environment.PayloadJson, JsonOptions)!;
        stored = request.Kind == "desired" ? stored with { DesiredStateRevisionId = stateId }
            : stored with { ObservedStateRevisionId = stateId };
        environment.PayloadJson = JsonSerializer.Serialize(stored, JsonOptions);
        environment.Revision++;
        environment.UpdatedAt = now;
        await db.SaveChangesAsync(token);
        await audit.WriteAsync("infrastructure.state.revisioned", nameof(PluginOperationalState), stateId,
            $"Persisted canonical {request.Kind} infrastructure state revision {revision.Revision}.",
            JsonSerializer.Serialize(new { organizationId, installationId, request.EnvironmentId, request.Kind,
                request.SchemaId, request.SchemaVersion, contentHash }), token);
        return revision;
    }

    private async Task<InfrastructureChangeSet> ProposeAsync(Guid organizationId, Guid installationId,
        InfrastructureChangeProposalRequest request, CancellationToken token)
    {
        ValidateIdempotency(request.IdempotencyKey);
        if (request.Operations.Count == 0 || request.Operations.Count > 32 || request.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("A bounded, unexpired infrastructure operation list is required.");
        await RequireEnvironmentAsync(organizationId, installationId, request.EnvironmentId, token);
        var manifest = await LoadManifestAsync(organizationId, installationId, token);
        var declared = manifest.McpServers.SelectMany(x => x.Tools.Select(tool =>
                (Capability: tool.Capability, Effect: tool.Effect)))
            .Concat(manifest.ProviderOperations.Select(x => (Capability: x.Capability, Effect: x.Effect)))
            .Append((Capability: InfrastructureCapabilityNames.FileTransfer, Effect: "security-sensitive-write"))
            .ToDictionary(x => x.Capability, x => x.Effect, StringComparer.Ordinal);
        foreach (var operation in request.Operations)
        {
            ValidateIdempotency(operation.IdempotencyKey);
            if (!declared.TryGetValue(operation.Capability, out var effect) || effect != operation.Effect)
                throw new InvalidOperationException($"Operation '{operation.Capability}' does not match a manifest-declared capability and effect.");
            if (operation.Input.TryGetProperty("force", out var force) && force.ValueKind == JsonValueKind.True)
                throw new InvalidOperationException("Destructive DNS force replacement is never exposed.");
        }
        var existing = await db.ActionProposals.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.AgentInstallationId == installationId &&
            x.IdempotencyKey == request.IdempotencyKey, token);
        if (existing is not null) return MapChange(existing);

        var payloadHash = Hash(JsonSerializer.Serialize(request, JsonOptions));
        var route = await ApprovalRouteAsync(organizationId, installationId, request.FiscalImpact, token);
        var writes = request.Operations.Any(x => x.Effect != "read");
        var envelope = new InfrastructureChangeEnvelope(request.EnvironmentId.ToString("D"),
            InfrastructureChangeExecutionService.ActionType, payloadHash, request.IdempotencyKey,
            request.EnvironmentId.ToString("D"), null, writes, request, route);
        var now = DateTimeOffset.UtcNow;
        var proposal = new ActionProposal
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, AgentInstallationId = installationId,
            ActionType = InfrastructureChangeExecutionService.ActionType, Summary = request.Summary,
            PayloadJson = JsonSerializer.Serialize(envelope, JsonOptions),
            RiskClass = request.FiscalImpact.HasFiscalImpact ? "FiscalWrite" : writes ? "InfrastructureWrite" : "ReadOnly",
            IdempotencyKey = request.IdempotencyKey, Status = writes ? ProposalStatus.Pending : ProposalStatus.Approved,
            CreatedAt = now, DecidedAt = writes ? null : now
        };
        db.ActionProposals.Add(proposal);
        await db.SaveChangesAsync(token);
        if (!writes) await changes.ExecuteAsync(proposal, token);
        await audit.WriteAsync("infrastructure.change.proposed", nameof(ActionProposal), proposal.Id,
            request.Summary, JsonSerializer.Serialize(new { organizationId, installationId, request.EnvironmentId,
                payloadHash, fiscal = request.FiscalImpact, operations = request.Operations.Select(x => x.Capability) }), token);
        return MapChange(proposal);
    }

    private async Task<IReadOnlyList<InfrastructureChangeSet>> ReadChangesAsync(Guid organizationId,
        Guid installationId, InfrastructureChangeReadRequest request, CancellationToken token) =>
        (await db.ActionProposals.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
             x.AgentInstallationId == installationId && x.ActionType == InfrastructureChangeExecutionService.ActionType &&
             (request.ChangeSetId == null || x.Id == request.ChangeSetId)).OrderByDescending(x => x.CreatedAt)
            .Take(250).ToListAsync(token)).Select(MapChange)
            .Where(x => request.EnvironmentId is null || x.EnvironmentId == request.EnvironmentId).ToArray();

    private async Task<IReadOnlyList<InfrastructureOperationReceipt>> ReadOrExecuteAsync(Guid organizationId,
        Guid installationId, InfrastructureOperationExecuteRequest request, CancellationToken token)
    {
        var proposal = await db.ActionProposals.SingleOrDefaultAsync(x => x.Id == request.ChangeSetId &&
            x.OrganizationId == organizationId && x.AgentInstallationId == installationId &&
            x.ActionType == InfrastructureChangeExecutionService.ActionType, token)
            ?? throw new InvalidOperationException("The infrastructure change set was not found.");
        var change = MapChange(proposal);
        if (!FixedEquals(change.PayloadHash, request.ExpectedPayloadHash))
            throw new InvalidOperationException("The infrastructure change hash changed after review.");
        if (proposal.Status != ProposalStatus.Approved)
            throw new InvalidOperationException("The infrastructure change has not received its final approval.");
        return await changes.ExecuteAsync(proposal, token);
    }

    private async Task<InfrastructureReconciliationReport> ReconcileAsync(Guid organizationId, Guid installationId,
        InfrastructureReconcileRequest request, CancellationToken token)
    {
        var environment = await RequireEnvironmentAsync(organizationId, installationId, request.EnvironmentId, token);
        var stored = JsonSerializer.Deserialize<StoredEnvironment>(environment.PayloadJson, JsonOptions)!;
        var now = DateTimeOffset.UtcNow;
        if (!request.Force && stored.NextReconciliationAt > now)
            throw new InvalidOperationException("The deterministic weekly reconciliation is not due yet.");
        var report = new InfrastructureReconciliationReport(Guid.NewGuid(), request.EnvironmentId,
            stored.ObservedStateRevisionId is null ? "AttentionRequired" : "Reconciled",
            stored.ObservedStateRevisionId is null ? ["Observed provider state has not been recorded."] : [],
            [], stored.ObservedStateRevisionId is null ? ["Run manifest-declared provider reads and persist observed state."] : [],
            now, now.AddDays(7));
        db.PluginOperationalStates.Add(new PluginOperationalState
        {
            Id = report.Id, OrganizationId = organizationId, AgentInstallationId = installationId,
            Kind = "infrastructure-reconciliation", ExternalKey = $"{request.EnvironmentId:N}:{now:yyyyMMddHHmmss}",
            PayloadJson = JsonSerializer.Serialize(report, JsonOptions), Revision = 1, CreatedAt = now, UpdatedAt = now
        });
        environment.PayloadJson = JsonSerializer.Serialize(stored with { NextReconciliationAt = report.NextReconciliationAt }, JsonOptions);
        environment.Revision++;
        environment.UpdatedAt = now;
        await db.SaveChangesAsync(token);
        return report;
    }

    private async Task<InfrastructureDeploymentContract> PublishDeploymentContractAsync(Guid organizationId,
        Guid installationId, InfrastructureDeploymentContractPublishRequest request, CancellationToken token)
    {
        ValidateIdempotency(request.IdempotencyKey);
        await RequireEnvironmentAsync(organizationId, installationId, request.EnvironmentId, token);
        var existing = await db.PluginOperationalStates.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.AgentInstallationId == installationId &&
            x.Kind == "infrastructure-deployment-contract" && x.ExternalKey == request.IdempotencyKey, token);
        if (existing is not null)
            return JsonSerializer.Deserialize<InfrastructureDeploymentContract>(existing.PayloadJson, JsonOptions)!;
        var now = DateTimeOffset.UtcNow;
        var priorVersions = await db.PluginOperationalStates.CountAsync(x => x.OrganizationId == organizationId &&
            x.AgentInstallationId == installationId && x.Kind == "infrastructure-deployment-contract", token);
        var hash = Hash(JsonSerializer.Serialize(request, JsonOptions));
        var contract = new InfrastructureDeploymentContract(Guid.NewGuid(), request.EnvironmentId, priorVersions + 1,
            request.Domain, request.HostingTarget, request.Endpoints, request.DnsExpectations,
            request.ArtifactRequirements, request.BrokeredCredentialReferences, hash, now);
        db.PluginOperationalStates.Add(new PluginOperationalState
        {
            Id = contract.Id, OrganizationId = organizationId, AgentInstallationId = installationId,
            Kind = "infrastructure-deployment-contract", ExternalKey = request.IdempotencyKey,
            PayloadJson = JsonSerializer.Serialize(contract, JsonOptions), Revision = contract.Version,
            CreatedAt = now, UpdatedAt = now
        });
        await db.SaveChangesAsync(token);
        return contract;
    }

    private async Task<InfrastructureFileTransferResponse> TransferAsync(Guid organizationId, Guid installationId,
        InfrastructureFileTransferRequest request, CancellationToken token)
    {
        if (request.Operation == "upload")
            throw new InvalidOperationException("Uploads require an exact hash-bound infrastructure change approval.");
        if (request.Operation is not ("probe" or "list" or "stat"))
            throw new InvalidOperationException("Only non-writing file-transfer operations may run without approval.");
        return await providers.TransferAsync(organizationId, installationId, request, token);
    }

    private async Task<IReadOnlyList<InfrastructureApprovalStage>> ApprovalRouteAsync(Guid organizationId,
        Guid installationId, InfrastructureFiscalImpact fiscal, CancellationToken token)
    {
        var employees = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.IsActive)
            .ToListAsync(token);
        var agent = employees.SingleOrDefault(x => x.AgentInstallationId == installationId);
        var leadership = await db.LeadershipAssignments.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.EndsAt == null)
            .ToListAsync(token);
        var ceo = leadership.SingleOrDefault(x => x.PositionKey == LeadershipPositionKeys.ChiefExecutiveOfficer)?.OrganizationUserId ??
                  employees.Where(x => x.PermissionLevel == OrganizationPermissionLevel.Owner).OrderBy(x => x.CreatedAt).Select(x => x.Id).FirstOrDefault();
        if (ceo == Guid.Empty) throw new InvalidOperationException("The organization has no active CEO or owner for infrastructure approvals.");
        var manager = agent?.ReportsToOrganizationUserId;
        var cfo = leadership.SingleOrDefault(x => x.PositionKey == LeadershipPositionKeys.ChiefFinancialOfficer)?.OrganizationUserId;
        var route = new List<(string Kind, Guid Id)>();
        var exception = fiscal.HasFiscalImpact && (fiscal.MaximumAmount is null ||
            !fiscal.BudgetStatus.Equals("WithinBudget", StringComparison.OrdinalIgnoreCase));
        if (!exception) route.Add(("ManagerApproval", manager ?? ceo));
        else
        {
            route.Add(("ManagerEndorsement", manager ?? ceo));
            if (cfo.HasValue && cfo != ceo) route.Add(("CfoRecommendation", cfo.Value));
            route.Add(("CeoException", ceo));
        }
        return route.GroupBy(x => x.Id).Select((x, index) => new InfrastructureApprovalStage(index + 1,
            x.Last().Kind, x.Key, "Pending", null, null, null)).ToArray();
    }

    private async Task<PluginOperationalState> RequireEnvironmentAsync(Guid organizationId, Guid installationId,
        Guid environmentId, CancellationToken token) => await db.PluginOperationalStates.SingleOrDefaultAsync(x =>
            x.Id == environmentId && x.OrganizationId == organizationId && x.AgentInstallationId == installationId &&
            x.Kind == "infrastructure-environment", token)
        ?? throw new InvalidOperationException("The infrastructure environment was not found.");

    private async Task<PluginManifest> LoadManifestAsync(Guid organizationId, Guid installationId, CancellationToken token)
    {
        var json = await db.AgentInstallations.AsNoTracking().Where(x => x.Id == installationId &&
            x.BusinessId == organizationId.ToString("D")).Select(x => x.PackageVersion!.ManifestJson).SingleAsync(token);
        return JsonSerializer.Deserialize<PluginManifest>(json, JsonOptions)
            ?? throw new InvalidOperationException("The installed manifest is invalid.");
    }

    private static InfrastructureEnvironment MapEnvironment(PluginOperationalState record)
    {
        var stored = JsonSerializer.Deserialize<StoredEnvironment>(record.PayloadJson, JsonOptions)!;
        return new(record.Id, record.OrganizationId, stored.Provider, stored.AccountReference, stored.Environment,
            record.Revision, stored.DesiredStateRevisionId, stored.ObservedStateRevisionId,
            stored.NextReconciliationAt, record.CreatedAt, record.UpdatedAt);
    }

    private static InfrastructureStateRevision MapStateRevision(PluginOperationalState record) =>
        JsonSerializer.Deserialize<InfrastructureStateRevision>(record.PayloadJson, JsonOptions)!;

    private static InfrastructureChangeSet MapChange(ActionProposal proposal)
    {
        var envelope = JsonSerializer.Deserialize<InfrastructureChangeEnvelope>(proposal.PayloadJson, JsonOptions)!;
        return new(proposal.Id, envelope.Change.EnvironmentId, proposal.Summary, envelope.PayloadHash,
            proposal.Status.ToString(), envelope.Change.Operations, envelope.Change.FiscalImpact,
            envelope.ApprovalRoute, envelope.Change.ExpiresAt, proposal.CreatedAt, proposal.DecidedAt ?? proposal.CreatedAt);
    }

    private static T Deserialize<T>(RequestCapability request) =>
        JsonSerializer.Deserialize<T>(request.Payload.Span, JsonOptions)
        ?? throw new JsonException("The infrastructure request payload is empty.");

    private static void ValidateIdempotency(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160)
            throw new InvalidOperationException("A bounded idempotency key is required.");
    }

    private static string NormalizeKey(string value) => new(value.Trim().ToLowerInvariant()
        .Where(x => char.IsLetterOrDigit(x) || x is '-' or '.').ToArray());
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool FixedEquals(string left, string right)
    {
        var a = Encoding.ASCII.GetBytes(left.ToLowerInvariant()); var b = Encoding.ASCII.GetBytes(right.ToLowerInvariant());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
    private static CapabilityResult Success(string requestId, object value) => new()
    {
        RequestId = requestId, Succeeded = true, ContentType = "application/json",
        Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions))
    };
    private static CapabilityResult Failure(string requestId, string error) => new()
    {
        RequestId = requestId, Succeeded = false, ContentType = "application/json", Error = error,
        Payload = JsonPayload.FromUtf8("{\"isError\":true}")
    };
    private sealed record StoredEnvironment(string Provider, string AccountReference, string Environment,
        Guid? DesiredStateRevisionId, Guid? ObservedStateRevisionId, DateTimeOffset? NextReconciliationAt);
}
