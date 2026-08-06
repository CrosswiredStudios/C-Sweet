using CSweet.AgentBroker;
using CSweet.AgentRuntime.Protocol;

namespace CSweet.UnitTests;

public sealed class AgentBrokerGrantTests
{
    [Fact]
    public void Validate_RejectsExpiredLease()
    {
        var grant = Valid() with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) };
        Assert.Throws<InvalidOperationException>(() => grant.Validate(TimeProvider.System));
    }

    [Fact]
    public void Validate_RejectsUnstructuredCapabilityPurpose()
    {
        var grant = Valid() with { AllowedPurposes = new HashSet<string> { "../../host" } };
        Assert.Throws<InvalidOperationException>(() => grant.Validate(TimeProvider.System));
    }

    [Fact]
    public void Validate_AcceptsBoundedSemanticGrant()
    {
        Valid().Validate(TimeProvider.System);
    }

    [Fact]
    public async Task HostSession_FaultsStartedWhenGuestDisconnectsBeforeHandshake()
    {
        var session = new GuestBrokerHostSession(
            Valid(), new DenyHandler(), TimeProvider.System);

        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            session.RunAsync(new MemoryStream(), new MemoryStream()));
        await Assert.ThrowsAsync<IOException>(() => session.Started);
    }

    [Fact]
    public async Task HostSession_SurfacesSanitizedGuestBootFailure()
    {
        var input = new MemoryStream();
        await LengthDelimitedProtobuf.WriteAsync(input, new GuestEnvelope
        {
            ProtocolVersion = "1.0",
            MessageId = Guid.NewGuid().ToString("N"),
            BootFailure = new GuestBootFailure
            {
                ReasonCode = "guest-boot-io-failed",
                Detail = "The artifact DVD could not be mounted."
            }
        });
        input.Position = 0;
        var session = new GuestBrokerHostSession(
            Valid(), new DenyHandler(), TimeProvider.System);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.RunAsync(input, new MemoryStream()));

        Assert.Contains("guest-boot-io-failed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("artifact DVD", exception.Message, StringComparison.Ordinal);
    }

    private static AgentBrokerGrant Valid() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        "sha256:" + new string('a', 64),
        "sha256:" + new string('b', 64),
        "1.0", "a-sufficiently-long-token", DateTimeOffset.UtcNow.AddMinutes(5),
        new HashSet<string> { "mcp.invoke", "workspace.snapshot.read" },
        100, 1024, 2048, 4096);

    private sealed class DenyHandler : IAgentBrokerOperationHandler
    {
        public Task<BrokerOperationResult> HandleAsync(
            BrokerOperationContext request,
            CancellationToken cancellationToken) =>
            throw new UnauthorizedAccessException();
    }
}
