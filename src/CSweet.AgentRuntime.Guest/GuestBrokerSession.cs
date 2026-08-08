using System.Collections.Concurrent;
using CSweet.AgentRuntime.Protocol;

namespace CSweet.AgentRuntime.Guest;

public sealed class GuestBrokerSession(
    GuestServiceOptions options,
    IGuestWorkloadSupervisor workload,
    TimeProvider timeProvider)
{
    private readonly SemaphoreSlim _outputLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ProxyResponse>> _pending = new(StringComparer.Ordinal);
    private Stream? _hostOutput;

    public async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        options.Validate(timeProvider);
        using var leaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leaseDelay = options.LeaseExpiresAt - timeProvider.GetUtcNow();
        leaseCancellation.CancelAfter(leaseDelay);
        var token = leaseCancellation.Token;

        var identity = new ExpectedGuestIdentity(
            options.WorkloadId,
            options.ChannelId,
            options.GuestImageDigest,
            options.ArtifactDigest,
            options.BootToken,
            options.LeaseExpiresAt,
            options.ProtocolVersion);
        using var handshake = new GuestHandshakeClient(identity, timeProvider);
        await WriteAsync(output, new GuestEnvelope
        {
            ProtocolVersion = options.ProtocolVersion,
            MessageId = Guid.NewGuid().ToString("N"),
            Hello = handshake.CreateHello()
        }, token);

        var challenge = await ReadRequiredAsync(input, token);
        if (challenge.BodyCase != GuestEnvelope.BodyOneofCase.Challenge)
            throw new InvalidDataException("The host did not issue the expected guest challenge.");
        await WriteAsync(output, new GuestEnvelope
        {
            ProtocolVersion = options.ProtocolVersion,
            MessageId = Guid.NewGuid().ToString("N"),
            Proof = handshake.Answer(challenge.Challenge)
        }, token);

        var lease = await ReadRequiredAsync(input, token);
        if (lease.BodyCase != GuestEnvelope.BodyOneofCase.Lease || !lease.Lease.Accepted)
            throw new UnauthorizedAccessException("The host rejected the guest lease.");
        if (lease.Lease.ExpiresAtUnixSeconds != options.LeaseExpiresAt.ToUnixTimeSeconds())
            throw new InvalidDataException("The host lease does not match the boot-bound guest lease.");

        _hostOutput = output;
        await using var localProxy = new GuestLocalBrokerProxy(options.LocalBrokerSocketPath, ForwardLocalRequestAsync);
        await localProxy.StartAsync(token);
        try
        {
            while (!token.IsCancellationRequested)
            {
                var command = await LengthDelimitedProtobuf.ReadAsync(
                    input,
                    GuestEnvelope.Parser,
                    Math.Min(options.MaximumFrameBytes, lease.Lease.MaximumFrameBytes),
                    token);
                if (command is null) break;
                ValidateEnvelope(command);
                switch (command.BodyCase)
                {
                    case GuestEnvelope.BodyOneofCase.StartCommand:
                        if (command.StartCommand.WorkloadKind != options.WorkloadKind)
                            throw new InvalidDataException("The workload kind does not match the boot configuration.");
                        if (command.StartCommand.MaximumLogBytes is < 1 or > 1024L * 1024 * 1024)
                            throw new InvalidDataException("The workload log limit is invalid.");
                        try
                        {
                            await workload.StartAsync(
                                command.StartCommand.Entrypoint,
                                command.StartCommand.MaximumLogBytes,
                                token);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            await SendExitAsync(
                                output,
                                126,
                                "workload-start-failed",
                                SanitizeDetail(exception.Message),
                                CancellationToken.None);
                            return;
                        }
                        await SendHealthAsync(output, "running", token);
                        _ = ObserveExitAsync(output, token);
                        if (options.WorkloadKind == 1)
                            _ = ObserveDiagnosticsAsync(output, token);
                        break;
                    case GuestEnvelope.BodyOneofCase.ProxyResponse:
                        if (!_pending.TryRemove(command.ProxyResponse.RequestId, out var completion))
                            throw new InvalidDataException("The host returned an unknown broker response.");
                        completion.TrySetResult(command.ProxyResponse);
                        break;
                    case GuestEnvelope.BodyOneofCase.ShutdownCommand:
                        await workload.StopAsync(
                            TimeSpan.FromSeconds(Math.Clamp(command.ShutdownCommand.GracePeriodSeconds, 0, 60)),
                            cancellationToken);
                        await SendExitAsync(output, 0, command.ShutdownCommand.ReasonCode, null, cancellationToken);
                        return;
                    case GuestEnvelope.BodyOneofCase.Health:
                        await SendHealthAsync(output, workload.IsRunning ? "running" : "ready", token);
                        break;
                    default:
                        throw new InvalidDataException("The host sent an unsupported guest command.");
                }
            }
        }
        finally
        {
            _hostOutput = null;
            foreach (var pending in _pending.Values) pending.TrySetCanceled();
            _pending.Clear();
            await workload.StopAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        }
    }

    private async Task<GuestLocalBrokerResponse> ForwardLocalRequestAsync(
        GuestLocalBrokerRequest request,
        CancellationToken cancellationToken)
    {
        var output = _hostOutput ?? throw new IOException("The authenticated host broker channel is unavailable.");
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<ProxyResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion)) throw new InvalidOperationException("A broker request identifier collided.");
        try
        {
            var purpose = PurposeFor(options.WorkloadKind, request.Path);
            var proxy = new ProxyRequest
            {
                RequestId = requestId,
                Purpose = purpose,
                Method = request.Method,
                Path = request.Path,
                Body = Google.Protobuf.ByteString.CopyFrom(request.Body.Span)
            };
            foreach (var header in request.Headers) proxy.Headers.Add(header.Key, header.Value);
            await WriteAsync(output, new GuestEnvelope
            {
                ProtocolVersion = options.ProtocolVersion,
                MessageId = Guid.NewGuid().ToString("N"),
                ProxyRequest = proxy
            }, cancellationToken);
            var response = await completion.Task.WaitAsync(cancellationToken);
            return new GuestLocalBrokerResponse(
                response.StatusCode,
                response.Headers,
                response.Body.Memory);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    internal static string PurposeFor(int workloadKind, string path) => (workloadKind, path) switch
    {
        (1, "/mcp") => "mcp.runtime",
        (0, "/build/fetch") => "build.fetch",
        (0, "/build/artifact") => "build.artifact",
        (0, "/build/progress") => "build.progress",
        _ => throw new UnauthorizedAccessException("The local broker endpoint is not available to this workload.")
    };

    private async Task ObserveExitAsync(Stream output, CancellationToken cancellationToken)
    {
        try
        {
            var code = await workload.WaitForExitAsync(cancellationToken);
            await SendExitAsync(
                output,
                code,
                "process-exited",
                string.IsNullOrWhiteSpace(workload.DiagnosticDetail)
                    ? null
                    : SanitizeDetail(workload.DiagnosticDetail),
                cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (InvalidDataException)
        {
            try { await SendExitAsync(output, 137, "resource-limit-exceeded", null, CancellationToken.None); }
            catch (IOException) { }
        }
    }

    private async Task ObserveDiagnosticsAsync(Stream output, CancellationToken cancellationToken)
    {
        string? previous = null;
        long sequence = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                var detail = workload.DiagnosticDetail;
                if (string.IsNullOrWhiteSpace(detail) || string.Equals(detail, previous, StringComparison.Ordinal))
                    continue;
                previous = detail;
                await WriteAsync(output, new GuestEnvelope
                {
                    ProtocolVersion = options.ProtocolVersion,
                    MessageId = Guid.NewGuid().ToString("N"),
                    StreamChunk = new StreamChunk
                    {
                        StreamId = "runtime.logs",
                        Sequence = sequence++,
                        Content = Google.Protobuf.ByteString.CopyFromUtf8(SanitizeDetail(detail))
                    }
                }, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private Task SendHealthAsync(Stream output, string state, CancellationToken cancellationToken) =>
        WriteAsync(output, new GuestEnvelope
        {
            ProtocolVersion = options.ProtocolVersion,
            MessageId = Guid.NewGuid().ToString("N"),
            Health = new GuestHealth { State = state }
        }, cancellationToken);

    private Task SendExitAsync(
        Stream output,
        int code,
        string reason,
        string? detail,
        CancellationToken cancellationToken) =>
        WriteAsync(output, new GuestEnvelope
        {
            ProtocolVersion = options.ProtocolVersion,
            MessageId = Guid.NewGuid().ToString("N"),
            Exit = new GuestExit
            {
                ExitCode = code,
                ReasonCode = reason ?? string.Empty,
                Detail = detail ?? string.Empty
            }
        }, cancellationToken);

    private static string SanitizeDetail(string value) => new(value
        .Where(character => !char.IsControl(character) || character == ' ')
        .TakeLast(8 * 1024)
        .ToArray());

    private async Task<GuestEnvelope> ReadRequiredAsync(Stream input, CancellationToken cancellationToken)
    {
        var envelope = await LengthDelimitedProtobuf.ReadAsync(
            input,
            GuestEnvelope.Parser,
            options.MaximumFrameBytes,
            cancellationToken) ?? throw new EndOfStreamException("The host closed the guest broker channel.");
        ValidateEnvelope(envelope);
        return envelope;
    }

    private void ValidateEnvelope(GuestEnvelope envelope)
    {
        if (!string.Equals(envelope.ProtocolVersion, options.ProtocolVersion, StringComparison.Ordinal) ||
            !Guid.TryParseExact(envelope.MessageId, "N", out _))
            throw new InvalidDataException("The guest broker envelope is invalid.");
    }

    private async Task WriteAsync(Stream output, GuestEnvelope envelope, CancellationToken cancellationToken)
    {
        await _outputLock.WaitAsync(cancellationToken);
        try
        {
            await LengthDelimitedProtobuf.WriteAsync(output, envelope, options.MaximumFrameBytes, cancellationToken);
        }
        finally
        {
            _outputLock.Release();
        }
    }
}
