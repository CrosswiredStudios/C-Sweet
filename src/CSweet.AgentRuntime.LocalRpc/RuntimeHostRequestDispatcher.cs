using System.Text.Json;
using A = CSweet.AgentRuntime.Abstractions;
using P = CSweet.AgentRuntime.Protocol;

namespace CSweet.AgentRuntime.LocalRpc;

public sealed class RuntimeHostRequestDispatcher(IEnumerable<A.IPlatformIsolationBackend> backends)
{
    private readonly IReadOnlyDictionary<string, A.IPlatformIsolationBackend> _backends = backends
        .ToDictionary(item => item.Descriptor.ProviderId, StringComparer.Ordinal);

    public async IAsyncEnumerable<P.RuntimeHostEnvelope> DispatchAsync(
        P.RuntimeHostEnvelope request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        switch (request.BodyCase)
        {
            case P.RuntimeHostEnvelope.BodyOneofCase.ProbeRequest:
                yield return await ProbeAsync(request, cancellationToken);
                break;
            case P.RuntimeHostEnvelope.BodyOneofCase.CreateRequest:
                yield return await CreateAsync(request, cancellationToken);
                break;
            case P.RuntimeHostEnvelope.BodyOneofCase.StartRequest:
                yield return await OperationAsync(request, request.StartRequest, static (backend, handle, token) => backend.StartAsync(handle, token), cancellationToken);
                break;
            case P.RuntimeHostEnvelope.BodyOneofCase.InspectRequest:
                yield return await InspectAsync(request, cancellationToken);
                break;
            case P.RuntimeHostEnvelope.BodyOneofCase.StopRequest:
                yield return await StopAsync(request, cancellationToken);
                break;
            case P.RuntimeHostEnvelope.BodyOneofCase.DestroyRequest:
                yield return await OperationAsync(request, request.DestroyRequest, static (backend, handle, token) => backend.DestroyAsync(handle, token), cancellationToken);
                break;
            case P.RuntimeHostEnvelope.BodyOneofCase.ReadLogsRequest:
                await foreach (var response in LogsAsync(request, cancellationToken)) yield return response;
                break;
            default:
                yield return Response(request, new P.OperationResponse
                {
                    Success = false,
                    ErrorCode = "unsupported-operation",
                    SanitizedError = "The requested runtime-host operation is not supported."
                });
                break;
        }
    }

    private async Task<P.RuntimeHostEnvelope> ProbeAsync(P.RuntimeHostEnvelope request, CancellationToken cancellationToken)
    {
        if (!_backends.TryGetValue(request.ProbeRequest.ProviderId, out var backend))
            return Response(request, new P.ProbeResponse { ProviderId = request.ProbeRequest.ProviderId, UnavailableReason = "Provider is not registered." });
        var probe = await backend.ProbeAsync(cancellationToken);
        return Response(request, new P.ProbeResponse
        {
            ProviderId = probe.Descriptor.ProviderId,
            ProviderVersion = probe.Descriptor.ProviderVersion,
            HostOperatingSystem = probe.Descriptor.HostOperatingSystem,
            HostArchitecture = probe.Descriptor.HostArchitecture,
            Assurance = (int)probe.Descriptor.Capabilities.Assurance,
            Available = probe.IsAvailable,
            UnavailableReason = probe.UnavailableReason ?? string.Empty,
            CertificationJson = probe.Certification is null ? string.Empty : JsonSerializer.Serialize(probe.Certification)
        });
    }

    private async Task<P.RuntimeHostEnvelope> CreateAsync(P.RuntimeHostEnvelope request, CancellationToken cancellationToken)
    {
        if (!_backends.TryGetValue(request.CreateRequest.ProviderId, out var backend)) return Error(request, "provider-not-registered");
        try
        {
            var workload = RuntimeHostProtocolMapper.FromProtocol(request.CreateRequest);
            var handle = await backend.CreateAsync(workload, cancellationToken);
            EnsureProvider(handle, backend);
            return Response(request, new P.WorkloadHandleResponse { Success = true, Workload = RuntimeHostProtocolMapper.ToProtocol(handle) });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException)
        {
            return ErrorHandle(request, "invalid-workload", "The isolated workload specification was rejected.");
        }
    }

    private async Task<P.RuntimeHostEnvelope> InspectAsync(P.RuntimeHostEnvelope request, CancellationToken cancellationToken)
    {
        var (backend, handle) = Resolve(request.InspectRequest);
        if (backend is null || handle is null) return Response(request, new P.WorkloadStatusResponse { Found = false });
        var status = await backend.InspectAsync(handle, cancellationToken);
        if (status is null) return Response(request, new P.WorkloadStatusResponse { Found = false });
        EnsureProvider(status.Handle, backend);
        var response = new P.WorkloadStatusResponse
        {
            Found = true,
            Workload = RuntimeHostProtocolMapper.ToProtocol(status.Handle),
            State = (int)status.State,
            TerminationReason = (int)status.TerminationReason,
            StartedAtUnixMilliseconds = status.StartedAt?.ToUnixTimeMilliseconds() ?? 0,
            FinishedAtUnixMilliseconds = status.FinishedAt?.ToUnixTimeMilliseconds() ?? 0,
            ErrorCode = status.ErrorCode ?? string.Empty,
            SanitizedError = status.SanitizedError ?? string.Empty
        };
        if (status.ExitCode.HasValue) response.ExitCode = status.ExitCode.Value;
        return Response(request, response);
    }

    private async Task<P.RuntimeHostEnvelope> StopAsync(P.RuntimeHostEnvelope request, CancellationToken cancellationToken)
    {
        var seconds = request.StopRequest.GracePeriodSeconds;
        if (seconds is < 0 or > 300) return Error(request, "invalid-grace-period");
        return await OperationAsync(
            request,
            request.StopRequest.Workload,
            (backend, handle, token) => backend.StopAsync(handle, TimeSpan.FromSeconds(seconds), token),
            cancellationToken);
    }

    private async Task<P.RuntimeHostEnvelope> OperationAsync(
        P.RuntimeHostEnvelope request,
        P.WorkloadOperationRequest protocolHandle,
        Func<A.IPlatformIsolationBackend, A.IsolationWorkloadHandle, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        var (backend, handle) = Resolve(protocolHandle);
        if (backend is null || handle is null) return Error(request, "provider-not-registered");
        try
        {
            await operation(backend, handle, cancellationToken);
            return Response(request, new P.OperationResponse { Success = true });
        }
        catch (KeyNotFoundException) { return Error(request, "workload-not-found"); }
        catch (Exception) { return Error(request, "provider-operation-failed"); }
    }

    private async IAsyncEnumerable<P.RuntimeHostEnvelope> LogsAsync(P.RuntimeHostEnvelope request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var maximum = request.ReadLogsRequest.MaximumBytes;
        var (backend, handle) = Resolve(request.ReadLogsRequest.Workload);
        if (backend is null || handle is null || maximum is < 1 or > 1024 * 1024 * 1024)
        {
            yield return Response(request, new P.LogChunk { Completed = true, Truncated = true });
            yield break;
        }
        var total = 0;
        await foreach (var chunk in backend.StreamLogsAsync(handle, maximum, cancellationToken))
        {
            total = checked(total + chunk.Content.Length);
            if (total > maximum) break;
            yield return Response(request, new P.LogChunk
            {
                OccurredAtUnixMilliseconds = chunk.OccurredAt.ToUnixTimeMilliseconds(),
                Stream = chunk.Stream,
                Content = Google.Protobuf.ByteString.CopyFrom(chunk.Content.Span),
                Truncated = chunk.IsTruncated
            });
        }
        yield return Response(request, new P.LogChunk { Completed = true, Truncated = total > maximum });
    }

    private (A.IPlatformIsolationBackend? Backend, A.IsolationWorkloadHandle? Handle) Resolve(P.WorkloadOperationRequest protocol)
    {
        if (!_backends.TryGetValue(protocol.ProviderId, out var backend)) return (null, null);
        try
        {
            var handle = RuntimeHostProtocolMapper.FromProtocol(protocol);
            EnsureProvider(handle, backend);
            return (backend, handle);
        }
        catch (InvalidDataException) { return (null, null); }
    }

    private static void EnsureProvider(A.IsolationWorkloadHandle handle, A.IAgentIsolationProvider provider)
    {
        if (!string.Equals(handle.ProviderId, provider.Descriptor.ProviderId, StringComparison.Ordinal))
            throw new InvalidDataException("The provider returned a workload handle for another provider.");
    }

    private static P.RuntimeHostEnvelope Error(P.RuntimeHostEnvelope request, string code) => Response(request, new P.OperationResponse
    {
        Success = false,
        ErrorCode = code,
        SanitizedError = "The runtime-host operation was rejected."
    });

    private static P.RuntimeHostEnvelope ErrorHandle(P.RuntimeHostEnvelope request, string code, string message) => Response(request, new P.WorkloadHandleResponse
    {
        Success = false,
        ErrorCode = code,
        SanitizedError = message
    });

    private static P.RuntimeHostEnvelope Response(P.RuntimeHostEnvelope request, P.ProbeResponse body)
    {
        var response = Base(request);
        response.ProbeResponse = body;
        return response;
    }

    private static P.RuntimeHostEnvelope Response(P.RuntimeHostEnvelope request, P.WorkloadHandleResponse body)
    {
        var response = Base(request);
        response.CreateResponse = body;
        return response;
    }

    private static P.RuntimeHostEnvelope Response(P.RuntimeHostEnvelope request, P.WorkloadStatusResponse body)
    {
        var response = Base(request);
        response.InspectResponse = body;
        return response;
    }

    private static P.RuntimeHostEnvelope Response(P.RuntimeHostEnvelope request, P.OperationResponse body)
    {
        var response = Base(request);
        response.OperationResponse = body;
        return response;
    }

    private static P.RuntimeHostEnvelope Response(P.RuntimeHostEnvelope request, P.LogChunk body)
    {
        var response = Base(request);
        response.LogChunk = body;
        return response;
    }

    private static P.RuntimeHostEnvelope Base(P.RuntimeHostEnvelope request) => new()
    {
        ProtocolVersion = request.ProtocolVersion,
        RequestId = request.RequestId
    };
}
