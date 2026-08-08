using CSweet.AgentBroker;
using CSweet.AgentRuntime.Protocol;
using System.IO.Pipelines;

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

    [Fact]
    public async Task HostSession_DoesNotLetLongPollBlockAnotherProxyRequest()
    {
        var grant = Valid();
        var handler = new ConcurrentHandler();
        var session = new GuestBrokerHostSession(grant, handler, TimeProvider.System);
        var guestToHost = new Pipe();
        var hostToGuest = new Pipe();
        await using var guestOutput = guestToHost.Writer.AsStream();
        await using var guestInput = hostToGuest.Reader.AsStream();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = session.RunAsync(
            guestToHost.Reader.AsStream(),
            hostToGuest.Writer.AsStream(),
            cancellation.Token);

        var identity = new ExpectedGuestIdentity(
            grant.WorkloadId,
            grant.ChannelId,
            grant.GuestImageDigest,
            grant.ArtifactDigest,
            grant.BootToken,
            grant.ExpiresAt,
            grant.ProtocolVersion);
        using var handshake = new GuestHandshakeClient(identity, TimeProvider.System);
        await WriteGuestAsync(guestOutput, grant, new GuestEnvelope
        {
            Hello = handshake.CreateHello()
        }, cancellation.Token);
        var challenge = await ReadHostAsync(guestInput, grant, cancellation.Token);
        await WriteGuestAsync(guestOutput, grant, new GuestEnvelope
        {
            Proof = handshake.Answer(challenge.Challenge)
        }, cancellation.Token);
        var lease = await ReadHostAsync(guestInput, grant, cancellation.Token);
        Assert.True(lease.Lease.Accepted);

        await WriteGuestAsync(guestOutput, grant, ProxyEnvelope(grant, "11111111111111111111111111111111", "/slow"), cancellation.Token);
        await handler.SlowStarted.Task.WaitAsync(cancellation.Token);
        await WriteGuestAsync(guestOutput, grant, ProxyEnvelope(grant, "22222222222222222222222222222222", "/fast"), cancellation.Token);

        var fast = await ReadHostAsync(guestInput, grant, cancellation.Token);
        Assert.Equal("22222222222222222222222222222222", fast.ProxyResponse.RequestId);
        handler.ReleaseSlow();
        var slow = await ReadHostAsync(guestInput, grant, cancellation.Token);
        Assert.Equal("11111111111111111111111111111111", slow.ProxyResponse.RequestId);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    private static GuestEnvelope ProxyEnvelope(AgentBrokerGrant grant, string requestId, string path) => new()
    {
        ProtocolVersion = grant.ProtocolVersion,
        MessageId = Guid.NewGuid().ToString("N"),
        ProxyRequest = new ProxyRequest
        {
            RequestId = requestId,
            Purpose = "mcp.invoke",
            Method = "POST",
            Path = path
        }
    };

    private static Task WriteGuestAsync(
        Stream stream,
        AgentBrokerGrant grant,
        GuestEnvelope envelope,
        CancellationToken cancellationToken)
    {
        envelope.ProtocolVersion = grant.ProtocolVersion;
        envelope.MessageId = Guid.NewGuid().ToString("N");
        return LengthDelimitedProtobuf.WriteAsync(stream, envelope, grant.MaximumFrameBytes, cancellationToken);
    }

    private static async Task<GuestEnvelope> ReadHostAsync(
        Stream stream,
        AgentBrokerGrant grant,
        CancellationToken cancellationToken) =>
        await LengthDelimitedProtobuf.ReadAsync(
            stream,
            GuestEnvelope.Parser,
            grant.MaximumFrameBytes,
            cancellationToken) ?? throw new EndOfStreamException();

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

    private sealed class ConcurrentHandler : IAgentBrokerOperationHandler
    {
        private readonly TaskCompletionSource _releaseSlow = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SlowStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<BrokerOperationResult> HandleAsync(
            BrokerOperationContext request,
            CancellationToken cancellationToken)
        {
            if (request.Path == "/slow")
            {
                SlowStarted.TrySetResult();
                await _releaseSlow.Task.WaitAsync(cancellationToken);
            }
            return new BrokerOperationResult(200, new Dictionary<string, string>(), ReadOnlyMemory<byte>.Empty);
        }

        public void ReleaseSlow() => _releaseSlow.TrySetResult();
    }
}
