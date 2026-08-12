using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CSweet.AgentRuntime.Abstractions;

namespace CSweet.AgentRuntime.Core;

/// <summary>
/// Opens a provider-owned vsock/virtio-socket tunnel through a privileged native helper.
/// The helper reads one JSON request line, writes one bounded JSON response line, and then
/// switches the same standard streams to opaque guest broker bytes.
/// </summary>
public class ExternalPlatformStdioGuestChannelConnector(
    string providerId,
    PlatformIsolationBackendOptions options) : IPlatformGuestChannelConnector
{
    public const string TransportName = "stdio-duplex-v1";
    private const int MaximumHandshakeBytes = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string ProviderId { get; } = providerId;

    public async Task<Stream> OpenGuestChannelAsync(
        IsolationWorkloadHandle handle,
        CancellationToken cancellationToken = default)
    {
        ValidateHandle(handle, ProviderId);
        if (!string.Equals(options.RequiredGuestChannelTransport, TransportName, StringComparison.Ordinal))
            throw new IsolationUnavailableException("The certified helper guest-channel transport is not enabled.");
        if (string.IsNullOrWhiteSpace(options.HelperExecutablePath) ||
            !Path.IsPathFullyQualified(options.HelperExecutablePath))
            throw new IsolationUnavailableException("The privileged platform helper path is unavailable.");
        var helper = Path.GetFullPath(options.HelperExecutablePath);
        if (!File.Exists(helper))
            throw new IsolationUnavailableException("The privileged platform helper is not installed.");
        if (!await VerifyHelperDigestAsync(helper, options.HelperExecutableDigest, cancellationToken))
            throw new IsolationUnavailableException("The privileged platform helper failed its integrity check.");

        var start = new ProcessStartInfo
        {
            FileName = helper,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--protocol");
        start.ArgumentList.Add("1.0");
        start.ArgumentList.Add("--operation");
        start.ArgumentList.Add("open-guest-channel");
        var process = Process.Start(start) ??
            throw new IsolationUnavailableException("The privileged platform helper could not be started.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(
                Math.Clamp(options.GuestChannelConnectTimeoutSeconds, 5, 120)));
            var payload = JsonSerializer.Serialize(
                new PlatformHelperRequest { Handle = handle }, JsonOptions) + "\n";
            await process.StandardInput.WriteAsync(payload.AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
            var response = await ReadHandshakeAsync(process.StandardOutput.BaseStream, timeout.Token);
            if (!response.Success)
                throw new IsolationUnavailableException(
                    $"The platform helper rejected the guest channel ({Sanitize(response.ErrorCode)}).");
            ValidateHandshake(response);
            _ = DrainStandardErrorAsync(process.StandardError.BaseStream);
            return new HelperDuplexStream(process);
        }
        catch
        {
            TryKill(process);
            process.Dispose();
            throw;
        }
    }

    internal static async Task<PlatformHelperResponse> ReadHandshakeAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var line = new MemoryStream();
        var single = new byte[1];
        while (line.Length <= MaximumHandshakeBytes)
        {
            var read = await stream.ReadAsync(single, cancellationToken);
            if (read == 0) throw new EndOfStreamException("The platform helper closed before acknowledging the guest channel.");
            if (single[0] == (byte)'\n')
            {
                if (line.Length == 0) throw new InvalidDataException("The platform helper guest-channel response was empty.");
                return JsonSerializer.Deserialize<PlatformHelperResponse>(line.ToArray(), JsonOptions)
                    ?? throw new InvalidDataException("The platform helper guest-channel response was empty.");
            }
            if (single[0] == 0 || single[0] == (byte)'\r')
                throw new InvalidDataException("The platform helper guest-channel response framing is invalid.");
            line.WriteByte(single[0]);
        }
        throw new InvalidDataException("The platform helper guest-channel response exceeded its limit.");
    }

    internal static void ValidateHandle(IsolationWorkloadHandle handle, string expectedProviderId)
    {
        if (handle.WorkloadId == Guid.Empty ||
            !string.Equals(handle.ProviderId, expectedProviderId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(handle.ProviderInstanceId) ||
            handle.ProviderInstanceId.Length > 256 ||
            handle.ProviderInstanceId.Any(char.IsControl))
            throw new InvalidDataException("The workload handle is invalid for this guest-channel provider.");
    }

    internal static void ValidateHandshake(PlatformHelperResponse response)
    {
        if (!string.Equals(response.GuestChannelTransport, TransportName, StringComparison.Ordinal))
            throw new IsolationUnavailableException(
                "The platform helper did not confirm the certified guest-channel transport.");
    }

    private static async Task DrainStandardErrorAsync(Stream stream)
    {
        try
        {
            var buffer = new byte[4096];
            while (await stream.ReadAsync(buffer) > 0) { }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }

    private static string Sanitize(string? value) => string.IsNullOrWhiteSpace(value)
        ? "unspecified"
        : new string(value.Where(character => !char.IsControl(character)).Take(128).ToArray());

    private static async Task<bool> VerifyHelperDigestAsync(
        string path,
        string expectedDigest,
        CancellationToken cancellationToken)
    {
        if (expectedDigest.Length != 71 ||
            !expectedDigest.StartsWith("sha256:", StringComparison.Ordinal) ||
            expectedDigest.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") >= 0)
            return false;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = $"sha256:{Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken))}";
            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expectedDigest));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    private sealed class HelperDuplexStream(Process process) : Stream
    {
        private readonly Stream _read = process.StandardOutput.BaseStream;
        private readonly Stream _write = process.StandardInput.BaseStream;
        private int _disposed;

        public override bool CanRead => _disposed == 0 && _read.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _disposed == 0 && _write.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _write.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _write.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _read.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _write.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (disposing)
            {
                try { _write.Dispose(); } catch (IOException) { }
                try { _read.Dispose(); } catch (IOException) { }
                TryKill(process);
                process.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { await _write.DisposeAsync(); } catch (IOException) { }
            try { await _read.DisposeAsync(); } catch (IOException) { }
            TryKill(process);
            process.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
