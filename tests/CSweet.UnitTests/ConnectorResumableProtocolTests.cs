using CSweet.AgentHost.Broker;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Infrastructure.Setup;
using System.Text.Json;

namespace CSweet.UnitTests;

public sealed class ConnectorResumableProtocolTests
{
    [Fact]
    public void MediaMaterializationRequiresAndFreezesTheExplicitProtocol()
    {
        var input = JsonSerializer.SerializeToElement(new { mediaAssetId = Guid.NewGuid() });
        var operation = new PluginProviderOperationDeclaration { Effect = "write", Http = new()
            { Connection = "account", Method = "POST", Endpoint = "https://api.example.com/upload", MediaInput = "/mediaAssetId" } };
        Assert.Throws<InvalidOperationException>(() => ConnectorRequestMaterializer.Prepare(operation, input, "account"));
        operation = operation with { Http = operation.Http with { MediaProtocol = ConnectorResumableProtocol.Name } };
        var request = ConnectorRequestMaterializer.Prepare(operation, input, "account");
        Assert.Equal(ConnectorResumableProtocol.Name, request.MediaProtocol);
        var hash = ConnectorRequestMaterializer.Hash(JsonSerializer.SerializeToElement(request));
        Assert.NotEqual(hash, ConnectorRequestMaterializer.Hash(JsonSerializer.SerializeToElement(request with { MediaProtocol = null })));
    }

    [Fact]
    public async Task InitiationProbeAndChunksUseOnlyFrozenMetadataAndExactByteRanges()
    {
        var plan = Plan(ConnectorResumableProtocol.Alignment + 7);
        using var begin = ConnectorResumableProtocol.CreateRequest(plan, ConnectorMediaStep.Begin);
        Assert.Equal(HttpMethod.Post, begin.Method); Assert.Equal(plan.Request.Url, begin.RequestUri!.AbsoluteUri);
        Assert.Equal("262151", Assert.Single(begin.Headers.GetValues("X-Upload-Content-Length")));
        Assert.Equal("video/mp4", Assert.Single(begin.Headers.GetValues("X-Upload-Content-Type")));
        Assert.Equal(plan.Request.Body, await begin.Content!.ReadAsStringAsync());
        using var probe = ConnectorResumableProtocol.CreateRequest(plan, ConnectorMediaStep.Probe, Session);
        Assert.Equal(HttpMethod.Put, probe.Method); Assert.Equal("bytes */262151", probe.Content!.Headers.ContentRange!.ToString());
        Assert.Empty(await probe.Content.ReadAsByteArrayAsync());
        using var chunk = ConnectorResumableProtocol.CreateRequest(plan, ConnectorMediaStep.Chunk, Session, 262144, new byte[7]);
        Assert.Equal("bytes 262144-262150/262151", chunk.Content!.Headers.ContentRange!.ToString());
        Assert.Equal(7, (await chunk.Content.ReadAsByteArrayAsync()).Length);
    }

    [Theory]
    [InlineData("http://api.example.com/upload?session=private")]
    [InlineData("https://evil.example.com/upload?session=private")]
    [InlineData("https://api.example.com/other?session=private")]
    [InlineData("https://user:secret@api.example.com/upload")]
    [InlineData("https://api.example.com:8443/upload")]
    [InlineData("https://api.example.com/upload#fragment")]
    [InlineData("/upload?session=private")]
    public void SessionCannotChangeReviewedOriginOrPath(string url) =>
        Assert.Throws<UnauthorizedAccessException>(() => ConnectorResumableProtocol.ValidateSession(Plan(), url));

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(1, 0)]
    [InlineData(99, 2)]
    [InlineData(100, 1)]
    [InlineData(0, 10)] // A non-final chunk must be a multiple of 256 KiB.
    public void InvalidByteRangesNeverProduceRequests(long offset, int length) =>
        Assert.Throws<InvalidOperationException>(() => ConnectorResumableProtocol.CreateRequest(
            Plan(), ConnectorMediaStep.Chunk, Session, offset, new byte[length]));

    [Fact]
    public void MissingRangeMeansNoAcknowledgedBytesRatherThanAssumedChunkSuccess()
    {
        Assert.Equal(0, ConnectorResumableProtocol.ParseCommittedBytes([], 100, 50));
        Assert.Equal(25, ConnectorResumableProtocol.ParseCommittedBytes(["bytes=0-24"], 100, 50));
        Assert.Equal(50, ConnectorResumableProtocol.ParseCommittedBytes(["bytes=0-49"], 100, 50));
    }

    [Theory]
    [InlineData("bytes=0-50")]
    [InlineData("bytes=1-25")]
    [InlineData("bytes=0--1")]
    [InlineData("bytes=0-24,50-75")]
    [InlineData("bytes=0-9223372036854775807")]
    [InlineData("bytes=0- 24")]
    public void ImpossibleOrMalformedAcknowledgementsFailClosed(string range) =>
        Assert.Throws<InvalidOperationException>(() => ConnectorResumableProtocol.ParseCommittedBytes([range], 100, 50));

    [Fact]
    public void DuplicateRangesAndUnsupportedProtocolsFailClosed()
    {
        Assert.Throws<InvalidOperationException>(() => ConnectorResumableProtocol.ParseCommittedBytes(["bytes=0-1", "bytes=0-2"], 100, 50));
        var plan = Plan();
        Assert.Throws<InvalidOperationException>(() => ConnectorResumableProtocol.CreateRequest(
            plan with { Request = plan.Request with { MediaProtocol = null } }, ConnectorMediaStep.Begin));
        Assert.Throws<InvalidOperationException>(() => ConnectorResumableProtocol.CreateRequest(
            plan with { Media = plan.Media! with { AssetId = Guid.NewGuid() } }, ConnectorMediaStep.Begin));
    }

    [Fact]
    public void RetryDelayIsBoundedWithoutIgnoringProviderInstructions()
    {
        var now = DateTimeOffset.UtcNow;
        using var response = new HttpResponseMessage();
        response.Headers.Add("Retry-After", "120");
        Assert.Equal(now.AddSeconds(120), ConnectorResumableProtocol.ParseRetryAfter(response.Headers, now));
        response.Headers.Remove("Retry-After"); response.Headers.TryAddWithoutValidation("Retry-After", "999999999");
        Assert.Throws<InvalidOperationException>(() => ConnectorResumableProtocol.ParseRetryAfter(response.Headers, now));
    }

    [Fact]
    public async Task LegacyUploadRouteCannotUseCallerSelectedUrlsOrCredentialsEvenWhenDirectlyInvoked()
    {
        var handler = new PlatformMediaTransferCapabilityHandler();
        Assert.True(handler.CanHandle(PluginPlatformCapabilities.MediaTransfer));
        var request = new RequestCapability { RequestId = "blocked", Capability = PluginPlatformCapabilities.MediaTransfer,
            Payload = JsonPayload.FromUtf8("{\"initiationUrl\":\"https://api.example.com/upload\",\"metadata\":{\"private\":\"do not expose\"}}") };
        var results = new List<CapabilityResult>();
        await foreach (var result in handler.HandleAsync(null!, request, default)) results.Add(result);
        var failure = Assert.Single(results); Assert.False(failure.Succeeded);
        Assert.DoesNotContain("do not expose", failure.Error); Assert.Contains("exact approved action", failure.Error);
    }

    private const string Session = "https://api.example.com/upload?session=private";
    private static FrozenConnectorPlan Plan(long size = 100)
    {
        var media = new ConnectorMediaBinding(Guid.NewGuid(), new string('a', 64), size, "video/mp4");
        return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 1, new string('b', 64),
            "example.upload.v1", "account", "stable", new string('c', 64),
            new("POST", "https://api.example.com/upload?mode=resumable", "{\"title\":\"Approved title\"}",
                "account", "account", "write", [], media.AssetId.ToString("D"), [], ConnectorResumableProtocol.Name), media);
    }
}
