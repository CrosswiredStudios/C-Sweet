using CSweet.Office.Contracts.Guest;

namespace CSweet.AgentBroker;

public sealed record BrokerOperationContext(
    Guid WorkloadId,
    Guid InstallationId,
    string RequestId,
    string Purpose,
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Headers,
    ReadOnlyMemory<byte> Body);

public sealed record BrokerOperationResult(
    int StatusCode,
    IReadOnlyDictionary<string, string> Headers,
    ReadOnlyMemory<byte> Body,
    string? ErrorCode = null);

public interface IAgentBrokerOperationHandler
{
    Task<BrokerOperationResult> HandleAsync(BrokerOperationContext request, CancellationToken cancellationToken);
}

public sealed record GuestBrokerStreamContext(
    Guid WorkloadId,
    Guid InstallationId,
    string StreamId,
    long Sequence,
    ReadOnlyMemory<byte> Content,
    bool Completed,
    string? Digest);

public interface IGuestBrokerStreamHandler
{
    Task HandleAsync(GuestBrokerStreamContext chunk, CancellationToken cancellationToken);
}

public sealed class GuestWorkloadExitedException(
    int exitCode,
    string reasonCode,
    string? detail) : InvalidOperationException(
        $"The guest workload failed ({reasonCode}, exit {exitCode})" +
        (string.IsNullOrWhiteSpace(detail) ? string.Empty : $": {Summarize(detail)}"))
{
    public int ExitCode { get; } = exitCode;
    public string ReasonCode { get; } = reasonCode;
    public string? SanitizedDetail { get; } = detail;

    private static string Summarize(string value) =>
        value.Length <= 1500 ? value : "..." + value[^1500..];
}

public sealed class GuestBrokerHostSession(
    AgentBrokerGrant grant,
    IAgentBrokerOperationHandler handler,
    TimeProvider timeProvider,
    IGuestBrokerStreamHandler? streamHandler = null,
    GuestBootConfiguration? bootConfiguration = null,
    StartCommand? startCommand = null)
{
    private const int MaximumConcurrentProxyRequests = 32;
    private int _requestCount;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _outputLock = new(1, 1);

    public Task Started => _started.Task;
    public GuestExit? WorkloadExit { get; private set; }

    public async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        var proxyOperations = new List<Task>();
        try
        {
            grant.Validate(timeProvider);
            using var leaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            leaseCancellation.CancelAfter(grant.ExpiresAt - timeProvider.GetUtcNow());
            var token = leaseCancellation.Token;
            if (bootConfiguration is not null)
                await WriteAsync(output, new GuestEnvelope
                {
                    ProtocolVersion = grant.ProtocolVersion,
                    MessageId = Guid.NewGuid().ToString("N"),
                    BootConfiguration = bootConfiguration
                }, token);
            var identity = new ExpectedGuestIdentity(
                grant.WorkloadId,
                grant.ChannelId,
                grant.GuestImageDigest,
                grant.ArtifactDigest,
                grant.BootToken,
                grant.ExpiresAt,
                grant.ProtocolVersion);
            var verifier = new GuestHandshakeVerifier(identity, timeProvider);
            var hello = await ReadRequiredAsync(input, token);
            if (hello.BodyCase == GuestEnvelope.BodyOneofCase.BootFailure)
                throw new InvalidOperationException(
                    $"The guest could not complete secure boot preparation ({hello.BootFailure.ReasonCode}): {hello.BootFailure.Detail}");
            if (hello.BodyCase != GuestEnvelope.BodyOneofCase.Hello)
                throw new InvalidDataException("The guest did not start with an authenticated hello.");
            await WriteAsync(output, Envelope(verifier.VerifyHelloAndCreateChallenge(hello.Hello)), token);
            var proof = await ReadRequiredAsync(input, token);
            if (proof.BodyCase != GuestEnvelope.BodyOneofCase.Proof)
                throw new InvalidDataException("The guest did not answer the host challenge.");
            var lease = verifier.VerifyProof(proof.Proof, grant.MaximumFrameBytes);
            await WriteAsync(output, Envelope(lease), token);
            if (!lease.Accepted) return;
            if (startCommand is not null)
                await WriteAsync(output, new GuestEnvelope
                {
                    ProtocolVersion = grant.ProtocolVersion,
                    MessageId = Guid.NewGuid().ToString("N"),
                    StartCommand = startCommand
                }, token);
            else
                _started.TrySetResult();

            while (!token.IsCancellationRequested)
            {
                var envelope = await LengthDelimitedProtobuf.ReadAsync(
                    input,
                    GuestEnvelope.Parser,
                    grant.MaximumFrameBytes,
                    token);
                if (envelope is null)
                    throw new EndOfStreamException("The guest closed the authenticated broker channel without reporting workload completion.");
                ValidateEnvelope(envelope);
                if (envelope.BodyCase is GuestEnvelope.BodyOneofCase.Exit)
                {
                    WorkloadExit = envelope.Exit.Clone();
                    if (envelope.Exit.ExitCode != 0)
                    {
                        throw new GuestWorkloadExitedException(
                            envelope.Exit.ExitCode,
                            envelope.Exit.ReasonCode,
                            string.IsNullOrWhiteSpace(envelope.Exit.Detail) ? null : envelope.Exit.Detail);
                    }
                    return;
                }
                if (envelope.BodyCase is GuestEnvelope.BodyOneofCase.Health)
                {
                    if (string.Equals(envelope.Health.State, "running", StringComparison.Ordinal))
                        _started.TrySetResult();
                    continue;
                }
                if (envelope.BodyCase is GuestEnvelope.BodyOneofCase.StreamChunk)
                {
                    if (streamHandler is null)
                        throw new InvalidDataException("The guest attempted an unsupported stream operation.");
                    var chunk = envelope.StreamChunk;
                    if (chunk.StreamId.Length is < 1 or > 128 || chunk.Sequence < 0 ||
                        chunk.Content.Length > grant.MaximumFrameBytes ||
                        (!string.IsNullOrEmpty(chunk.Digest) && !IsDigest(chunk.Digest)))
                        throw new InvalidDataException("The guest stream chunk is invalid.");
                    await streamHandler.HandleAsync(new GuestBrokerStreamContext(
                        grant.WorkloadId,
                        grant.InstallationId,
                        chunk.StreamId,
                        chunk.Sequence,
                        chunk.Content.Memory,
                        chunk.Completed,
                        string.IsNullOrEmpty(chunk.Digest) ? null : chunk.Digest), token);
                    continue;
                }
                if (envelope.BodyCase is not GuestEnvelope.BodyOneofCase.ProxyRequest)
                    throw new InvalidDataException("The guest sent an unsupported broker message.");
                proxyOperations.RemoveAll(operation => operation.IsCompleted);
                if (proxyOperations.Count >= MaximumConcurrentProxyRequests)
                {
                    await Task.WhenAny(proxyOperations);
                    proxyOperations.RemoveAll(operation => operation.IsCompleted);
                }
                proxyOperations.Add(ProcessProxySafeAsync(envelope.ProxyRequest, output, token));
            }
        }
        finally
        {
            if (proxyOperations.Count > 0)
            {
                try { await Task.WhenAll(proxyOperations); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            }
            if (!_started.Task.IsCompleted)
                _started.TrySetException(new IOException(
                    "The guest broker session ended before the authenticated workload start was acknowledged."));
        }
    }

    private async Task ProcessProxySafeAsync(
        ProxyRequest request,
        Stream output,
        CancellationToken cancellationToken)
    {
        try
        {
            await ProcessProxyAsync(request, output, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            await WriteAsync(
                output,
                Response(request.RequestId, 502, "broker-operation-failed", ReadOnlyMemory<byte>.Empty),
                cancellationToken);
        }
    }

    private async Task ProcessProxyAsync(ProxyRequest request, Stream output, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _requestCount) > grant.MaximumRequestCount)
        {
            await WriteAsync(output, Response(request.RequestId, 429, "request-limit-exceeded", ReadOnlyMemory<byte>.Empty), cancellationToken);
            return;
        }
        if (!Guid.TryParseExact(request.RequestId, "N", out _) ||
            !grant.AllowedPurposes.Contains(request.Purpose) ||
            !IsMethod(request.Method) || !IsPath(request.Path) ||
            request.Body.Length > grant.MaximumRequestBodyBytes ||
            request.Headers.Count > 64 || request.Headers.Any(header => !IsHeader(header.Key, header.Value)))
        {
            await WriteAsync(output, Response(request.RequestId, 403, "capability-denied", ReadOnlyMemory<byte>.Empty), cancellationToken);
            return;
        }
        BrokerOperationResult result;
        try
        {
            result = await handler.HandleAsync(new BrokerOperationContext(
                grant.WorkloadId,
                grant.InstallationId,
                request.RequestId,
                request.Purpose,
                request.Method,
                request.Path,
                request.Headers,
                request.Body.Memory), cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            result = new BrokerOperationResult(403, new Dictionary<string, string>(), ReadOnlyMemory<byte>.Empty, "capability-denied");
        }
        if (result.StatusCode is < 100 or > 599 || result.Body.Length > grant.MaximumResponseBodyBytes ||
            result.Headers.Count > 64 || result.Headers.Any(header => !IsHeader(header.Key, header.Value)))
            throw new InvalidDataException("A broker handler returned an invalid or oversized response.");
        await WriteAsync(output, Response(request.RequestId, result.StatusCode, result.ErrorCode, result.Body, result.Headers), cancellationToken);
    }

    private async Task<GuestEnvelope> ReadRequiredAsync(Stream input, CancellationToken cancellationToken)
    {
        var envelope = await LengthDelimitedProtobuf.ReadAsync(input, GuestEnvelope.Parser, grant.MaximumFrameBytes, cancellationToken)
            ?? throw new EndOfStreamException("The guest closed the broker channel.");
        ValidateEnvelope(envelope);
        return envelope;
    }

    private void ValidateEnvelope(GuestEnvelope envelope)
    {
        if (!string.Equals(envelope.ProtocolVersion, grant.ProtocolVersion, StringComparison.Ordinal) ||
            !Guid.TryParseExact(envelope.MessageId, "N", out _))
            throw new InvalidDataException("The broker envelope is invalid.");
    }

    private GuestEnvelope Envelope(HostChallenge challenge) => new()
    {
        ProtocolVersion = grant.ProtocolVersion,
        MessageId = Guid.NewGuid().ToString("N"),
        Challenge = challenge
    };
    private GuestEnvelope Envelope(GuestLease lease) => new()
    {
        ProtocolVersion = grant.ProtocolVersion,
        MessageId = Guid.NewGuid().ToString("N"),
        Lease = lease
    };
    private GuestEnvelope Response(string id, int status, string? error, ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string>? headers = null)
    {
        var response = new ProxyResponse
        {
            RequestId = id,
            StatusCode = status,
            ErrorCode = error ?? string.Empty,
            Body = Google.Protobuf.ByteString.CopyFrom(body.Span)
        };
        if (headers is not null)
            foreach (var header in headers) response.Headers.Add(header.Key, header.Value);
        return new GuestEnvelope
        {
            ProtocolVersion = grant.ProtocolVersion,
            MessageId = Guid.NewGuid().ToString("N"),
            ProxyResponse = response
        };
    }
    private async Task WriteAsync(Stream output, GuestEnvelope envelope, CancellationToken cancellationToken)
    {
        await _outputLock.WaitAsync(cancellationToken);
        try
        {
            await LengthDelimitedProtobuf.WriteAsync(output, envelope, grant.MaximumFrameBytes, cancellationToken);
        }
        finally
        {
            _outputLock.Release();
        }
    }
    private static bool IsMethod(string value) => value is "GET" or "POST" or "PUT" or "PATCH" or "DELETE";
    private static bool IsPath(string value) => value.Length is >= 1 and <= 2048 && value[0] == '/' && !value.Contains("..", StringComparison.Ordinal) && !value.Contains('\\');
    private static bool IsHeader(string key, string value) => key.Length is >= 1 and <= 80 && value.Length <= 4096 &&
        key.All(character => char.IsAsciiLetterOrDigit(character) || character == '-') &&
        !value.Any(char.IsControl);
    private static bool IsDigest(string value) => value.Length == 71 &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;
}
