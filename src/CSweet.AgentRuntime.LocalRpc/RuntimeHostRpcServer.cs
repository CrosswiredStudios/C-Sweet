using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using P = CSweet.AgentRuntime.Protocol;

namespace CSweet.AgentRuntime.LocalRpc;

public sealed class RuntimeHostRpcServer(
    RuntimeHostEndpointOptions endpointOptions,
    P.RuntimeHostRequestAuthenticator authenticator,
    RuntimeHostRequestDispatcher dispatcher,
    ILogger<RuntimeHostRpcServer>? logger = null)
{
    private readonly ConcurrentDictionary<int, Task> _connections = [];
    private int _connectionId;

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        endpointOptions.Validate();
        return OperatingSystem.IsWindows()
            ? RunNamedPipeAsync(cancellationToken)
            : RunUnixSocketAsync(cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    private async Task RunNamedPipeAsync(CancellationToken cancellationToken)
    {
        var pipeSecurity = CreateWindowsPipeSecurity(endpointOptions.AllowedClientSid);
        logger?.LogInformation(
            "RuntimeHost RPC server is listening on Windows named pipe {NamedPipeName}.",
            endpointOptions.NamedPipeName);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var pipe = NamedPipeServerStreamAcl.Create(
                    endpointOptions.NamedPipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.WriteThrough,
                    0,
                    0,
                    pipeSecurity,
                    HandleInheritability.None,
                    (PipeAccessRights)0);
                try
                {
                    await pipe.WaitForConnectionAsync(cancellationToken);
                    Track(HandleOwnedStreamAsync(pipe, cancellationToken));
                    pipe = null!;
                }
                finally
                {
                    pipe?.Dispose();
                }
            }
        }
        finally
        {
            await AwaitConnectionsAsync();
            logger?.LogInformation("RuntimeHost RPC server stopped accepting Windows named-pipe requests.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static PipeSecurity CreateWindowsPipeSecurity(string? allowedClientSid)
    {
        if (string.IsNullOrWhiteSpace(allowedClientSid))
            throw new InvalidOperationException("The RuntimeHost allowed client SID is not configured.");

        SecurityIdentifier client;
        try
        {
            client = new SecurityIdentifier(allowedClientSid);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("The RuntimeHost allowed client SID is invalid.", exception);
        }

        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var server = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The RuntimeHost Windows service identity is unavailable.");
        security.AddAccessRule(new PipeAccessRule(
            server,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            client,
            // Windows maps duplex generic-write opens to FILE_CREATE_PIPE_INSTANCE (bit 0x4).
            // NamedPipeClientStream therefore requires this bit even though only RuntimeHost
            // calls the server-side creation API. The HMAC key and this explicit SID remain
            // the authorization boundary for control-plane requests.
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        return security;
    }

    [UnsupportedOSPlatform("windows")]
    private async Task RunUnixSocketAsync(CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(endpointOptions.UnixSocketPath);
        var parent = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The runtime-host socket directory is invalid.");
        Directory.CreateDirectory(parent);
        if (File.Exists(path)) File.Delete(path);

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        listener.Listen(128);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var socket = await listener.AcceptAsync(cancellationToken);
                Track(HandleOwnedStreamAsync(new NetworkStream(socket, ownsSocket: true), cancellationToken));
            }
        }
        finally
        {
            listener.Close();
            await AwaitConnectionsAsync();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private async Task HandleOwnedStreamAsync(Stream stream, CancellationToken cancellationToken)
    {
        await using (stream)
        {
            P.RuntimeHostEnvelope? request = null;
            var started = Stopwatch.GetTimestamp();
            try
            {
                request = await P.LengthDelimitedProtobuf.ReadAsync(
                    stream,
                    P.RuntimeHostEnvelope.Parser,
                    endpointOptions.MaximumFrameBytes,
                    cancellationToken);
                if (request is null)
                {
                    logger?.LogWarning("RuntimeHost client closed a connection before sending a request.");
                    return;
                }
                if (!string.Equals(request.ProtocolVersion, "1.0", StringComparison.Ordinal))
                {
                    logger?.LogWarning(
                        "RuntimeHost rejected request {RuntimeHostRequestId} with protocol version {ProtocolVersion}.",
                        request.RequestId, request.ProtocolVersion);
                    return;
                }
                var authentication = authenticator.Validate(request);
                if (!authentication.Accepted)
                {
                    logger?.LogWarning(
                        "RuntimeHost rejected unauthenticated {Operation} request {RuntimeHostRequestId}: {AuthenticationError}.",
                        request.BodyCase, request.RequestId, authentication.ErrorCode);
                    return;
                }

                logger?.LogInformation(
                    "RuntimeHost accepted {Operation} request {RuntimeHostRequestId}.",
                    request.BodyCase, request.RequestId);
                await foreach (var response in dispatcher.DispatchAsync(request, cancellationToken))
                {
                    authenticator.Sign(response);
                    await P.LengthDelimitedProtobuf.WriteAsync(
                        stream,
                        response,
                        endpointOptions.MaximumFrameBytes,
                        cancellationToken);
                }
                logger?.LogInformation(
                    "RuntimeHost completed {Operation} request {RuntimeHostRequestId} in {ElapsedMilliseconds} ms.",
                    request.BodyCase,
                    request.RequestId,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger?.LogInformation(
                    "RuntimeHost canceled {Operation} request {RuntimeHostRequestId} because the service is stopping.",
                    request?.BodyCase, request?.RequestId);
            }
            catch (Exception exception)
            {
                logger?.LogError(
                    exception,
                    "RuntimeHost failed {Operation} request {RuntimeHostRequestId} after {ElapsedMilliseconds} ms and closed the connection.",
                    request?.BodyCase,
                    request?.RequestId,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
    }

    private void Track(Task connection)
    {
        var id = Interlocked.Increment(ref _connectionId);
        _connections[id] = connection;
        _ = connection.ContinueWith(
            completed =>
            {
                if (completed.Exception is not null)
                    logger?.LogError(completed.Exception, "RuntimeHost connection {ConnectionId} failed.", id);
                _connections.TryRemove(id, out var _);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private Task AwaitConnectionsAsync() => Task.WhenAll(_connections.Values);
}
