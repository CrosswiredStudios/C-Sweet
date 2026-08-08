using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using A = CSweet.AgentRuntime.Abstractions;
using P = CSweet.AgentRuntime.Protocol;

namespace CSweet.AgentRuntime.LocalRpc;

public sealed class RuntimeHostProviderClient(
    A.IsolationProviderDescriptor descriptor,
    RuntimeHostEndpointOptions endpointOptions,
    P.RuntimeHostRequestAuthenticator authenticator,
    ILogger<RuntimeHostProviderClient>? logger = null) : A.IRuntimeHostClient
{
    private const int ProbeAttempts = 4;

    public A.IsolationProviderDescriptor Descriptor { get; } = descriptor;

    public async Task<A.IsolationProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var response = await CallAsync(
            new P.RuntimeHostEnvelope
            {
                ProbeRequest = new P.ProbeRequest { ProviderId = Descriptor.ProviderId }
            },
            P.RuntimeHostEnvelope.BodyOneofCase.ProbeResponse,
            cancellationToken);
        var probe = response.ProbeResponse;
        A.IsolationProviderCertification? certification = null;
        if (!string.IsNullOrWhiteSpace(probe.CertificationJson))
        {
            try { certification = JsonSerializer.Deserialize<A.IsolationProviderCertification>(probe.CertificationJson); }
            catch (JsonException) { }
        }
        var probedDescriptor = Descriptor with
        {
            ProviderVersion = probe.ProviderVersion,
            HostOperatingSystem = probe.HostOperatingSystem,
            HostArchitecture = probe.HostArchitecture,
            Capabilities = Descriptor.Capabilities with { Assurance = (A.IsolationAssurance)probe.Assurance }
        };
        return new A.IsolationProviderProbeResult(
            probedDescriptor,
            probe.Available,
            string.IsNullOrWhiteSpace(probe.UnavailableReason) ? null : probe.UnavailableReason,
            certification);
    }

    public async Task<A.IsolationWorkloadHandle> CreateAsync(A.IsolationWorkloadSpec workload, CancellationToken cancellationToken = default)
    {
        var response = await CallAsync(
            new P.RuntimeHostEnvelope { CreateRequest = RuntimeHostProtocolMapper.ToProtocol(Descriptor.ProviderId, workload) },
            P.RuntimeHostEnvelope.BodyOneofCase.CreateResponse,
            cancellationToken);
        EnsureSuccess(response.CreateResponse.Success, response.CreateResponse.ErrorCode, response.CreateResponse.SanitizedError);
        return RuntimeHostProtocolMapper.FromProtocol(response.CreateResponse.Workload);
    }

    public Task StartAsync(A.IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) =>
        OperationAsync(new P.RuntimeHostEnvelope { StartRequest = RuntimeHostProtocolMapper.ToProtocol(handle) }, cancellationToken);

    public async Task<A.IsolationWorkloadStatus?> InspectAsync(A.IsolationWorkloadHandle handle, CancellationToken cancellationToken = default)
    {
        var response = await CallAsync(
            new P.RuntimeHostEnvelope { InspectRequest = RuntimeHostProtocolMapper.ToProtocol(handle) },
            P.RuntimeHostEnvelope.BodyOneofCase.InspectResponse,
            cancellationToken);
        var status = response.InspectResponse;
        if (!status.Found)
        {
            if (!string.IsNullOrWhiteSpace(status.ErrorCode))
                throw new A.IsolationUnavailableException(
                    $"Runtime-host inspect failed ({status.ErrorCode}): " +
                    $"{EmptyAsNull(status.SanitizedError) ?? "no details available"}");
            return null;
        }
        return new A.IsolationWorkloadStatus(
            RuntimeHostProtocolMapper.FromProtocol(status.Workload),
            (A.IsolationWorkloadState)status.State,
            (A.IsolationTerminationReason)status.TerminationReason,
            status.HasExitCode ? status.ExitCode : null,
            Timestamp(status.StartedAtUnixMilliseconds),
            Timestamp(status.FinishedAtUnixMilliseconds),
            EmptyAsNull(status.ErrorCode),
            EmptyAsNull(status.SanitizedError));
    }

    public Task StopAsync(A.IsolationWorkloadHandle handle, TimeSpan gracePeriod, CancellationToken cancellationToken = default)
    {
        if (gracePeriod < TimeSpan.Zero || gracePeriod > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(gracePeriod));
        return OperationAsync(new P.RuntimeHostEnvelope
        {
            StopRequest = new P.StopWorkloadRequest
            {
                Workload = RuntimeHostProtocolMapper.ToProtocol(handle),
                GracePeriodSeconds = (int)Math.Ceiling(gracePeriod.TotalSeconds)
            }
        }, cancellationToken);
    }

    public Task DestroyAsync(A.IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) =>
        OperationAsync(new P.RuntimeHostEnvelope { DestroyRequest = RuntimeHostProtocolMapper.ToProtocol(handle) }, cancellationToken);

    public async IAsyncEnumerable<A.IsolationLogChunk> StreamLogsAsync(
        A.IsolationWorkloadHandle handle,
        int maximumBytes,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (maximumBytes is < 1 or > 1024 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        await using var stream = await LocalRuntimeHostTransport.ConnectAsync(endpointOptions, cancellationToken);
        var request = Prepare(new P.RuntimeHostEnvelope
        {
            ReadLogsRequest = new P.ReadLogsRequest
            {
                Workload = RuntimeHostProtocolMapper.ToProtocol(handle),
                MaximumBytes = maximumBytes
            }
        });
        await P.LengthDelimitedProtobuf.WriteAsync(stream, request, endpointOptions.MaximumFrameBytes, cancellationToken);
        var total = 0;
        while (true)
        {
            var response = await P.LengthDelimitedProtobuf.ReadAsync(stream, P.RuntimeHostEnvelope.Parser, endpointOptions.MaximumFrameBytes, cancellationToken)
                ?? throw new EndOfStreamException("The runtime host closed the log stream unexpectedly.");
            ValidateResponse(request, response, P.RuntimeHostEnvelope.BodyOneofCase.LogChunk);
            if (response.LogChunk.Completed) yield break;
            total = checked(total + response.LogChunk.Content.Length);
            if (total > maximumBytes) throw new InvalidDataException("The runtime host exceeded the requested log limit.");
            yield return new A.IsolationLogChunk(
                DateTimeOffset.FromUnixTimeMilliseconds(response.LogChunk.OccurredAtUnixMilliseconds),
                response.LogChunk.Stream,
                response.LogChunk.Content.Memory,
                response.LogChunk.Truncated);
        }
    }

    private async Task OperationAsync(P.RuntimeHostEnvelope request, CancellationToken cancellationToken)
    {
        var response = await CallAsync(request, P.RuntimeHostEnvelope.BodyOneofCase.OperationResponse, cancellationToken);
        EnsureSuccess(response.OperationResponse.Success, response.OperationResponse.ErrorCode, response.OperationResponse.SanitizedError);
    }

    private async Task<P.RuntimeHostEnvelope> CallAsync(
        P.RuntimeHostEnvelope request,
        P.RuntimeHostEnvelope.BodyOneofCase expectedBody,
        CancellationToken cancellationToken)
    {
        var maximumAttempts = request.BodyCase == P.RuntimeHostEnvelope.BodyOneofCase.ProbeRequest
            ? ProbeAttempts
            : 1;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            var prepared = Prepare(request.Clone());
            try
            {
                logger?.LogDebug(
                    "Sending RuntimeHost {Operation} request {RuntimeHostRequestId} to provider {ProviderId} (attempt {Attempt}/{MaximumAttempts}).",
                    request.BodyCase, prepared.RequestId, Descriptor.ProviderId, attempt, maximumAttempts);
                await using var stream = await LocalRuntimeHostTransport.ConnectAsync(endpointOptions, cancellationToken);
                await P.LengthDelimitedProtobuf.WriteAsync(
                    stream, prepared, endpointOptions.MaximumFrameBytes, cancellationToken);
                var response = await P.LengthDelimitedProtobuf.ReadAsync(
                    stream, P.RuntimeHostEnvelope.Parser, endpointOptions.MaximumFrameBytes, cancellationToken);
                if (response is null)
                    throw new EndOfStreamException(
                        $"RuntimeHost closed {request.BodyCase} request {prepared.RequestId} without a response.");
                ValidateResponse(prepared, response, expectedBody);
                logger?.LogDebug(
                    "RuntimeHost {Operation} request {RuntimeHostRequestId} completed for provider {ProviderId}.",
                    request.BodyCase, prepared.RequestId, Descriptor.ProviderId);
                return response;
            }
            catch (Exception exception) when (
                attempt < maximumAttempts && IsTransientProbeFailure(exception, cancellationToken))
            {
                logger?.LogWarning(
                    exception,
                    "RuntimeHost {Operation} request {RuntimeHostRequestId} was interrupted for provider {ProviderId}; retrying attempt {NextAttempt}/{MaximumAttempts}.",
                    request.BodyCase, prepared.RequestId, Descriptor.ProviderId, attempt + 1, maximumAttempts);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
            }
            catch (EndOfStreamException exception)
            {
                logger?.LogError(
                    exception,
                    "RuntimeHost {Operation} request {RuntimeHostRequestId} returned no response for provider {ProviderId}.",
                    request.BodyCase, prepared.RequestId, Descriptor.ProviderId);
                throw new EndOfStreamException(
                    $"RuntimeHost {request.BodyCase} request {prepared.RequestId} returned no response. " +
                    "Use this request ID to correlate the C-Sweet and Windows RuntimeHost logs.",
                    exception);
            }
            catch (Exception exception)
            {
                logger?.LogError(
                    exception,
                    "RuntimeHost {Operation} request {RuntimeHostRequestId} failed for provider {ProviderId}.",
                    request.BodyCase, prepared.RequestId, Descriptor.ProviderId);
                throw;
            }
        }
        throw new UnreachableException();
    }

    private static bool IsTransientProbeFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is IOException or TimeoutException ||
        exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    private P.RuntimeHostEnvelope Prepare(P.RuntimeHostEnvelope request)
    {
        request.ProtocolVersion = "1.0";
        request.RequestId = Guid.NewGuid().ToString("N");
        authenticator.Sign(request);
        return request;
    }

    private void ValidateResponse(P.RuntimeHostEnvelope request, P.RuntimeHostEnvelope response, P.RuntimeHostEnvelope.BodyOneofCase expectedBody)
    {
        var authentication = authenticator.Validate(response);
        if (!authentication.Accepted) throw new InvalidDataException($"The runtime-host response failed authentication: {authentication.ErrorCode}.");
        if (!string.Equals(response.ProtocolVersion, "1.0", StringComparison.Ordinal) ||
            !string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal) ||
            response.BodyCase != expectedBody)
            throw new InvalidDataException("The runtime-host response envelope is invalid.");
    }

    private static void EnsureSuccess(bool success, string errorCode, string sanitizedError)
    {
        if (!success) throw new A.IsolationUnavailableException(
            $"Runtime-host operation failed ({EmptyAsNull(errorCode) ?? "unknown"}): {EmptyAsNull(sanitizedError) ?? "no details available"}");
    }

    private static DateTimeOffset? Timestamp(long value) => value == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(value);
    private static string? EmptyAsNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
