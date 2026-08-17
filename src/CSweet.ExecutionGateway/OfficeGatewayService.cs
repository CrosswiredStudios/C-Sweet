using CSweet.AgentBroker;
using CSweet.Office.Contracts.ControlPlane;
using CSweet.Office.Contracts.Security;
using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using CSweet.Office.Contracts.Workloads;
using System.Security.Cryptography;
using System.Text.Json;

namespace CSweet.ExecutionGateway;

public sealed class OfficeGatewayService(
    CSweetDbContext db,
    IExecutionWorkloadOrchestrator orchestrator,
    IExecutionBrokerSessionRunner brokerSessions,
    IAgentArtifactStore artifactStore,
    ExecutionArtifactGrantLeaseService artifactGrants,
    ExecutionAssignmentSigner signer,
    TimeProvider timeProvider,
    ILogger<OfficeGatewayService> logger)
    : OfficeGateway.OfficeGatewayBase
{
    public override async Task Connect(
        IAsyncStreamReader<OfficeControlMessage> requestStream,
        IServerStreamWriter<HeadquartersControlMessage> responseStream,
        ServerCallContext context)
    {
        var delivered = new Dictionary<Guid, long>();
        var helloDelivered = false;
        bool? deliveredDrainState = null;
        Guid? authenticatedOfficeId = null;
        long sessionEpoch = 0;
        await foreach (var message in requestStream.ReadAllAsync(context.CancellationToken))
        {
            if (!Guid.TryParse(message.OfficeId, out var nodeId) || message.ProtocolVersion != "1.0")
                throw new RpcException(new Status(StatusCode.InvalidArgument, "The node envelope is invalid."));
            if (authenticatedOfficeId is null)
            {
                authenticatedOfficeId = nodeId;
                sessionEpoch = message.SessionEpoch;
            }
            if (authenticatedOfficeId != nodeId || sessionEpoch != message.SessionEpoch)
                throw new RpcException(new Status(StatusCode.PermissionDenied, "The node session binding changed."));
            await AuthorizeAsync(nodeId, context, context.CancellationToken);
            if (message.BodyCase != OfficeControlMessage.BodyOneofCase.Heartbeat)
                await RequireCurrentSessionAsync(nodeId, sessionEpoch, context.CancellationToken);

            if (message.BodyCase == OfficeControlMessage.BodyOneofCase.Heartbeat)
            {
                ValidateHeartbeat(message.Heartbeat);
                var node = await db.ExecutionNodes.Include(x => x.Providers)
                    .SingleAsync(x => x.Id == nodeId, context.CancellationToken);
                if (node.Status == ExecutionNodeStatus.Revoked)
                    throw new RpcException(new Status(StatusCode.PermissionDenied, "The node is revoked."));
                if (sessionEpoch < node.SessionEpoch)
                    throw new RpcException(new Status(StatusCode.PermissionDenied, "The node session was superseded."));
                node.SessionEpoch = sessionEpoch;
                node.LastHeartbeatAt = timeProvider.GetUtcNow();
                var assistedSession = await db.LocalOfficeSetupSessions.AsNoTracking()
                    .Where(x => x.ExecutionNodeId == node.Id &&
                        (x.Status == LocalOfficeSetupSessionStatus.Connected ||
                         x.Status == LocalOfficeSetupSessionStatus.Ready))
                    .OrderByDescending(x => x.UpdatedAt)
                    .FirstOrDefaultAsync(context.CancellationToken);
                ApplyHeartbeatCapacity(node, message.Heartbeat, assistedSession);
                node.UpdatedAt = timeProvider.GetUtcNow();
                if (message.Heartbeat.Providers.Count > 0)
                    ReplaceProviderInventory(node, message.Heartbeat.Providers, timeProvider.GetUtcNow());
                    ReplaceSecurityPosture(node, message.Heartbeat.SecurityPosture);
                if (node.ApprovedAt is not null && node.Status == ExecutionNodeStatus.Offline)
                    node.Status = ExecutionNodeStatus.Ready;
                if (node.ApprovedAt is not null)
                {
                    var enrollment = await db.ExecutionNodeEnrollments.SingleOrDefaultAsync(
                        x => x.ExecutionNodeId == node.Id && x.ReceiptHash != null,
                        context.CancellationToken);
                    if (enrollment is not null) enrollment.ReceiptHash = null;
                }
                await db.SaveChangesAsync(context.CancellationToken);

                if (!helloDelivered)
                {
                    await responseStream.WriteAsync(new HeadquartersControlMessage
                    {
                        ProtocolVersion = "1.0",
                        OfficeId = nodeId.ToString("D"),
                        SessionEpoch = sessionEpoch,
                        Hello = new GatewayHello
                        {
                            AssignmentSigningKeyId = signer.KeyId,
                            AssignmentVerificationPublicKey = ByteString.CopyFrom(
                                Convert.FromBase64String(signer.ExportPublicKeyBase64()))
                        }
                    }, context.CancellationToken);
                    helloDelivered = true;
                }

                var drain = node.Status == ExecutionNodeStatus.Draining;
                if (deliveredDrainState != drain)
                {
                    await responseStream.WriteAsync(new HeadquartersControlMessage
                    {
                        ProtocolVersion = "1.0",
                        OfficeId = nodeId.ToString("D"),
                        SessionEpoch = sessionEpoch,
                        Drain = new DrainOffice
                        {
                            Drain = drain,
                            Reason = drain
                                ? "An administrator placed this node in maintenance drain mode."
                                : "The node is accepting new assignments."
                        }
                    }, context.CancellationToken);
                    deliveredDrainState = drain;
                }

                var assignments = await orchestrator.GetNodeAssignmentsAsync(nodeId, sessionEpoch, context.CancellationToken);
                var activeEpochs = assignments.ToDictionary(x => x.AssignmentId, x => x.FencingEpoch);
                foreach (var stale in delivered.Where(x =>
                             !activeEpochs.TryGetValue(x.Key, out var currentEpoch) || currentEpoch != x.Value).ToArray())
                {
                    await WriteFenceAsync(responseStream, nodeId, sessionEpoch, stale.Key, stale.Value,
                        activeEpochs.ContainsKey(stale.Key)
                            ? "The control plane replaced this assignment with a newer fenced retry."
                            : "The control plane cancelled or replaced this assignment.",
                        context.CancellationToken);
                    delivered.Remove(stale.Key);
                }
                foreach (var assignment in assignments.Where(x =>
                             ShouldDeliver(delivered, x.AssignmentId, x.FencingEpoch)))
                {
                    Guid workloadId;
                    try
                    {
                        workloadId = ResolveAuthorizedWorkloadId(
                            assignment.WorkloadKind, assignment.SpecificationJson);
                    }
                    catch (InvalidDataException exception)
                    {
                        logger.LogError(exception,
                            "Refused to sign assignment {AssignmentId} because its workload specification is invalid.",
                            assignment.AssignmentId);
                        await orchestrator.ReportStatusAsync(
                            nodeId, assignment.AssignmentId, assignment.FencingEpoch,
                            ExecutionAssignmentStatus.Failed,
                            "invalid-workload-authorization",
                            "Headquarters could not bind the assignment to a valid workload specification.",
                            null,
                            context.CancellationToken);
                        continue;
                    }
                    var issuedAt = DateTimeOffset.UtcNow;
                    var artifactReadToken = assignment.ArtifactDigest is null ? null :
                        await orchestrator.IssueArtifactReadGrantAsync(
                            nodeId, assignment.AssignmentId, assignment.FencingEpoch, context.CancellationToken);
                    if (assignment.ArtifactDigest is not null && string.IsNullOrWhiteSpace(artifactReadToken))
                        continue;
                    await responseStream.WriteAsync(new HeadquartersControlMessage
                    {
                        ProtocolVersion = "1.0",
                        OfficeId = nodeId.ToString("D"),
                        SessionEpoch = sessionEpoch,
                        Assignment = new WorkloadAssignment
                        {
                            AssignmentId = assignment.AssignmentId.ToString("D"),
                            WorkloadId = workloadId.ToString("D"),
                            FencingEpoch = assignment.FencingEpoch,
                            ProviderId = assignment.ProviderId,
                            SpecificationJson = assignment.SpecificationJson,
                            SpecificationSha256 = assignment.SpecificationDigest,
                            SignatureKeyId = signer.KeyId,
                            Signature = ByteString.CopyFrom(signer.Sign(
                                nodeId, assignment.AssignmentId, workloadId,
                                assignment.FencingEpoch, assignment.ProviderId,
                                assignment.SpecificationDigest, issuedAt, assignment.LeaseExpiresAt)),
                            LeaseExpiresAtUnixSeconds = assignment.LeaseExpiresAt.ToUnixTimeSeconds(),
                            ArtifactReadToken = artifactReadToken ?? string.Empty,
                            IssuedAtUnixSeconds = issuedAt.ToUnixTimeSeconds(),
                            AuthorizationVersion = AssignmentEnvelope.CurrentAuthorizationVersion
                        }
                    }, context.CancellationToken);
                    logger.LogInformation(
                        "Dispatched assignment {AssignmentId} epoch {FencingEpoch} to Office {OfficeId} " +
                        "using provider {ProviderId}; lease expires at {LeaseExpiresAt}.",
                        assignment.AssignmentId,
                        assignment.FencingEpoch,
                        nodeId,
                        assignment.ProviderId,
                        assignment.LeaseExpiresAt);
                    delivered[assignment.AssignmentId] = assignment.FencingEpoch;
                }
            }
            else if (message.BodyCase == OfficeControlMessage.BodyOneofCase.LeaseRenewal &&
                Guid.TryParse(message.LeaseRenewal.AssignmentId, out var renewalId))
            {
                if (!await orchestrator.RenewLeaseAsync(nodeId, renewalId,
                        message.LeaseRenewal.FencingEpoch, context.CancellationToken))
                {
                    logger.LogWarning(
                        "Rejected lease renewal for assignment {AssignmentId} epoch {FencingEpoch} from Office {OfficeId}.",
                        renewalId, message.LeaseRenewal.FencingEpoch, nodeId);
                    await WriteFenceAsync(responseStream, nodeId, sessionEpoch, renewalId,
                        message.LeaseRenewal.FencingEpoch, "The assignment lease is no longer active.", context.CancellationToken);
                }
            }
            else if (message.BodyCase == OfficeControlMessage.BodyOneofCase.AssignmentStatus &&
                Guid.TryParse(message.AssignmentStatus.AssignmentId, out var statusId) &&
                Enum.TryParse<ExecutionAssignmentStatus>(message.AssignmentStatus.Status, true, out var status))
            {
                var accepted = await orchestrator.ReportStatusAsync(nodeId, statusId,
                        message.AssignmentStatus.FencingEpoch, status,
                        message.AssignmentStatus.FailureCode,
                        message.AssignmentStatus.SanitizedFailure,
                        Result(message.AssignmentStatus), context.CancellationToken);
                if (accepted)
                    logger.LogInformation(
                        "Office {OfficeId} reported assignment {AssignmentId} epoch {FencingEpoch} " +
                        "as {AssignmentStatus}. Failure code: {FailureCode}",
                        nodeId, statusId, message.AssignmentStatus.FencingEpoch, status,
                        message.AssignmentStatus.FailureCode);
                else
                {
                    logger.LogWarning(
                        "Rejected status {AssignmentStatus} for assignment {AssignmentId} epoch {FencingEpoch} " +
                        "from Office {OfficeId}.",
                        status, statusId, message.AssignmentStatus.FencingEpoch, nodeId);
                    await WriteFenceAsync(responseStream, nodeId, sessionEpoch, statusId,
                        message.AssignmentStatus.FencingEpoch, "The status update was fenced.", context.CancellationToken);
                }
            }
        }
    }

    internal static bool ShouldDeliver(
        IReadOnlyDictionary<Guid, long> delivered,
        Guid assignmentId,
        long fencingEpoch) =>
        !delivered.TryGetValue(assignmentId, out var deliveredEpoch) || deliveredEpoch != fencingEpoch;

    private void ReplaceProviderInventory(
        ExecutionNode node,
        IEnumerable<OfficeProviderInventory> inventory,
        DateTimeOffset now)
    {
        var providers = inventory.ToArray();
        if (providers.Length is < 1 or > 16 || providers.Any(provider =>
                string.IsNullOrWhiteSpace(provider.ProviderId) || provider.ProviderId.Length > 100 ||
                provider.ProviderVersion.Length > 64 || provider.BrokerProtocolVersion.Length > 32 ||
                provider.CertificationSuiteVersion.Length > 128 || provider.UnavailableReason.Length > 1024 ||
                !IsValidUnixSeconds(provider.CertifiedAtUnixSeconds) ||
                provider.CertificationExpiresAtUnixSeconds != 0 &&
                    !IsValidUnixSeconds(provider.CertificationExpiresAtUnixSeconds) ||
                provider.IsAvailable && (provider.BrokerProtocolVersion != "1.0" ||
                    !IsSha256(provider.GuestImageDigest) || !IsSha256(provider.CertificationEvidenceDigest) ||
                    string.IsNullOrWhiteSpace(provider.CertificationSuiteVersion))) ||
            providers.Select(provider => provider.ProviderId).Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                providers.Length)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The provider inventory is invalid."));
        var reportedKeys = providers.Select(ProviderKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var obsolete in node.Providers.Where(provider =>
                     !reportedKeys.Contains(ProviderKey(provider))).ToArray())
        {
            db.ExecutionNodeProviders.Remove(obsolete);
            node.Providers.Remove(obsolete);
        }

        foreach (var reported in providers)
        {
            var existing = node.Providers.SingleOrDefault(provider =>
                string.Equals(ProviderKey(provider), ProviderKey(reported), StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new ExecutionNodeProvider
                {
                    Id = Guid.NewGuid(),
                    ExecutionNodeId = node.Id,
                    ProviderId = reported.ProviderId,
                    GuestImageDigest = reported.GuestImageDigest
                };
                node.Providers.Add(existing);
            }
            existing.ProviderVersion = reported.ProviderVersion;
            existing.BrokerProtocolVersion = reported.BrokerProtocolVersion;
            existing.CertificationSuiteVersion = reported.CertificationSuiteVersion;
            existing.CertificationEvidenceDigest = reported.CertificationEvidenceDigest;
            existing.CertifiedAt = DateTimeOffset.FromUnixTimeSeconds(reported.CertifiedAtUnixSeconds);
            existing.CertificationExpiresAt = reported.CertificationExpiresAtUnixSeconds == 0 ? null :
                DateTimeOffset.FromUnixTimeSeconds(reported.CertificationExpiresAtUnixSeconds);
            existing.SupportsBuilderWorkloads = reported.SupportsBuilderWorkloads;
            existing.SupportsRuntimeWorkloads = reported.SupportsRuntimeWorkloads;
            existing.IsAvailable = reported.IsAvailable;
            existing.UnavailableReason = string.IsNullOrWhiteSpace(reported.UnavailableReason) ? null : reported.UnavailableReason;
            existing.UpdatedAt = now;
        }
    }

    private static void ReplaceSecurityPosture(ExecutionNode node, OfficeSecurityPosture? posture)
    {
        if (posture is null) return;
        var profile = posture.Profile.Trim().ToLowerInvariant();
        if (profile is not ("baseline" or "hardened" or "development") ||
            posture.EnabledControls.Count > 64 || posture.MissingControls.Count > 64 ||
            profile == "development" && !posture.DevelopmentAssignmentsAllowed ||
            profile == "hardened" && posture.MissingControls.Count != 0 ||
            !IsValidUnixSeconds(posture.EvaluatedAtUnixSeconds))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The security posture report is invalid."));
        Dictionary<string, string> labels;
        try { labels = JsonSerializer.Deserialize<Dictionary<string, string>>(node.LabelsJson) ?? []; }
        catch (JsonException) { labels = []; }
        foreach (var key in labels.Keys.Where(key => key.StartsWith("csweet.security.", StringComparison.Ordinal)).ToArray())
            labels.Remove(key);
        labels["csweet.security.profile"] = profile;
        labels["csweet.security.mixed-use"] = posture.MixedUseHost ? "true" : "false";
        labels["csweet.security.development-assignments"] = posture.DevelopmentAssignmentsAllowed ? "true" : "false";
        labels["csweet.security.missing-controls"] = posture.MissingControls.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        node.LabelsJson = JsonSerializer.Serialize(labels);
    }

    private static string ProviderKey(ExecutionNodeProvider provider) =>
        $"{provider.ProviderId}\n{provider.GuestImageDigest}";

    private static string ProviderKey(OfficeProviderInventory provider) =>
        $"{provider.ProviderId}\n{provider.GuestImageDigest}";

    private static void ValidateHeartbeat(OfficeHeartbeat heartbeat)
    {
        if (heartbeat.AllocatableCpuCount < 0 || heartbeat.AllocatableCpuCount > 4096 ||
            heartbeat.AllocatableMemoryMb < 0 || heartbeat.AllocatableMemoryMb > 16 * 1024 * 1024 ||
            heartbeat.AllocatableDiskMb < 0 || heartbeat.AllocatableDiskMb > 1024 * 1024 * 1024 ||
            heartbeat.MaximumConcurrentWorkloads < 0 || heartbeat.MaximumConcurrentWorkloads > 100_000)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The node capacity is invalid."));
    }

    internal static void ApplyHeartbeatCapacity(
        ExecutionNode node,
        OfficeHeartbeat heartbeat,
        LocalOfficeSetupSession? assistedSession)
    {
        node.AllocatableCpuCount = assistedSession?.AllocatableCpuCount ?? heartbeat.AllocatableCpuCount;
        node.AllocatableMemoryMb = assistedSession?.AllocatableMemoryMb ?? heartbeat.AllocatableMemoryMb;
        node.AllocatableDiskMb = assistedSession?.AllocatableDiskMb ?? heartbeat.AllocatableDiskMb;
        node.MaximumConcurrentWorkloads = assistedSession?.MaximumConcurrentWorkloads ??
            heartbeat.MaximumConcurrentWorkloads;
    }

    private static bool IsValidUnixSeconds(long value) =>
        value >= DateTimeOffset.MinValue.ToUnixTimeSeconds() &&
        value <= DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    private async Task RequireCurrentSessionAsync(
        Guid nodeId,
        long sessionEpoch,
        CancellationToken cancellationToken)
    {
        var current = await db.ExecutionNodes.AsNoTracking()
            .Where(x => x.Id == nodeId && x.Status != ExecutionNodeStatus.Revoked)
            .Select(x => (long?)x.SessionEpoch)
            .SingleOrDefaultAsync(cancellationToken);
        if (current != sessionEpoch)
            throw new RpcException(new Status(StatusCode.PermissionDenied, "The node session was superseded."));
    }

    public override async Task OpenWorkloadTunnel(
        IAsyncStreamReader<WorkloadTunnelFrame> requestStream,
        IServerStreamWriter<WorkloadTunnelFrame> responseStream,
        ServerCallContext context)
    {
        if (!await requestStream.MoveNext(context.CancellationToken)) return;
        var first = requestStream.Current;
        var (nodeId, assignmentId) = await ValidateTunnelFrameAsync(first, context, context.CancellationToken);
        await using var tunnel = new GrpcWorkloadTunnelStream(
            requestStream,
            responseStream,
            first,
            frame => ValidateTunnelFrameAsync(frame, context, context.CancellationToken),
            nodeId,
            assignmentId,
            first.FencingEpoch,
            first.SessionEpoch,
            context.CancellationToken);
        logger.LogInformation(
            "Opened authenticated workload tunnel for assignment {AssignmentId} from Office {OfficeId} at epoch {FencingEpoch}.",
            assignmentId, nodeId, first.FencingEpoch);
        try
        {
            await brokerSessions.RunAsync(assignmentId, tunnel, context.CancellationToken);
            await tunnel.CompleteAsync();
            logger.LogInformation(
                "Completed authenticated workload tunnel for assignment {AssignmentId} from Office {OfficeId}.",
                assignmentId, nodeId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !context.CancellationToken.IsCancellationRequested)
        {
            if (exception is GuestWorkloadExitedException exited)
            {
                // Guest-provided diagnostic text is persisted through the bounded diagnostic
                // stream. Do not duplicate it into infrastructure logs or gRPC status text.
                logger.LogWarning(
                    "Authenticated guest workload exited for assignment {AssignmentId} from Office {OfficeId} at epoch {FencingEpoch} with reason {ReasonCode} and exit code {ExitCode}.",
                    assignmentId, nodeId, first.FencingEpoch, SafeCode(exited.ReasonCode), exited.ExitCode);
            }
            else
            {
                logger.LogError(exception,
                    "Authenticated workload tunnel failed for assignment {AssignmentId} from Office {OfficeId} at epoch {FencingEpoch}.",
                    assignmentId, nodeId, first.FencingEpoch);
            }
            throw exception is RpcException
                ? exception
                : BrokerTunnelFailure(exception);
        }
    }

    internal static RpcException BrokerTunnelFailure(Exception exception)
    {
        var detail = exception switch
        {
            GuestWorkloadExitedException exited =>
                $"The isolated guest workload exited ({SafeCode(exited.ReasonCode)}, exit {exited.ExitCode}). " +
                "Review the bounded runtime diagnostic excerpt in C-Sweet for details.",
            InvalidDataException invalid =>
                $"The authenticated guest broker protocol was rejected: {SafeDetail(invalid.Message)}",
            _ => "The authenticated guest broker session failed. Check the C-Sweet server log using the assignment identifier."
        };
        return new RpcException(new Status(StatusCode.FailedPrecondition, detail));
    }

    private static string SafeCode(string value) => new(value
        .Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
        .Take(128)
        .ToArray());

    private static string SafeDetail(string value) => new(value
        .Where(character => !char.IsControl(character) || character is '\r' or '\n' or '\t')
        .Take(1500)
        .ToArray());

    public override async Task DownloadArtifact(
        ArtifactDownloadRequest request,
        IServerStreamWriter<ArtifactChunk> responseStream,
        ServerCallContext context)
    {
        if (request.ProtocolVersion != "1.0" ||
            !Guid.TryParse(request.OfficeId, out var nodeId) ||
            !Guid.TryParse(request.AssignmentId, out var assignmentId) ||
            !Guid.TryParse(request.TransferId, out var transferId) || transferId == Guid.Empty ||
            !IsSha256(request.ArtifactDigest) || request.ArtifactReadToken.Length is < 32 or > 256)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The artifact request is invalid."));
        await AuthorizeAsync(nodeId, context, context.CancellationToken);
        var tokenHash = Convert.ToHexStringLower(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(request.ArtifactReadToken)));
        var transferHash = Convert.ToHexStringLower(SHA256.HashData(transferId.ToByteArray()));
        if (!await artifactGrants.ClaimAsync(
                nodeId, assignmentId, request.FencingEpoch, request.ArtifactDigest,
                tokenHash, transferHash, context.CancellationToken))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "The artifact grant is expired or fenced."));

        var completed = false;
        try
        {
            await using var content = await artifactStore.OpenReadAsync(request.ArtifactDigest, context.CancellationToken);
            var totalSize = content.CanSeek ? content.Length : -1;
            if (totalSize > 2L * 1024 * 1024 * 1024)
                throw new RpcException(new Status(StatusCode.ResourceExhausted, "The artifact exceeds the transfer limit."));
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long offset = 0;
            while (true)
            {
                var read = await content.ReadAsync(buffer, context.CancellationToken);
                if (read == 0) break;
                offset += read;
                if (offset > 2L * 1024 * 1024 * 1024)
                    throw new RpcException(new Status(StatusCode.ResourceExhausted, "The artifact exceeds the transfer limit."));
                hash.AppendData(buffer, 0, read);
                await responseStream.WriteAsync(new ArtifactChunk
                {
                    Offset = offset - read,
                    Content = ByteString.CopyFrom(buffer, 0, read),
                    TotalSize = totalSize
                }, context.CancellationToken);
            }
            var actual = $"sha256:{Convert.ToHexStringLower(hash.GetHashAndReset())}";
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(actual),
                    System.Text.Encoding.ASCII.GetBytes(request.ArtifactDigest)))
                throw new RpcException(new Status(StatusCode.DataLoss, "The artifact store returned the wrong digest."));
            await responseStream.WriteAsync(new ArtifactChunk
            {
                Offset = offset,
                Completed = true,
                TotalSize = offset,
                Sha256 = actual
            }, context.CancellationToken);
            completed = true;
        }
        finally
        {
            await artifactGrants.ReleaseAsync(assignmentId, transferHash, completed, CancellationToken.None);
        }
    }

    private async Task<(Guid NodeId, Guid AssignmentId)> ValidateTunnelFrameAsync(
        WorkloadTunnelFrame frame,
        ServerCallContext context,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(frame.OfficeId, out var nodeId) ||
            !Guid.TryParse(frame.AssignmentId, out var assignmentId) || frame.Content.Length > 1024 * 1024)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The workload tunnel frame is invalid."));
        await AuthorizeAsync(nodeId, context, cancellationToken);
        var valid = await db.ExecutionWorkloadAssignments.AsNoTracking().AnyAsync(x =>
            x.Id == assignmentId && x.ExecutionNodeId == nodeId &&
            x.FencingEpoch == frame.FencingEpoch && x.LeaseExpiresAt > timeProvider.GetUtcNow() &&
            (x.Status == ExecutionAssignmentStatus.Assigned || x.Status == ExecutionAssignmentStatus.Starting ||
             x.Status == ExecutionAssignmentStatus.Running || x.Status == ExecutionAssignmentStatus.Stopping),
            cancellationToken);
        valid = valid && await db.ExecutionNodes.AsNoTracking().AnyAsync(x =>
            x.Id == nodeId && x.SessionEpoch == frame.SessionEpoch &&
            x.Status != ExecutionNodeStatus.Revoked, cancellationToken);
        if (!valid)
            throw new RpcException(new Status(StatusCode.PermissionDenied, "The workload tunnel was fenced."));
        return (nodeId, assignmentId);
    }

    private async Task AuthorizeAsync(Guid nodeId, ServerCallContext context, CancellationToken cancellationToken)
    {
        var node = await db.ExecutionNodes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == nodeId, cancellationToken)
            ?? throw new RpcException(new Status(StatusCode.Unauthenticated, "The node is unknown."));
        if (node.Status == ExecutionNodeStatus.Revoked || node.CertificateExpiresAt <= timeProvider.GetUtcNow())
            throw new RpcException(new Status(StatusCode.PermissionDenied, "The node identity is inactive."));
        var certificate = context.GetHttpContext().Connection.ClientCertificate;
        if (certificate is not null && string.Equals(
                Normalize(certificate.Thumbprint), Normalize(node.CertificateThumbprint), StringComparison.Ordinal) &&
            string.Equals(Normalize(certificate.SerialNumber), Normalize(node.CertificateSerialNumber), StringComparison.Ordinal))
            return;
        logger.LogWarning("Rejected Office {OfficeId} because its client certificate did not match.", nodeId);
        throw new RpcException(new Status(StatusCode.Unauthenticated, "A matching Office client certificate is required."));
    }

    private static Task WriteFenceAsync(
        IServerStreamWriter<HeadquartersControlMessage> response,
        Guid nodeId,
        long sessionEpoch,
        Guid assignmentId,
        long epoch,
        string reason,
        CancellationToken cancellationToken) => response.WriteAsync(new HeadquartersControlMessage
    {
        ProtocolVersion = "1.0",
        OfficeId = nodeId.ToString("D"),
        SessionEpoch = sessionEpoch,
        Fence = new FenceAssignment
        {
            AssignmentId = assignmentId.ToString("D"),
            FencingEpoch = epoch,
            Reason = reason
        }
    }, cancellationToken);

    private static string Normalize(string value) =>
        new(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());

    internal static Guid ResolveAuthorizedWorkloadId(
        ExecutionWorkloadKind workloadKind,
        string specificationJson)
    {
        if (string.IsNullOrWhiteSpace(specificationJson))
            throw new InvalidDataException("The workload specification is empty.");
        try
        {
            var workloadId = workloadKind switch
            {
                ExecutionWorkloadKind.Builder =>
                    JsonSerializer.Deserialize<BuilderWorkloadSpecification>(specificationJson)?.WorkloadId,
                ExecutionWorkloadKind.Runtime =>
                    JsonSerializer.Deserialize<RuntimeWorkloadSpecification>(specificationJson)?.WorkloadId,
                _ => null
            };
            if (workloadId is null || workloadId == Guid.Empty)
                throw new InvalidDataException("The workload specification has no valid workload identifier.");
            return workloadId.Value;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The workload specification is not valid JSON.", exception);
        }
    }

    private static bool IsSha256(string value) => value.Length == 71 &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static ExecutionWorkloadResult? Result(AssignmentStatusUpdate update)
    {
        if (string.IsNullOrWhiteSpace(update.ProviderInstanceId) &&
            string.IsNullOrWhiteSpace(update.LogExcerpt)) return null;
        return new ExecutionWorkloadResult(
            update.ProviderInstanceId,
            update.LogExcerpt);
    }
}
