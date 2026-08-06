using System.ComponentModel;
using System.Runtime.InteropServices;
using CSweet.AgentRuntime.Protocol;
using Microsoft.Win32.SafeHandles;

namespace CSweet.AgentRuntime.Guest;

public sealed record GuestBrokerConnection(Stream Input, Stream Output) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        if (ReferenceEquals(Input, Output))
        {
            await Input.DisposeAsync();
            return;
        }
        await Input.DisposeAsync();
        await Output.DisposeAsync();
    }
}

public interface IGuestBrokerTransport
{
    Task<GuestBrokerConnection> AcceptAsync(CancellationToken cancellationToken = default);
}

public sealed class StandardIoGuestBrokerTransport : IGuestBrokerTransport
{
    public Task<GuestBrokerConnection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new GuestBrokerConnection(
            Console.OpenStandardInput(), Console.OpenStandardOutput()));
    }
}

public sealed class LinuxHyperVSocketGuestTransport(int port = 2761) : IGuestBrokerTransport
{
    private const int AddressFamilyVsock = 40;
    private const int SocketStream = 1;
    private const int SocketNonBlocking = 0x800;
    private const int SocketCloseOnExec = 0x80000;
    private const int FcntlDuplicateCloseOnExec = 1030;
    private const int ErrorInterrupted = 4;
    private const int ErrorWouldBlock = 11;
    private readonly int _port = port is >= 1024 and <= 65535
        ? port
        : throw new ArgumentOutOfRangeException(nameof(port));

    public async Task<GuestBrokerConnection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The Hyper-V guest VSOCK listener requires Linux.");

        // .NET's Linux SocketPal does not map AF_VSOCK, so using Socket here
        // fails before reaching the kernel. Keep the native surface limited to
        // listener creation and expose the accepted descriptor as a Stream.
        var listener = LinuxSocket(AddressFamilyVsock, SocketStream | SocketNonBlocking | SocketCloseOnExec, 0);
        if (listener < 0) ThrowNative("create the Hyper-V guest VSOCK listener");
        try
        {
            var address = new LinuxSockAddrVm
            {
                Family = AddressFamilyVsock,
                Port = checked((uint)_port),
                ContextId = uint.MaxValue
            };
            if (LinuxBind(listener, ref address, Marshal.SizeOf<LinuxSockAddrVm>()) < 0)
                ThrowNative("bind the Hyper-V guest VSOCK listener");
            if (LinuxListen(listener, 1) < 0)
                ThrowNative("listen on the Hyper-V guest VSOCK endpoint");

            int accepted;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                accepted = LinuxAccept4(listener, IntPtr.Zero, IntPtr.Zero, SocketCloseOnExec);
                if (accepted >= 0) break;
                var error = Marshal.GetLastPInvokeError();
                if (error is not (ErrorInterrupted or ErrorWouldBlock))
                    throw new Win32Exception(error, "The Hyper-V guest VSOCK connection could not be accepted.");
                await Task.Delay(100, cancellationToken);
            }

            var inputHandle = new SafeFileHandle((IntPtr)accepted, ownsHandle: true);
            var duplicated = LinuxFcntl(accepted, FcntlDuplicateCloseOnExec, 0);
            if (duplicated < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                inputHandle.Dispose();
                throw new Win32Exception(error, "The guest could not duplicate the Hyper-V guest VSOCK descriptor.");
            }
            var outputHandle = new SafeFileHandle((IntPtr)duplicated, ownsHandle: true);
            // accept4 returns a normal synchronous Linux descriptor. FileStream's
            // isAsync flag describes how the handle was opened; marking this handle
            // asynchronous makes FileStream reject it before the broker can start.
            // Use a CLOEXEC duplicate for output because FileStream serializes async
            // operations per stream. Independent streams preserve socket full duplex
            // while the guest blocks reading its next host command.
            return OpenAcceptedConnection(inputHandle, outputHandle);
        }
        finally
        {
            LinuxClose(listener);
        }
    }

    internal static GuestBrokerConnection OpenAcceptedConnection(
        SafeFileHandle inputHandle,
        SafeFileHandle outputHandle)
    {
        FileStream? input = null;
        try
        {
            input = new FileStream(inputHandle, FileAccess.Read, 4096, isAsync: false);
            var output = new FileStream(outputHandle, FileAccess.Write, 4096, isAsync: false);
            return new GuestBrokerConnection(input, output);
        }
        catch
        {
            input?.Dispose();
            if (input is null) inputHandle.Dispose();
            outputHandle.Dispose();
            throw;
        }
    }

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    internal struct LinuxSockAddrVm
    {
        public ushort Family;
        public ushort Reserved;
        public uint Port;
        public uint ContextId;
    }

    [DllImport("libc", EntryPoint = "socket", SetLastError = true)]
    private static extern int LinuxSocket(int domain, int type, int protocol);

    [DllImport("libc", EntryPoint = "bind", SetLastError = true)]
    private static extern int LinuxBind(int socket, ref LinuxSockAddrVm address, int addressLength);

    [DllImport("libc", EntryPoint = "listen", SetLastError = true)]
    private static extern int LinuxListen(int socket, int backlog);

    [DllImport("libc", EntryPoint = "accept4", SetLastError = true)]
    private static extern int LinuxAccept4(int socket, IntPtr address, IntPtr addressLength, int flags);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int LinuxFcntl(int fileDescriptor, int command, int argument);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int LinuxClose(int fileDescriptor);

    private static void ThrowNative(string operation)
    {
        var error = Marshal.GetLastPInvokeError();
        throw new Win32Exception(error, $"The guest could not {operation}.");
    }
}

public static class GuestBootConfigurationReader
{
    public static async Task<GuestBootConfiguration> ReadAsync(
        Stream input,
        int maximumFrameBytes = 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var envelope = await LengthDelimitedProtobuf.ReadAsync(
            input, GuestEnvelope.Parser, maximumFrameBytes, cancellationToken)
            ?? throw new EndOfStreamException("The host closed the guest bootstrap channel.");
        if (envelope.BodyCase != GuestEnvelope.BodyOneofCase.BootConfiguration ||
            !Guid.TryParseExact(envelope.MessageId, "N", out _))
            throw new InvalidDataException("The host did not send a valid guest boot configuration.");
        return envelope.BootConfiguration;
    }
}
