using System.Net;
using System.Net.Sockets;
using CSweet.AgentRuntime.Abstractions;

namespace CSweet.AgentRuntime.HyperV;

public sealed class HyperVSocketTransportOptions
{
    public const int DefaultLinuxVsockPort = 2761;
    public int LinuxVsockPort { get; set; } = DefaultLinuxVsockPort;
    public int ConnectTimeoutSeconds { get; set; } = 60;
    public int RetryDelayMilliseconds { get; set; } = 250;

    public Guid ServiceId => LinuxVsockServiceId(LinuxVsockPort);

    public void Validate()
    {
        if (LinuxVsockPort is < 1024 or > 65535)
            throw new InvalidOperationException("The Hyper-V Linux VSOCK port is invalid.");
        if (ConnectTimeoutSeconds is < 1 or > 300 || RetryDelayMilliseconds is < 25 or > 5000)
            throw new InvalidOperationException("The Hyper-V socket connection timing is invalid.");
    }

    public static Guid LinuxVsockServiceId(int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        return Guid.Parse($"{port:x8}-facb-11e6-bd58-64006a7986d3");
    }
}

public static class WindowsHyperVSocketServiceRegistration
{
    public const string RegistryBasePath =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices";

    public static string ServiceKeyPath(Guid serviceId)
    {
        if (serviceId == Guid.Empty) throw new ArgumentException("A Hyper-V socket service identifier is required.", nameof(serviceId));
        return $@"{RegistryBasePath}\{serviceId:D}";
    }

    public static string LegacyBracedServiceKeyPath(Guid serviceId)
    {
        if (serviceId == Guid.Empty) throw new ArgumentException("A Hyper-V socket service identifier is required.", nameof(serviceId));
        return $@"{RegistryBasePath}\{{{serviceId:D}}}";
    }
}

public interface IHyperVGuestTransport
{
    Task<Stream> ConnectAsync(Guid virtualMachineId, CancellationToken cancellationToken = default);
}

public sealed class WindowsHyperVSocketTransport(HyperVSocketTransportOptions options) :
    IHyperVGuestTransport, IPlatformGuestChannelConnector
{
    private const int AddressFamilyHyperV = 34;
    private const int HyperVProtocolRaw = 1;
    public string ProviderId => IsolationProviderCatalog.HyperV().ProviderId;

    public Task<Stream> OpenGuestChannelAsync(
        IsolationWorkloadHandle handle,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(handle.ProviderId, ProviderId, StringComparison.Ordinal) ||
            !Guid.TryParseExact(handle.ProviderInstanceId, "N", out var virtualMachineId))
            throw new InvalidDataException("The Hyper-V workload handle is invalid.");
        return ConnectAsync(virtualMachineId, cancellationToken);
    }

    public async Task<Stream> ConnectAsync(Guid virtualMachineId, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Hyper-V sockets require Windows.");
        if (virtualMachineId == Guid.Empty) throw new ArgumentException("A Hyper-V VM identifier is required.", nameof(virtualMachineId));
        options.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.ConnectTimeoutSeconds));
        Exception? lastError = null;
        while (!timeout.IsCancellationRequested)
        {
            var socket = new Socket((AddressFamily)AddressFamilyHyperV, SocketType.Stream, (ProtocolType)HyperVProtocolRaw);
            try
            {
                // The Windows Hyper-V Winsock provider accepts SOCKADDR_HV through
                // connect(), but the managed ConnectAsync/ConnectEx path rejects
                // this non-IP address family with WSAEINVAL (10022). A blocking
                // connect also consumes the entire readiness deadline if Linux is
                // still booting, so poll short nonblocking attempts instead.
                if (TryConnect(socket, new HyperVSocketEndPoint(virtualMachineId, options.ServiceId), timeout.Token))
                    return new NetworkStream(socket, ownsSocket: true);
                socket.Dispose();
                try { await Task.Delay(options.RetryDelayMilliseconds, timeout.Token); }
                catch (OperationCanceledException) { }
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
                lastError = exception;
                socket.Dispose();
                try { await Task.Delay(options.RetryDelayMilliseconds, timeout.Token); }
                catch (OperationCanceledException) { }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                break;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        if (cancellationToken.IsCancellationRequested) cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException(
            "The guest did not open its authenticated Hyper-V socket before the connection deadline.",
            lastError);
    }

    private bool TryConnect(Socket socket, EndPoint endpoint, CancellationToken cancellationToken)
    {
        socket.Blocking = false;
        try
        {
            socket.Connect(endpoint);
            socket.Blocking = true;
            return true;
        }
        catch (SocketException exception) when (exception.SocketErrorCode is
            SocketError.WouldBlock or SocketError.InProgress or SocketError.AlreadyInProgress)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!socket.Poll(checked(options.RetryDelayMilliseconds * 1000), SelectMode.SelectWrite))
                return false;
            var option = socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error);
            var error = option is int value
                ? value
                : throw new SocketException((int)SocketError.SocketError);
            if (error != 0) throw new SocketException(error);
            socket.Blocking = true;
            return true;
        }
    }

    internal sealed class HyperVSocketEndPoint(Guid virtualMachineId, Guid serviceId) : EndPoint
    {
        public Guid VirtualMachineId { get; } = virtualMachineId;
        public Guid ServiceId { get; } = serviceId;
        public override AddressFamily AddressFamily => (AddressFamily)AddressFamilyHyperV;

        public override SocketAddress Serialize()
        {
            var address = new SocketAddress(AddressFamily, 36);
            WriteGuid(address, 4, VirtualMachineId);
            WriteGuid(address, 20, ServiceId);
            return address;
        }

        public override EndPoint Create(SocketAddress socketAddress)
        {
            ArgumentNullException.ThrowIfNull(socketAddress);
            if (socketAddress.Family != AddressFamily || socketAddress.Size < 36)
                throw new ArgumentException("The Hyper-V socket address is invalid.", nameof(socketAddress));
            return new HyperVSocketEndPoint(ReadGuid(socketAddress, 4), ReadGuid(socketAddress, 20));
        }

        private static void WriteGuid(SocketAddress address, int offset, Guid value)
        {
            var bytes = value.ToByteArray();
            for (var index = 0; index < bytes.Length; index++) address[offset + index] = bytes[index];
        }

        private static Guid ReadGuid(SocketAddress address, int offset)
        {
            Span<byte> bytes = stackalloc byte[16];
            for (var index = 0; index < bytes.Length; index++) bytes[index] = address[offset + index];
            return new Guid(bytes);
        }
    }
}
