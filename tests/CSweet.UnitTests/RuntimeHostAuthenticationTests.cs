using System.Security.Cryptography;
using CSweet.AgentRuntime.Protocol;

namespace CSweet.UnitTests;

public sealed class RuntimeHostAuthenticationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Validate_AcceptsSignedEnvelopeOnce()
    {
        var authenticator = CreateAuthenticator();
        var envelope = Request();
        authenticator.Sign(envelope);

        Assert.True(authenticator.Validate(envelope).Accepted);
        Assert.Equal("replayed-request", authenticator.Validate(envelope).ErrorCode);
    }

    [Fact]
    public void Validate_RejectsChangedBody()
    {
        var authenticator = CreateAuthenticator();
        var envelope = Request();
        authenticator.Sign(envelope);
        envelope.ProbeRequest.ProviderId = "firecracker";

        Assert.Equal("invalid-signature", authenticator.Validate(envelope).ErrorCode);
    }

    [Fact]
    public void Validate_RejectsExpiredEnvelope()
    {
        var signer = CreateAuthenticator(Now.AddMinutes(-5));
        var validator = CreateAuthenticator(Now);
        var envelope = Request();
        signer.Sign(envelope);

        Assert.Equal("expired-request", validator.Validate(envelope).ErrorCode);
    }

    [Fact]
    public void LoadSharedKeyFileIfNeeded_LoadsBoundedNonReparseKeyFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            File.WriteAllText(path, key);
            var options = new RuntimeHostAuthenticationOptions();

            options.LoadSharedKeyFileIfNeeded(path);

            Assert.Equal(key, options.SharedKeyBase64);
            Assert.Equal(Path.GetFullPath(path), options.SharedKeyFilePath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Authenticator_LoadsKeyCreatedAfterControlPlaneStartup()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"csweet-runtime-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "runtime-host.key");
        try
        {
            var options = new RuntimeHostAuthenticationOptions { KeyId = "test" };
            options.LoadSharedKeyFileIfNeeded(path);
            var authenticator = new RuntimeHostRequestAuthenticator(options, new FixedTimeProvider(Now));

            Assert.Equal(Path.GetFullPath(path), options.SharedKeyFilePath);
            Assert.Throws<InvalidDataException>(() => authenticator.Sign(Request()));

            File.WriteAllText(path, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            var envelope = Request();
            authenticator.Sign(envelope);

            Assert.True(authenticator.Validate(envelope).Accepted);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static RuntimeHostRequestAuthenticator CreateAuthenticator(DateTimeOffset? now = null) =>
        new(new RuntimeHostAuthenticationOptions
        {
            KeyId = "test",
            SharedKeyBase64 = Convert.ToBase64String(new byte[32]),
            MaximumClockSkewSeconds = 60
        }, new FixedTimeProvider(now ?? Now));

    private static RuntimeHostEnvelope Request() => new()
    {
        ProtocolVersion = "1.0",
        RequestId = Guid.NewGuid().ToString("N"),
        ProbeRequest = new ProbeRequest { ProviderId = "hyperv" }
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
