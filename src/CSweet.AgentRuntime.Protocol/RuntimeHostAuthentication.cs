using System.Collections.Concurrent;
using System.Security.Cryptography;
using Google.Protobuf;

namespace CSweet.AgentRuntime.Protocol;

public sealed class RuntimeHostAuthenticationOptions
{
    public const string SectionName = "CSweet:AgentRuntime:HostAuthentication";

    public string KeyId { get; set; } = "control-plane";
    public string SharedKeyBase64 { get; set; } = string.Empty;
    public string SharedKeyFilePath { get; set; } = string.Empty;
    public int MaximumClockSkewSeconds { get; set; } = 60;
    public int ReplayRetentionSeconds { get; set; } = 300;

    public void LoadSharedKeyFileIfNeeded(string defaultPath)
    {
        if (!string.IsNullOrWhiteSpace(SharedKeyBase64)) return;
        var path = string.IsNullOrWhiteSpace(SharedKeyFilePath) ? defaultPath : SharedKeyFilePath;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return;
        path = Path.GetFullPath(path);
        SharedKeyFilePath = path;
        SharedKeyBase64 = ReadSharedKeyFile(path) ?? string.Empty;
    }

    internal string ResolveSharedKeyBase64()
    {
        if (!string.IsNullOrWhiteSpace(SharedKeyFilePath))
            return ReadSharedKeyFile(SharedKeyFilePath) ?? string.Empty;
        return SharedKeyBase64;
    }

    private static string? ReadSharedKeyFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is < 40 or > 4096 ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0)
                return null;
            return File.ReadAllText(path).Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}

public sealed class RuntimeHostRequestAuthenticator(
    RuntimeHostAuthenticationOptions options,
    TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nonces = new(StringComparer.Ordinal);

    public void Sign(RuntimeHostEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        envelope.AuthenticationKeyId = options.KeyId;
        envelope.AuthenticationTimestampUnixSeconds = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        envelope.AuthenticationNonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32));
        envelope.AuthenticationSignature = ByteString.Empty;
        envelope.AuthenticationSignature = ByteString.CopyFrom(ComputeSignature(envelope));
    }

    public RuntimeHostAuthenticationResult Validate(RuntimeHostEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!string.Equals(envelope.AuthenticationKeyId, options.KeyId, StringComparison.Ordinal))
            return RuntimeHostAuthenticationResult.Reject("unknown-key");
        if (envelope.AuthenticationNonce.Length != 32 || envelope.AuthenticationSignature.Length != 32)
            return RuntimeHostAuthenticationResult.Reject("invalid-authentication-envelope");

        var now = timeProvider.GetUtcNow();
        DateTimeOffset timestamp;
        try { timestamp = DateTimeOffset.FromUnixTimeSeconds(envelope.AuthenticationTimestampUnixSeconds); }
        catch (ArgumentOutOfRangeException) { return RuntimeHostAuthenticationResult.Reject("invalid-timestamp"); }
        if ((now - timestamp).Duration() > TimeSpan.FromSeconds(Math.Clamp(options.MaximumClockSkewSeconds, 1, 300)))
            return RuntimeHostAuthenticationResult.Reject("expired-request");

        var expected = ComputeSignature(envelope);
        if (!CryptographicOperations.FixedTimeEquals(expected, envelope.AuthenticationSignature.Span))
            return RuntimeHostAuthenticationResult.Reject("invalid-signature");

        Prune(now);
        var nonce = Convert.ToHexString(envelope.AuthenticationNonce.Span);
        if (!_nonces.TryAdd(nonce, now.AddSeconds(Math.Clamp(options.ReplayRetentionSeconds, 60, 3600))))
            return RuntimeHostAuthenticationResult.Reject("replayed-request");
        return RuntimeHostAuthenticationResult.Accept();
    }

    private byte[] ComputeSignature(RuntimeHostEnvelope envelope)
    {
        var canonical = envelope.Clone();
        canonical.AuthenticationSignature = ByteString.Empty;
        return HMACSHA256.HashData(ParseKey(options.ResolveSharedKeyBase64()), canonical.ToByteArray());
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var nonce in _nonces.Where(item => item.Value <= now).Select(item => item.Key))
            _nonces.TryRemove(nonce, out _);
    }

    private static byte[] ParseKey(string keyBase64)
    {
        byte[] key;
        try { key = Convert.FromBase64String(keyBase64); }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The runtime-host shared key is not available or is not valid Base64.", exception);
        }
        if (key.Length < 32)
            throw new InvalidDataException("The runtime-host shared key is not available or contains fewer than 32 bytes.");
        return key;
    }
}

public sealed record RuntimeHostAuthenticationResult(bool Accepted, string? ErrorCode)
{
    public static RuntimeHostAuthenticationResult Accept() => new(true, null);
    public static RuntimeHostAuthenticationResult Reject(string errorCode) => new(false, errorCode);
}
