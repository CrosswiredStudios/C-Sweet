using System.Runtime.InteropServices;
using System.Text.Json;
using CSweet.Compute.Contracts;
using Microsoft.Win32.SafeHandles;

namespace CSweet.Compute.Guest;

/// <summary>Hyper-V Linux guest listener. Only host CID 2 is admitted; no TCP fallback.</summary>
public static class LinuxComputeGuestServer
{
    public static async Task RunAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/sys/bus/vmbus"))
            throw new PlatformNotSupportedException("A Linux Hyper-V guest is required.");
        if (!Directory.Exists("/run/systemd/system") || !File.Exists("/usr/bin/systemd-run"))
            throw new IOException("The Linux guest requires systemd 254 or later.");
        var handler = new ComputeGuestConnection(new ComputeGuestExecution("linux",
            command => new LinuxComputeGuestProcess(command, Path.GetTempPath())));
        var listener = Native.socket(40, 1 | 0x800 | 0x80000, 0);
        if (listener < 0) throw Failure();
        var connections = new List<Task>();
        try
        {
            var address = new Address { Family = 40, Port = ComputeGuestWire.Port, Cid = uint.MaxValue };
            if (Native.bind(listener, ref address, 16) != 0 || Native.listen(listener, 8) != 0) throw Failure();
            while (!token.IsCancellationRequested)
            {
                foreach (var failed in connections.Where(task => task.IsFaulted)) _ = failed.Exception;
                connections.RemoveAll(task => task.IsCompleted);
                if (connections.Count >= 8) { await Task.WhenAny(connections).WaitAsync(token); continue; }
                var peer = new Address(); uint size = 16;
                var accepted = Native.accept4(listener, ref peer, ref size, 0x80000);
                if (accepted < 0)
                {
                    if (Marshal.GetLastPInvokeError() is not (4 or 11)) throw Failure();
                    await Task.Delay(50, token); continue;
                }
                if (size != 16 || peer.Family != 40 || peer.Cid != 2) { Native.close(accepted); continue; }
                connections.Add(ServeAsync(accepted));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { Native.close(listener); await Task.WhenAll(connections); }

        async Task ServeAsync(int fd)
        {
            await using var stream = new Connection(fd);
            try { await handler.ServeAsync(stream, token); }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is System.Net.Sockets.SocketException or IOException or JsonException or ArgumentException or InvalidOperationException)
            { Console.Error.WriteLine("compute-guest-request-failed"); }
        }
    }

    private sealed class Connection : Stream
    {
        private readonly int fd;
        private readonly FileStream input;
        private readonly FileStream output;
        public Connection(int fd)
        {
            this.fd = fd;
            var readHandle = new SafeFileHandle((IntPtr)fd, true);
            var duplicate = Native.fcntl(fd, 1030, 0); // F_DUPFD_CLOEXEC
            if (duplicate < 0) { readHandle.Dispose(); throw Failure(); }
            var writeHandle = new SafeFileHandle((IntPtr)duplicate, true);
            try
            {
                input = new FileStream(readHandle, FileAccess.Read, 4096, false);
                output = new FileStream(writeHandle, FileAccess.Write, 4096, false);
            }
            catch { readHandle.Dispose(); writeHandle.Dispose(); throw; }
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            using var cancel = token.Register(() => Native.shutdown(fd, 2));
            return await input.ReadAsync(buffer, token);
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            using var cancel = token.Register(() => Native.shutdown(fd, 2));
            await output.WriteAsync(buffer, token);
        }
        public override async Task FlushAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var cancel = token.Register(() => Native.shutdown(fd, 2));
            await output.FlushAsync(token);
        }
        public override void Flush() => output.Flush();
        protected override void Dispose(bool disposing) { if (disposing) { input.Dispose(); output.Dispose(); } base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static IOException Failure() => new("Linux guest VSOCK operation failed.");
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct Address { public ushort Family, Reserved; public uint Port, Cid; }
    private static class Native
    {
        [DllImport("libc", SetLastError = true)] internal static extern int socket(int family, int type, int protocol);
        [DllImport("libc", SetLastError = true)] internal static extern int bind(int fd, ref Address address, int size);
        [DllImport("libc", SetLastError = true)] internal static extern int listen(int fd, int backlog);
        [DllImport("libc", SetLastError = true)] internal static extern int accept4(int fd, ref Address address, ref uint size, int flags);
        [DllImport("libc", SetLastError = true)] internal static extern int fcntl(int fd, int command, int value);
        [DllImport("libc")] internal static extern int shutdown(int fd, int how);
        [DllImport("libc")] internal static extern int close(int fd);
    }
}
