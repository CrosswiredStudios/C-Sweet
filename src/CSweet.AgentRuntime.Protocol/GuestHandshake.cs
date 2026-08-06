using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;

namespace CSweet.AgentRuntime.Protocol;

public sealed record ExpectedGuestIdentity(
    Guid WorkloadId,
    Guid ChannelId,
    string GuestImageDigest,
    string? ArtifactDigest,
    string BootToken,
    DateTimeOffset ExpiresAt,
    string ProtocolVersion);

public sealed class GuestHandshakeClient : IDisposable
{
    private readonly ExpectedGuestIdentity _identity;
    private readonly TimeProvider _timeProvider;
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public GuestHandshakeClient(ExpectedGuestIdentity identity, TimeProvider? timeProvider = null)
    {
        _identity = identity;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public GuestHello CreateHello()
    {
        var publicKey = _key.ExportSubjectPublicKeyInfo();
        return new GuestHello
        {
            WorkloadId = _identity.WorkloadId.ToString("D"),
            ChannelId = _identity.ChannelId.ToString("D"),
            GuestImageDigest = _identity.GuestImageDigest,
            ArtifactDigest = _identity.ArtifactDigest ?? string.Empty,
            EphemeralPublicKey = ByteString.CopyFrom(publicKey),
            BootTokenProof = ByteString.CopyFrom(BootProof(_identity, publicKey))
        };
    }

    public GuestProof Answer(HostChallenge challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        if (challenge.Nonce.Length != 32)
            throw new InvalidDataException("The host challenge nonce is invalid.");
        var expiry = DateTimeOffset.FromUnixTimeSeconds(challenge.ExpiresAtUnixSeconds);
        if (expiry <= _timeProvider.GetUtcNow() || expiry > _identity.ExpiresAt)
            throw new InvalidDataException("The host challenge expiry is invalid.");
        var payload = ChallengePayload(_identity, challenge.Nonce.Span, challenge.ExpiresAtUnixSeconds);
        return new GuestProof
        {
            Signature = ByteString.CopyFrom(_key.SignData(payload, HashAlgorithmName.SHA256))
        };
    }

    public void Dispose()
    {
        _key.Dispose();
        GC.SuppressFinalize(this);
    }

    internal static byte[] BootProof(ExpectedGuestIdentity identity, ReadOnlySpan<byte> publicKey)
    {
        var key = Encoding.UTF8.GetBytes(identity.BootToken);
        if (key.Length < 16) throw new InvalidOperationException("The guest boot token is too short.");
        return HMACSHA256.HashData(key, HelloPayload(identity, publicKey));
    }

    internal static byte[] ChallengePayload(ExpectedGuestIdentity identity, ReadOnlySpan<byte> nonce, long expiresAt)
    {
        using var output = new MemoryStream();
        Write(output, "csweet-guest-challenge-v1");
        Write(output, identity.WorkloadId.ToString("D"));
        Write(output, identity.ChannelId.ToString("D"));
        Write(output, expiresAt.ToString(System.Globalization.CultureInfo.InvariantCulture));
        output.Write(nonce);
        return output.ToArray();
    }

    private static byte[] HelloPayload(ExpectedGuestIdentity identity, ReadOnlySpan<byte> publicKey)
    {
        using var output = new MemoryStream();
        Write(output, "csweet-guest-hello-v1");
        Write(output, identity.ProtocolVersion);
        Write(output, identity.WorkloadId.ToString("D"));
        Write(output, identity.ChannelId.ToString("D"));
        Write(output, identity.GuestImageDigest);
        Write(output, identity.ArtifactDigest ?? string.Empty);
        Write(output, identity.ExpiresAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        output.Write(publicKey);
        return output.ToArray();
    }

    private static void Write(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes);
        stream.WriteByte(0);
    }
}

public sealed class GuestHandshakeVerifier(
    ExpectedGuestIdentity identity,
    TimeProvider timeProvider)
{
    private ECDsa? _guestKey;
    private HostChallenge? _challenge;
    private bool _completed;

    public HostChallenge VerifyHelloAndCreateChallenge(GuestHello hello)
    {
        if (_challenge is not null || _completed) throw new InvalidOperationException("The guest handshake has already started.");
        ArgumentNullException.ThrowIfNull(hello);
        if (identity.ExpiresAt <= timeProvider.GetUtcNow()) throw new InvalidDataException("The guest boot lease has expired.");
        if (!Guid.TryParse(hello.WorkloadId, out var workloadId) || workloadId != identity.WorkloadId ||
            !Guid.TryParse(hello.ChannelId, out var channelId) || channelId != identity.ChannelId ||
            !string.Equals(hello.GuestImageDigest, identity.GuestImageDigest, StringComparison.Ordinal) ||
            !string.Equals(EmptyAsNull(hello.ArtifactDigest), identity.ArtifactDigest, StringComparison.Ordinal) ||
            hello.EphemeralPublicKey.Length is < 64 or > 1024 ||
            hello.BootTokenProof.Length != 32)
            throw new InvalidDataException("The guest hello identity is invalid.");

        var expectedProof = GuestHandshakeClient.BootProof(identity, hello.EphemeralPublicKey.Span);
        if (!CryptographicOperations.FixedTimeEquals(expectedProof, hello.BootTokenProof.Span))
            throw new InvalidDataException("The guest boot-token proof is invalid.");
        try
        {
            _guestKey = ECDsa.Create();
            _guestKey.ImportSubjectPublicKeyInfo(hello.EphemeralPublicKey.Span, out var read);
            if (read != hello.EphemeralPublicKey.Length)
                throw new InvalidDataException("The guest public key contains trailing data.");
        }
        catch (CryptographicException exception)
        {
            _guestKey?.Dispose();
            _guestKey = null;
            throw new InvalidDataException("The guest public key is invalid.", exception);
        }

        var expiry = Min(identity.ExpiresAt, timeProvider.GetUtcNow().AddMinutes(1));
        _challenge = new HostChallenge
        {
            Nonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            ExpiresAtUnixSeconds = expiry.ToUnixTimeSeconds()
        };
        return _challenge.Clone();
    }

    public GuestLease VerifyProof(GuestProof proof, int maximumFrameBytes)
    {
        ArgumentNullException.ThrowIfNull(proof);
        if (_challenge is null || _guestKey is null || _completed)
            throw new InvalidOperationException("The guest hello must be verified exactly once before its proof.");
        _completed = true;
        try
        {
            if (timeProvider.GetUtcNow() >= DateTimeOffset.FromUnixTimeSeconds(_challenge.ExpiresAtUnixSeconds))
                return Reject("challenge-expired");
            if (maximumFrameBytes is < 4096 or > LengthDelimitedProtobuf.AbsoluteMaximumFrameBytes)
                return Reject("invalid-frame-limit");
            var payload = GuestHandshakeClient.ChallengePayload(identity, _challenge.Nonce.Span, _challenge.ExpiresAtUnixSeconds);
            if (!_guestKey.VerifyData(payload, proof.Signature.Span, HashAlgorithmName.SHA256))
                return Reject("invalid-guest-proof");
            return new GuestLease
            {
                Accepted = true,
                ExpiresAtUnixSeconds = identity.ExpiresAt.ToUnixTimeSeconds(),
                MaximumFrameBytes = maximumFrameBytes
            };
        }
        finally
        {
            _guestKey.Dispose();
            _guestKey = null;
        }
    }

    private static GuestLease Reject(string reason) => new() { Accepted = false, ReasonCode = reason };
    private static string? EmptyAsNull(string value) => string.IsNullOrEmpty(value) ? null : value;
    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;
}
