using CSweet.Office.Contracts.Guest;

namespace CSweet.UnitTests;

public sealed class GuestHandshakeTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Handshake_BindsGuestToWorkloadImageArtifactAndChannel()
    {
        var identity = Identity();
        using var guest = new GuestHandshakeClient(identity, new FixedTimeProvider(Now));
        var host = new GuestHandshakeVerifier(identity, new FixedTimeProvider(Now));

        var challenge = host.VerifyHelloAndCreateChallenge(guest.CreateHello());
        var lease = host.VerifyProof(guest.Answer(challenge), 1024 * 1024);

        Assert.True(lease.Accepted);
        Assert.Equal(identity.ExpiresAt.ToUnixTimeSeconds(), lease.ExpiresAtUnixSeconds);
    }

    [Fact]
    public void VerifyHello_RejectsAnotherArtifact()
    {
        var expected = Identity();
        var other = expected with { ArtifactDigest = "sha256:" + new string('c', 64) };
        using var guest = new GuestHandshakeClient(other, new FixedTimeProvider(Now));
        var host = new GuestHandshakeVerifier(expected, new FixedTimeProvider(Now));

        Assert.Throws<InvalidDataException>(() => host.VerifyHelloAndCreateChallenge(guest.CreateHello()));
    }

    [Fact]
    public void VerifyProof_IsSingleUse()
    {
        var identity = Identity();
        using var guest = new GuestHandshakeClient(identity, new FixedTimeProvider(Now));
        var host = new GuestHandshakeVerifier(identity, new FixedTimeProvider(Now));
        var challenge = host.VerifyHelloAndCreateChallenge(guest.CreateHello());
        var proof = guest.Answer(challenge);

        Assert.True(host.VerifyProof(proof, 1024 * 1024).Accepted);
        Assert.Throws<InvalidOperationException>(() => host.VerifyProof(proof, 1024 * 1024));
    }

    private static ExpectedGuestIdentity Identity() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "sha256:" + new string('a', 64),
        "sha256:" + new string('b', 64),
        Convert.ToBase64String(new byte[32]),
        Now.AddMinutes(5),
        "1.0");

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
