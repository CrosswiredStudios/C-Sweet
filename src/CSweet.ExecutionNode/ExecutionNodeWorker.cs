using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CSweet.AgentRuntime.Abstractions;
using CSweet.AgentRuntime.Protocol;
using CSweet.Contracts.Setup;
using Grpc.Core;
using Grpc.Net.Client;

namespace CSweet.ExecutionNode;

public sealed class ExecutionNodeWorker(
    ExecutionNodeOptions options,
    ExecutionNodeStateStore stateStore,
    RuntimeHostInventory inventory,
    ExecutionNodeArtifactCache artifactCache,
    IEnumerable<IAgentIsolationProvider> isolationProviders,
    IHttpClientFactory httpClientFactory,
    ILogger<ExecutionNodeWorker> logger) : BackgroundService
{
    private readonly IReadOnlyDictionary<string, IAgentIsolationProvider> _providers = isolationProviders
        .ToDictionary(x => x.Descriptor.ProviderId, StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeAssignments = [];
    private readonly SemaphoreSlim _workloadSlots = new(Math.Max(1, options.MaximumConcurrentWorkloads));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        stateStore.InitializeMaintenanceSession();
        ExecutionNodeState? processSession = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var certificate = stateStore.GetOrCreateCertificate();
                try
                {
                    var state = processSession ?? await stateStore.LoadAsync(stoppingToken) ??
                        await EnrollAsync(certificate, stoppingToken);
                    if (processSession is null)
                    {
                        if (state.SessionEpoch == long.MaxValue)
                            throw new InvalidDataException("The execution-node session epoch is exhausted.");
                        var nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        var nextEpoch = Math.Max(state.SessionEpoch + 1, nowEpoch);
                        state = state with { SessionEpoch = nextEpoch };
                        await stateStore.SaveAsync(state, stoppingToken);
                        processSession = state;
                    }
                    var operational = await RefreshOperationalCertificateAsync(state, certificate, stoppingToken);
                    if (!ReferenceEquals(operational, certificate))
                    {
                        certificate.Dispose();
                        certificate = operational;
                    }
                    await RunControlSessionAsync(state, certificate, stoppingToken);
                }
                finally { certificate.Dispose(); }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogError(exception, "The execution-node control session ended; reconnecting.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<ExecutionNodeState> EnrollAsync(X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        var token = options.EnrollmentToken;
        if (string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(options.EnrollmentTokenFilePath))
        {
            var tokenPath = Path.GetFullPath(options.EnrollmentTokenFilePath);
            try
            {
                token = (await File.ReadAllTextAsync(tokenPath, cancellationToken)).Trim();
                File.Delete(tokenPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("The protected enrollment-token input could not be read.", exception);
            }
        }
        if (string.IsNullOrWhiteSpace(token) && string.IsNullOrWhiteSpace(options.DevelopmentBootstrapKey) && !Console.IsInputRedirected)
            throw new InvalidOperationException("An enrollment token must be supplied through protected installer configuration or stdin.");
        if (string.IsNullOrWhiteSpace(token) && string.IsNullOrWhiteSpace(options.DevelopmentBootstrapKey))
            token = (await Console.In.ReadLineAsync(cancellationToken))?.Trim();
        if (string.IsNullOrWhiteSpace(token) && string.IsNullOrWhiteSpace(options.DevelopmentBootstrapKey))
            throw new InvalidOperationException("The enrollment token is missing.");
        var providers = await inventory.ProbeAsync(cancellationToken);
        var request = new ClaimExecutionNodeRequest(
            token ?? string.Empty, options.NodeName, Environment.MachineName, RuntimeHostInventory.Platform(),
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            typeof(ExecutionNodeWorker).Assembly.GetName().Version?.ToString(3) ?? "1.0.0", "1.0",
            certificate.Thumbprint, certificate.SerialNumber, certificate.NotAfter,
            ExecutionNodeStateStore.CreateCertificateSigningRequestPem(certificate),
            options.AllocatableCpuCount, options.AllocatableMemoryMb, options.AllocatableDiskMb,
            options.MaximumConcurrentWorkloads, providers);
        var client = httpClientFactory.CreateClient("control-plane");
        using var response = string.IsNullOrWhiteSpace(token)
            ? await client.PostAsJsonAsync("api/execution-nodes/development-loopback-claim",
                new DevelopmentExecutionNodeClaimRequest(options.DevelopmentBootstrapKey, request), cancellationToken)
            : await client.PostAsJsonAsync("api/execution-nodes/claim", request, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<ClaimExecutionNodeResponse>(cancellationToken)
            ?? throw new InvalidDataException("The control plane returned an empty enrollment response.");
        if (!response.IsSuccessStatusCode || !result.Succeeded || result.NodeId is null || string.IsNullOrWhiteSpace(result.EnrollmentReceipt))
            throw new InvalidOperationException($"Execution-node enrollment failed ({result.ErrorCode ?? "unknown"}): {result.Message}");
        options.EnrollmentToken = string.Empty;
        var state = new ExecutionNodeState(result.NodeId.Value, result.EnrollmentReceipt,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), stateStore.GetCertificatePath());
        await stateStore.SaveAsync(state, cancellationToken);
        logger.LogInformation("Execution node {NodeId} enrolled and is awaiting administrator approval.", state.NodeId);
        return state;
    }

    private async Task<X509Certificate2> RefreshOperationalCertificateAsync(
        ExecutionNodeState state,
        X509Certificate2 current,
        CancellationToken cancellationToken)
    {
        var bootstrapCertificate = string.Equals(current.Subject, current.Issuer, StringComparison.OrdinalIgnoreCase);
        if (!bootstrapCertificate && current.NotAfter > DateTime.UtcNow.AddHours(6)) return current;
        using var client = bootstrapCertificate
            ? httpClientFactory.CreateClient("control-plane")
            : CreateMutualTlsClient(current);
        using var response = await client.PostAsJsonAsync(
            $"api/execution-nodes/{state.NodeId:D}/certificate",
            new ExecutionNodeCertificateRequest(bootstrapCertificate ? state.EnrollmentReceipt : string.Empty),
            cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            return current;
        response.EnsureSuccessStatusCode();
        var issued = await response.Content.ReadFromJsonAsync<ExecutionNodeCertificateResponse>(cancellationToken)
            ?? throw new InvalidDataException("The operational certificate response was empty.");
        if (!issued.Succeeded || string.IsNullOrWhiteSpace(issued.CertificateBase64))
            return current;
        if (string.Equals(Normalize(current.Thumbprint), Normalize(issued.CertificateThumbprint ?? string.Empty),
                StringComparison.Ordinal))
            return current;
        var installed = stateStore.InstallOperationalCertificate(current, issued.CertificateBase64);
        if (!string.Equals(Normalize(installed.Thumbprint), Normalize(issued.CertificateThumbprint ?? string.Empty),
                StringComparison.Ordinal))
        {
            installed.Dispose();
            throw new CryptographicException("The issued operational certificate thumbprint did not match.");
        }
        logger.LogInformation("Installed rotated operational certificate {Thumbprint} expiring at {ExpiresAt}.",
            installed.Thumbprint, installed.NotAfter);
        return installed;
    }

    private HttpClient CreateMutualTlsClient(X509Certificate2 certificate)
    {
        var handler = new HttpClientHandler();
        handler.ClientCertificates.Add(certificate);
        return new HttpClient(handler) { BaseAddress = new Uri(options.ControlPlaneUrl) };
    }

    private async Task RunControlSessionAsync(
        ExecutionNodeState state,
        X509Certificate2 certificate,
        CancellationToken cancellationToken)
    {
        var providers = await inventory.ProbeAsync(cancellationToken);
        if (string.Equals(certificate.Subject, certificate.Issuer, StringComparison.OrdinalIgnoreCase))
        {
            var http = httpClientFactory.CreateClient("control-plane");
            using var heartbeat = await http.PostAsJsonAsync($"api/execution-nodes/{state.NodeId:D}/heartbeat",
                new ExecutionNodeHeartbeatRequest(state.EnrollmentReceipt, state.SessionEpoch,
                    options.AllocatableCpuCount, options.AllocatableMemoryMb, options.AllocatableDiskMb,
                    options.MaximumConcurrentWorkloads, providers), cancellationToken);
            heartbeat.EnsureSuccessStatusCode();
        }

        var handler = new HttpClientHandler();
        handler.ClientCertificates.Add(certificate);
        using var channel = GrpcChannel.ForAddress(options.ControlPlaneUrl, new GrpcChannelOptions { HttpHandler = handler });
        var client = new ExecutionNodeGateway.ExecutionNodeGatewayClient(channel);
        using var call = client.Connect(cancellationToken: cancellationToken);
        using var writerLock = new SemaphoreSlim(1, 1);
        var readTask = ReadControlMessagesAsync(
            call.ResponseStream, call.RequestStream, writerLock, client, state, cancellationToken);
        try
        {
            var sendInventory = true;
            while (!cancellationToken.IsCancellationRequested)
            {
                var heartbeat = new NodeHeartbeat
                {
                    OccurredAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    AllocatableCpuCount = options.AllocatableCpuCount,
                    AllocatableMemoryMb = options.AllocatableMemoryMb,
                    AllocatableDiskMb = options.AllocatableDiskMb,
                    MaximumConcurrentWorkloads = options.MaximumConcurrentWorkloads
                };
                if (sendInventory)
                {
                    heartbeat.Providers.AddRange(providers.Select(ProviderInventory));
                    sendInventory = false;
                }
                await SendAsync(call.RequestStream, writerLock, new NodeControlMessage
                {
                    ProtocolVersion = "1.0",
                    NodeId = state.NodeId.ToString("D"),
                    SessionEpoch = state.SessionEpoch,
                    Heartbeat = heartbeat
                }, cancellationToken);
                await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));
                if (readTask.IsCompleted) await readTask;
            }
        }
        finally
        {
            foreach (var active in _activeAssignments.Values) active.Cancel();
            await call.RequestStream.CompleteAsync();
        }
    }

    private static NodeProviderInventory ProviderInventory(RegisterExecutionNodeProviderRequest provider) => new()
    {
        ProviderId = provider.ProviderId,
        ProviderVersion = provider.ProviderVersion,
        BrokerProtocolVersion = provider.BrokerProtocolVersion,
        GuestImageDigest = provider.GuestImageDigest,
        CertificationSuiteVersion = provider.CertificationSuiteVersion,
        CertificationEvidenceDigest = provider.CertificationEvidenceDigest,
        CertifiedAtUnixSeconds = provider.CertifiedAt.ToUnixTimeSeconds(),
        CertificationExpiresAtUnixSeconds = provider.CertificationExpiresAt?.ToUnixTimeSeconds() ?? 0,
        SupportsBuilderWorkloads = provider.SupportsBuilderWorkloads,
        SupportsRuntimeWorkloads = provider.SupportsRuntimeWorkloads,
        IsAvailable = provider.IsAvailable,
        UnavailableReason = provider.UnavailableReason ?? string.Empty
    };

    private async Task ReadControlMessagesAsync(
        IAsyncStreamReader<ControlPlaneMessage> stream,
        IClientStreamWriter<NodeControlMessage> writer,
        SemaphoreSlim writerLock,
        ExecutionNodeGateway.ExecutionNodeGatewayClient gatewayClient,
        ExecutionNodeState state,
        CancellationToken cancellationToken)
    {
        ECDsa? assignmentVerificationKey = null;
        string? assignmentSigningKeyId = null;
        try
        {
        await foreach (var message in stream.ReadAllAsync(cancellationToken))
        {
            if (message.NodeId != state.NodeId.ToString("D") || message.SessionEpoch != state.SessionEpoch)
                throw new InvalidDataException("The control-plane message was not bound to this node session.");
            if (message.BodyCase == ControlPlaneMessage.BodyOneofCase.Hello)
            {
                if (message.Hello.AssignmentVerificationPublicKey.Length is < 64 or > 1024 ||
                    string.IsNullOrWhiteSpace(message.Hello.AssignmentSigningKeyId))
                    throw new InvalidDataException("The gateway signing identity is invalid.");
                assignmentVerificationKey?.Dispose();
                assignmentVerificationKey = ECDsa.Create();
                assignmentVerificationKey.ImportSubjectPublicKeyInfo(
                    message.Hello.AssignmentVerificationPublicKey.Span, out _);
                assignmentSigningKeyId = message.Hello.AssignmentSigningKeyId;
            }
            else if (message.BodyCase == ControlPlaneMessage.BodyOneofCase.Assignment)
            {
                ValidateAssignment(message.Assignment, state.NodeId,
                    assignmentVerificationKey, assignmentSigningKeyId);
                logger.LogInformation("Received fenced workload assignment {AssignmentId} at epoch {Epoch}.",
                    message.Assignment.AssignmentId, message.Assignment.FencingEpoch);
                var assignmentId = Guid.Parse(message.Assignment.AssignmentId);
                var assignmentCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (_activeAssignments.TryAdd(assignmentId, assignmentCancellation))
                {
                    stateStore.MarkAssignmentActive(assignmentId);
                    _ = ExecuteAssignmentAsync(message.Assignment, writer, writerLock, gatewayClient, state,
                        assignmentCancellation).ContinueWith(task =>
                        {
                            if (task.Exception is not null)
                                logger.LogError(task.Exception, "Execution assignment {AssignmentId} failed.", assignmentId);
                        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                }
                else
                    assignmentCancellation.Dispose();
            }
            else if (message.BodyCase == ControlPlaneMessage.BodyOneofCase.Fence)
            {
                if (Guid.TryParse(message.Fence.AssignmentId, out var fencedId) &&
                    _activeAssignments.TryGetValue(fencedId, out var active)) active.Cancel();
                logger.LogWarning("Assignment {AssignmentId} was fenced at epoch {Epoch}: {Reason}",
                    message.Fence.AssignmentId, message.Fence.FencingEpoch, message.Fence.Reason);
            }
            else if (message.BodyCase == ControlPlaneMessage.BodyOneofCase.Drain)
            {
                stateStore.SetDraining(message.Drain.Drain);
                logger.LogWarning("The node drain state changed to {Drain}: {Reason}",
                    message.Drain.Drain, message.Drain.Reason);
            }
        }
        }
        finally { assignmentVerificationKey?.Dispose(); }
    }

    private async Task ExecuteAssignmentAsync(
        WorkloadAssignment assignment,
        IClientStreamWriter<NodeControlMessage> writer,
        SemaphoreSlim writerLock,
        ExecutionNodeGateway.ExecutionNodeGatewayClient gatewayClient,
        ExecutionNodeState state,
        CancellationTokenSource assignmentCancellation)
    {
        var assignmentId = Guid.Parse(assignment.AssignmentId);
        IsolationWorkloadHandle? handle = null;
        IAgentIsolationProvider? provider = null;
        CancellationTokenSource? tunnelLifetime = null;
        Task? tunnelTask = null;
        var enteredSlot = false;
        try
        {
            await _workloadSlots.WaitAsync(assignmentCancellation.Token);
            enteredSlot = true;
            if (!_providers.TryGetValue(assignment.ProviderId, out provider))
                throw new IsolationUnavailableException("The assigned provider is not installed on this node.");
            var specification = DeserializeSpecification(assignment.SpecificationJson);
            if (specification is RuntimeWorkloadSpec runtime)
            {
                if (assignment.ArtifactReadToken.Length is < 32 or > 256)
                    throw new InvalidDataException("The runtime assignment does not contain a valid artifact read grant.");
                await artifactCache.EnsureAsync(
                    gatewayClient, state, assignment, runtime.Artifact.Digest, assignmentCancellation.Token);
            }
            await SendStatusAsync(writer, writerLock, state, assignment,
                "Starting", null, null, null, null, assignmentCancellation.Token);
            handle = await provider.CreateAsync(specification, assignmentCancellation.Token);
            await provider.StartAsync(handle, assignmentCancellation.Token);
            if (provider is not IAgentGuestChannelProvider guestChannels)
                throw new IsolationUnavailableException("The RuntimeHost provider does not expose a guest broker channel.");
            var guestStream = await guestChannels.OpenGuestChannelAsync(handle, assignmentCancellation.Token);
            tunnelLifetime = CancellationTokenSource.CreateLinkedTokenSource(assignmentCancellation.Token);
            tunnelTask = RelayGuestChannelAsync(
                gatewayClient, guestStream, state, assignment, tunnelLifetime.Token);
            await SendStatusAsync(writer, writerLock, state, assignment,
                "Running", null, null, handle, null, assignmentCancellation.Token);

            var nextRenewal = DateTimeOffset.UtcNow.AddSeconds(20);
            while (!assignmentCancellation.IsCancellationRequested)
            {
                if (tunnelTask.IsFaulted) await tunnelTask;
                if (DateTimeOffset.UtcNow >= nextRenewal)
                {
                    await SendAsync(writer, writerLock, Envelope(state, new AssignmentLeaseRenewal
                    {
                        AssignmentId = assignment.AssignmentId,
                        FencingEpoch = assignment.FencingEpoch,
                        RequestedExpiryUnixSeconds = DateTimeOffset.UtcNow.AddSeconds(60).ToUnixTimeSeconds()
                    }), assignmentCancellation.Token);
                    nextRenewal = DateTimeOffset.UtcNow.AddSeconds(20);
                }
                var status = await provider.InspectAsync(handle, assignmentCancellation.Token);
                if (status is null || status.State is IsolationWorkloadState.Destroyed or
                    IsolationWorkloadState.Failed or IsolationWorkloadState.Stopped)
                {
                    var completed = status is not null && status.State != IsolationWorkloadState.Failed &&
                        status.ExitCode.GetValueOrDefault() == 0 &&
                        status.TerminationReason is IsolationTerminationReason.None or IsolationTerminationReason.Completed;
                    var logs = await ReadLogsAsync(provider, handle, CancellationToken.None);
                    await SendStatusAsync(writer, writerLock, state, assignment,
                        completed ? "Completed" : "Failed",
                        completed ? null : status?.ErrorCode ?? "workload-failed",
                        completed ? null : "The isolated workload did not complete successfully.",
                        handle,
                        logs,
                        assignmentCancellation.Token);
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), assignmentCancellation.Token);
            }
        }
        catch (OperationCanceledException) when (assignmentCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            try
            {
                await SendStatusAsync(writer, writerLock, state, assignment,
                    "Failed", "execution-node-error",
                    $"The node could not execute the workload ({exception.GetType().Name}).",
                    handle,
                    provider is not null && handle is not null
                        ? await ReadLogsAsync(provider, handle, CancellationToken.None)
                        : null,
                    CancellationToken.None);
            }
            catch (Exception reportException)
            {
                logger.LogWarning(reportException, "Could not report assignment {AssignmentId} failure.", assignmentId);
            }
        }
        finally
        {
            if (tunnelLifetime is not null)
            {
                await tunnelLifetime.CancelAsync();
                if (tunnelTask is not null)
                {
                    try { await tunnelTask; }
                    catch (OperationCanceledException) when (tunnelLifetime.IsCancellationRequested) { }
                    catch (IOException) when (tunnelLifetime.IsCancellationRequested) { }
                    catch (RpcException) when (tunnelLifetime.IsCancellationRequested) { }
                }
                tunnelLifetime.Dispose();
            }
            if (handle is not null && provider is not null)
            {
                try { await provider.DestroyAsync(handle, CancellationToken.None); }
                catch (Exception exception) { logger.LogWarning(exception, "Could not destroy workload {AssignmentId}.", assignmentId); }
            }
            if (enteredSlot) _workloadSlots.Release();
            _activeAssignments.TryRemove(assignmentId, out _);
            stateStore.MarkAssignmentInactive(assignmentId);
            assignmentCancellation.Dispose();
        }
    }

    private static async Task RelayGuestChannelAsync(
        ExecutionNodeGateway.ExecutionNodeGatewayClient client,
        Stream guest,
        ExecutionNodeState state,
        WorkloadAssignment assignment,
        CancellationToken cancellationToken)
    {
        await using (guest)
        using (var call = client.OpenWorkloadTunnel(cancellationToken: cancellationToken))
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            var upload = Task.Run(async () =>
            {
                var buffer = new byte[64 * 1024];
                long sequence = 0;
                while (true)
                {
                    var read = await guest.ReadAsync(buffer, lifetime.Token);
                    await call.RequestStream.WriteAsync(new WorkloadTunnelFrame
                    {
                        NodeId = state.NodeId.ToString("D"),
                        AssignmentId = assignment.AssignmentId,
                        FencingEpoch = assignment.FencingEpoch,
                        SessionEpoch = state.SessionEpoch,
                        Sequence = sequence++,
                        Content = read == 0
                            ? Google.Protobuf.ByteString.Empty
                            : Google.Protobuf.ByteString.CopyFrom(buffer, 0, read),
                        Completed = read == 0
                    }, lifetime.Token);
                    if (read == 0) break;
                }
                await call.RequestStream.CompleteAsync();
            }, lifetime.Token);
            var download = Task.Run(async () =>
            {
                long expectedSequence = 0;
                await foreach (var frame in call.ResponseStream.ReadAllAsync(lifetime.Token))
                {
                    if (frame.Sequence != expectedSequence++ || frame.FencingEpoch != assignment.FencingEpoch)
                        throw new InvalidDataException("The gateway returned an invalid guest-channel frame sequence.");
                    if (frame.Content.Length > 0)
                        await guest.WriteAsync(frame.Content.Memory, lifetime.Token);
                    if (frame.Completed) break;
                }
            }, lifetime.Token);
            var completed = await Task.WhenAny(upload, download);
            await completed;
            if (upload.IsCompletedSuccessfully) await download;
            await lifetime.CancelAsync();
        }
    }

    private static IsolationWorkloadSpec DeserializeSpecification(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = 32 };
        var isBuilder = document.RootElement.EnumerateObject()
            .Any(x => x.Name.Equals("repository", StringComparison.OrdinalIgnoreCase));
        return isBuilder
            ? JsonSerializer.Deserialize<BuilderWorkloadSpec>(json, options)
                ?? throw new InvalidDataException("The builder workload specification is empty.")
            : JsonSerializer.Deserialize<RuntimeWorkloadSpec>(json, options)
                ?? throw new InvalidDataException("The runtime workload specification is empty.");
    }

    private static Task SendStatusAsync(
        IClientStreamWriter<NodeControlMessage> writer,
        SemaphoreSlim writerLock,
        ExecutionNodeState state,
        WorkloadAssignment assignment,
        string status,
        string? failureCode,
        string? sanitizedFailure,
        IsolationWorkloadHandle? handle,
        string? logExcerpt,
        CancellationToken cancellationToken) => SendAsync(writer, writerLock,
            Envelope(state, new AssignmentStatusUpdate
            {
                AssignmentId = assignment.AssignmentId,
                FencingEpoch = assignment.FencingEpoch,
                Status = status,
                FailureCode = failureCode ?? string.Empty,
                SanitizedFailure = sanitizedFailure ?? string.Empty,
                ProviderInstanceId = handle?.ProviderInstanceId ?? string.Empty,
                LogExcerpt = logExcerpt ?? string.Empty
            }), cancellationToken);

    private static async Task<string?> ReadLogsAsync(
        IAgentIsolationProvider provider,
        IsolationWorkloadHandle handle,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        await foreach (var chunk in provider.StreamLogsAsync(handle, 64 * 1024, cancellationToken))
        {
            var remaining = 64 * 1024 - (int)output.Length;
            if (remaining <= 0) break;
            await output.WriteAsync(chunk.Content[..Math.Min(remaining, chunk.Content.Length)], cancellationToken);
        }
        return output.Length == 0 ? null : System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    private static NodeControlMessage Envelope(ExecutionNodeState state, AssignmentLeaseRenewal renewal) => new()
    {
        ProtocolVersion = "1.0", NodeId = state.NodeId.ToString("D"),
        SessionEpoch = state.SessionEpoch, LeaseRenewal = renewal
    };

    private static NodeControlMessage Envelope(ExecutionNodeState state, AssignmentStatusUpdate status) => new()
    {
        ProtocolVersion = "1.0", NodeId = state.NodeId.ToString("D"),
        SessionEpoch = state.SessionEpoch, AssignmentStatus = status
    };

    private static async Task SendAsync(
        IClientStreamWriter<NodeControlMessage> writer,
        SemaphoreSlim writerLock,
        NodeControlMessage message,
        CancellationToken cancellationToken)
    {
        await writerLock.WaitAsync(cancellationToken);
        try { await writer.WriteAsync(message, cancellationToken); }
        finally { writerLock.Release(); }
    }

    private static void ValidateAssignment(
        WorkloadAssignment assignment,
        Guid nodeId,
        ECDsa? verificationKey,
        string? keyId)
    {
        if (verificationKey is null || !string.Equals(keyId, assignment.SignatureKeyId, StringComparison.Ordinal) ||
            !Guid.TryParse(assignment.AssignmentId, out var assignmentId))
            throw new InvalidDataException("The assignment signing identity is unavailable.");
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(assignment.LeaseExpiresAtUnixSeconds);
        if (expiresAt <= DateTimeOffset.UtcNow ||
            !string.Equals(ExecutionAssignmentEnvelope.Digest(assignment.SpecificationJson),
                assignment.SpecificationSha256, StringComparison.Ordinal) ||
            !verificationKey.VerifyData(
                ExecutionAssignmentEnvelope.Payload(nodeId, assignmentId, assignment.FencingEpoch,
                    assignment.SpecificationSha256, expiresAt, assignment.ArtifactReadToken),
                assignment.Signature.Span, HashAlgorithmName.SHA256))
            throw new InvalidDataException("The assignment envelope signature or digest is invalid.");
    }

    private static string Normalize(string value) =>
        new(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
}
