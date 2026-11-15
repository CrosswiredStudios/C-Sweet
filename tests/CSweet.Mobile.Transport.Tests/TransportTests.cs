using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace CSweet.Mobile.Transport.Tests;

/// <summary>P0-07 rows a–c and f: bounded stream, pinned mutual TLS through relay frames, fault injection.</summary>
public sealed class TransportTests : IAsyncDisposable
{
    private const string Route = "route-1";
    private readonly InMemoryRelay _relay = new();
    private readonly ECDsa _routeKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _headquartersKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _phoneKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly X509Certificate2 _headquartersCertificate;
    private readonly X509Certificate2 _phoneCertificate;
    private readonly List<PhoneChannel> _served = [];
    private HeadquartersRelayConnector? _connector;
    private Func<string, bool> _enrolled;

    public TransportTests()
    {
        _headquartersCertificate = PinnedTls.CreateCertificate(_headquartersKey, "Northstar Headquarters", TimeSpan.FromDays(365));
        _phoneCertificate = PinnedTls.CreateCertificate(_phoneKey, "Maya's phone", TimeSpan.FromDays(365));
        _enrolled = pin => pin == RelayProtocol.Thumbprint(_phoneKey);
    }

    private string HeadquartersPin => RelayProtocol.Thumbprint(_headquartersKey);

    [Fact]
    public void Proof_strings_match_the_relay_exactly()
    {
        Assert.Equal("csweet-relay-attach\nroute-1\nN", RelayProtocol.AttachMessage("route-1", "N"));
        Assert.Equal("csweet-relay-open\nroute-1\nN", RelayProtocol.OpenMessage("route-1", "N"));
        Assert.Equal("csweet-relay-ticket\nroute-1\nT\n42", RelayProtocol.TicketMessage("route-1", "T", 42));
        Assert.Equal(PinnedTls.Pin(_headquartersCertificate), HeadquartersPin);
        var ticket = RelayProtocol.IssueTicket(_routeKey, Route, "T", DateTimeOffset.FromUnixTimeSeconds(42));
        var body = ticket.Split('.')[0];
        Assert.Equal("csweet-relay-ticket\nroute-1\nT\n42", Encoding.UTF8.GetString(Decode(body)));
        Assert.True(_routeKey.VerifyData(Decode(body), Decode(ticket.Split('.')[1]), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public async Task Pinned_mutual_TLS_through_the_relay_echoes_a_megabyte_unchanged()
    {
        await StartHeadquarters();
        await using var session = await Phone();
        Assert.True(session.Protocol is SslProtocols.Tls12 or SslProtocols.Tls13);

        var payload = RandomNumberGenerator.GetBytes(1024 * 1024);
        var echo = new byte[payload.Length];
        var reading = ReadExactly(session.Stream, echo);
        await session.Stream.WriteAsync(payload);
        await reading.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(payload, echo);

        var served = Assert.Single(_served);
        Assert.Equal(RelayProtocol.Thumbprint(_phoneKey), served.PhonePin);
    }

    [Fact]
    public async Task The_phone_refuses_a_headquarters_with_the_wrong_pin()
    {
        await StartHeadquarters();
        using var impostor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await Assert.ThrowsAnyAsync<AuthenticationException>(() => Phone(headquartersPin: RelayProtocol.Thumbprint(impostor)));
        // On some platforms the pin is checked just after the TLS handshake, so Headquarters may briefly see a session;
        // the phone sends nothing over it and the channel closes.
        await WaitUntil(() => _relay.OpenChannels == 0);
    }

    [Fact]
    public async Task Headquarters_refuses_a_phone_key_that_is_not_enrolled()
    {
        _enrolled = _ => false;
        await StartHeadquarters();
        await AssertRefused(() => Phone());
        Assert.Empty(_served);
    }

    [Fact]
    public async Task The_TLS_client_key_must_be_the_key_the_relay_admitted()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherCertificate = PinnedTls.CreateCertificate(other, "Other phone", TimeSpan.FromDays(1));
        _enrolled = _ => true; // even if both keys were enrolled
        await StartHeadquarters();
        await AssertRefused(() => Phone(certificate: otherCertificate));
        Assert.Empty(_served);
    }

    [Theory]
    [InlineData(FrameAction.Drop)]
    [InlineData(FrameAction.Duplicate)]
    [InlineData(FrameAction.Corrupt)]
    public async Task A_damaged_frame_ends_the_session_and_a_new_channel_starts_a_fresh_one(FrameAction fault)
    {
        await StartHeadquarters();
        await using (var session = await Phone())
        {
            var faultAt = -1;
            _relay.Faults = (index, _) =>
            {
                if (faultAt < 0) faultAt = index + 3;
                return index == faultAt ? fault : FrameAction.Deliver;
            };
            var payload = RandomNumberGenerator.GetBytes(256 * 1024);
            var echo = new byte[payload.Length];
            var failed = false;
            try
            {
                var reading = ReadExactly(session.Stream, echo);
                await session.Stream.WriteAsync(payload);
                await reading.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception error) when (error is IOException or AuthenticationException or TimeoutException or EndOfStreamException)
            {
                failed = true;
            }
            // TLS records carry implicit sequence numbers and MACs: a lost, replayed or altered record is never accepted.
            Assert.True(failed);
        }

        _relay.Faults = (_, _) => FrameAction.Deliver;
        await using var fresh = await Phone();
        var data = Encoding.UTF8.GetBytes("hello after recovery");
        var back = new byte[data.Length];
        var r = ReadExactly(fresh.Stream, back);
        await fresh.Stream.WriteAsync(data);
        await r.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(data, back);
    }

    [Fact]
    public async Task A_relay_reconnect_re_attaches_the_route_and_ends_open_sessions()
    {
        await StartHeadquarters();
        await using var session = await Phone();
        Assert.Equal(1, _relay.Attachments);
        await ((IRelayHandler)_connector!).OnReconnectedAsync();
        Assert.Equal(2, _relay.Attachments);
        var buffer = new byte[16];
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var read = await session.Stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (read == 0) throw new EndOfStreamException();
        });
    }

    [Fact]
    public async Task Hello_and_welcome_cross_the_secure_channel()
    {
        var installation = Guid.NewGuid();
        _connector = new HeadquartersRelayConnector(_relay.Connect(), Route, _routeKey, _headquartersCertificate, pin => _enrolled(pin),
            async (tls, channel, ct) =>
            {
                var hello = await HeadquartersHello.ReadHelloAsync(tls, ct);
                Assert.Equal(HeadquartersHello.Protocol, hello.Protocol);
                await HeadquartersHello.SendWelcomeAsync(tls, new HeadquartersWelcome("", 0, installation, "Northstar", channel.PhonePin, HeadquartersHello.EnrollmentRequired), ct);
            });
        await _connector.StartAsync(CancellationToken.None);
        await using var session = await Phone();
        await HeadquartersHello.SendHelloAsync(session.Stream, CancellationToken.None);
        var welcome = await HeadquartersHello.ReadWelcomeAsync(session.Stream, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(("Northstar", installation, RelayProtocol.Thumbprint(_phoneKey), "required"), (welcome.Headquarters, welcome.InstallationId, welcome.PhonePin, welcome.Enrollment));
    }

    [Fact]
    public void Device_assertions_verify_only_with_the_directory_key_and_parse_exactly()
    {
        using var directory = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var installation = Guid.NewGuid();
        var body = Encoding.UTF8.GetBytes(RelayProtocol.DeviceAssertionMessage(installation, "market-user-1", "PIN", 1800000000));
        string Signed(ECDsa key) => B64(body) + "." + B64(key.SignData(body, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var parsed = RelayProtocol.ReadDeviceAssertion(Signed(directory), RelayProtocol.PublicKey(directory));
        Assert.Equal((installation, "market-user-1", "PIN", 1800000000L), (parsed!.InstallationId, parsed.Subject, parsed.PhoneKeyThumbprint, parsed.ExpiresAt.ToUnixTimeSeconds()));
        Assert.Null(RelayProtocol.ReadDeviceAssertion(Signed(other), RelayProtocol.PublicKey(directory)));
        Assert.Null(RelayProtocol.ReadDeviceAssertion("garbage", RelayProtocol.PublicKey(directory)));
        Assert.Equal("abcd efgh ijkl mnop", RelayProtocol.Fingerprint("abcdefghijklmnopqrstuvwxyz"));
    }

    [Fact]
    public async Task Concurrent_requests_and_multi_megabyte_files_are_matched_by_id_over_the_secure_channel()
    {
        var big = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17);
        _connector = new HeadquartersRelayConnector(_relay.Connect(), Route, _routeKey, _headquartersCertificate, pin => _enrolled(pin),
            (tls, channel, ct) => HeadquartersTunnelServer.ServeAsync(tls, async (request, token) =>
            {
                if (request.Path == "/big.wasm") return new TunnelReply(200, "application/wasm", null, big);
                if (request.Method == "POST")
                    return new TunnelReply(200, "text/plain", null, Encoding.UTF8.GetBytes(
                        $"{request.OperationKey}|{request.ContentType}|{Convert.FromBase64String(request.Body!).Length}"));
                await Task.Delay(request.Path == "/slow" ? 300 : 0, token);
                return new TunnelReply(200, "application/json", null, Encoding.UTF8.GetBytes($"{{\"path\":\"{request.Path}\"}}"));
            }, concurrency: 8, idle: TimeSpan.FromMinutes(1), ct));
        await _connector.StartAsync(CancellationToken.None);
        await using var session = await Phone();
        await using var client = new PhoneTunnelClient(session.Stream);
        client.Start();

        var slow = client.SendAsync("GET", "/slow", 1024, CancellationToken.None);
        var fast = client.SendAsync("GET", "/fast", 1024, CancellationToken.None);
        var file = client.SendAsync("GET", "/big.wasm", 8 * 1024 * 1024, CancellationToken.None);
        Assert.Equal("{\"path\":\"/fast\"}", Encoding.UTF8.GetString((await fast.WaitAsync(TimeSpan.FromSeconds(10))).Body));
        Assert.False(slow.IsCompleted); // answered out of order
        Assert.Equal("{\"path\":\"/slow\"}", Encoding.UTF8.GetString((await slow.WaitAsync(TimeSpan.FromSeconds(10))).Body));
        var reply = await file.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(("application/wasm", big.Length), (reply.ContentType, reply.Body.Length));
        Assert.Equal(big, reply.Body);

        // A write carries its body, content type and operation key; bodies over 256 KiB are refused before sending.
        var write = await client.SendAsync(new MobileRequest("", 0, "POST", "/api/x", "application/json",
            Convert.ToBase64String(new byte[200 * 1024]), "op-1"), 1024, CancellationToken.None);
        Assert.Equal($"op-1|application/json|{200 * 1024}", Encoding.UTF8.GetString(write.Body));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.SendAsync(new MobileRequest("", 0, "POST", "/api/x", "application/json",
            Convert.ToBase64String(new byte[300 * 1024]), "op-2"), 1024, CancellationToken.None));

        // A response larger than the caller allows is refused.
        await Assert.ThrowsAsync<InvalidDataException>(() => client.SendAsync("GET", "/big.wasm", 1024, CancellationToken.None));
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public async Task Incoming_frames_wait_when_the_buffer_is_full_until_the_reader_catches_up()
    {
        var stream = new RelayChannelStream(_relay.Connect(), "c1", bufferedFrames: 2);
        await stream.DeliverAsync([1]);
        await stream.DeliverAsync([2]);
        var third = stream.DeliverAsync([3]);
        await Task.Delay(50);
        Assert.False(third.IsCompleted);
        var buffer = new byte[1];
        Assert.Equal(1, await stream.ReadAsync(buffer));
        await third.WaitAsync(TimeSpan.FromSeconds(5));
        stream.Complete("closed");
        Assert.Equal(1, await stream.ReadAsync(buffer));
        Assert.Equal(1, await stream.ReadAsync(buffer));
        Assert.Equal(0, await stream.ReadAsync(buffer));
        await Assert.ThrowsAsync<IOException>(() => stream.WriteAsync(new byte[] { 1 }).AsTask());
    }

    private async Task StartHeadquarters()
    {
        _connector = new HeadquartersRelayConnector(_relay.Connect(), Route, _routeKey, _headquartersCertificate,
            pin => _enrolled(pin), async (tls, channel, ct) =>
            {
                lock (_served) _served.Add(channel);
                var buffer = new byte[16 * 1024];
                int read;
                while ((read = await tls.ReadAsync(buffer, ct)) > 0) await tls.WriteAsync(buffer.AsMemory(0, read), ct);
            });
        await _connector.StartAsync(CancellationToken.None);
    }

    private Task<PhoneRelaySession> Phone(string? headquartersPin = null, X509Certificate2? certificate = null) =>
        PhoneRelaySession.ConnectAsync(_relay.Connect(), Route,
            RelayProtocol.IssueTicket(_routeKey, Route, RelayProtocol.Thumbprint(_phoneKey), DateTimeOffset.UtcNow.AddMinutes(4)),
            _phoneKey, certificate ?? _phoneCertificate, headquartersPin ?? HeadquartersPin, CancellationToken.None);

    /// <summary>With TLS 1.3 a refused client certificate can surface on the first read rather than in the handshake.</summary>
    private static async Task AssertRefused(Func<Task<PhoneRelaySession>> connect)
    {
        try
        {
            await using var session = await connect();
            var buffer = new byte[1];
            await session.Stream.WriteAsync(new byte[] { 1 });
            var read = await session.Stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, read);
        }
        catch (Exception error) when (error is AuthenticationException or IOException) { }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition());
    }

    private static async Task ReadExactly(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total));
            if (read == 0) throw new EndOfStreamException();
            total += read;
        }
    }

    private static byte[] Decode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    public async ValueTask DisposeAsync()
    {
        if (_connector is not null) await _connector.DisposeAsync();
        _headquartersCertificate.Dispose();
        _phoneCertificate.Dispose();
    }
}
