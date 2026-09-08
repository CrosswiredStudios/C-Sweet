using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using CSweet.Contracts.GenAi;
using CSweet.UI.Services;

namespace CSweet.UnitTests;

public sealed class ResumableAttachmentUploadTests
{
    [Fact]
    public async Task Upload_HashesAndTransfersBoundedChunks_AndReturnsVerifiedAsset()
    {
        using var fixture = new Fixture(ResumableAttachmentUpload.MaximumBufferBytes + 17);
        var asset = await fixture.RunAsync();
        Assert.Equal(fixture.Hash, asset.Sha256);
        Assert.Equal(new long[] { 0, ResumableAttachmentUpload.MaximumBufferBytes }, fixture.PutOffsets);
        Assert.All(fixture.Reads, x => Assert.InRange(x.Count, 1, ResumableAttachmentUpload.MaximumBufferBytes));
        Assert.Equal(fixture.Organization, fixture.Ticket!.OrganizationId);
        Assert.Equal(fixture.Conversation, fixture.Ticket.ConversationId);
        Assert.Equal(1, fixture.Creates);
        Assert.Equal(1, fixture.Completes);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("chunk")]
    [InlineData("complete")]
    public async Task LostResponse_ResumeReusesIdentityAndAuthoritativePosition(string failure)
    {
        using var fixture = new Fixture(90_000) { Failure = failure, ChunkSize = 65_536 };
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.RunAsync());
        var key = fixture.Ticket!.IdempotencyKey;
        var asset = await fixture.RunAsync();
        Assert.Equal(fixture.Hash, asset.Sha256);
        Assert.Equal(key, fixture.Ticket.IdempotencyKey);
        Assert.Equal(1, fixture.Completes);
        Assert.Equal(new long[] { 0, 65_536 }, fixture.PutOffsets);
        Assert.Equal(failure == "create" ? 2 : 1, fixture.Creates);
        Assert.All(fixture.CreateKeys, x => Assert.Equal(key, x));
    }

    [Fact]
    public async Task Resume_RejectsDifferentBytesEvenWithSameNameSizeAndType()
    {
        using var fixture = new Fixture(90_000) { Failure = "chunk", ChunkSize = 65_536 };
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.RunAsync());
        fixture.Bytes[0] ^= 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunAsync());
        Assert.Single(fixture.PutOffsets);
        Assert.Equal(0, fixture.Gets);
    }

    [Fact]
    public async Task Resume_RejectsForeignConversationBeforeReadingFile()
    {
        using var fixture = new Fixture(128);
        fixture.Ticket = new(fixture.Organization, Guid.NewGuid(), "video.mp4", "video/mp4", 128, Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunAsync());
        Assert.Empty(fixture.Reads);
        Assert.Equal(0, fixture.Creates);
    }

    [Theory]
    [InlineData("organization")]
    [InlineData("size")]
    [InlineData("chunk")]
    public async Task InvalidSessionResponse_NeverSendsFileBytes(string tamper)
    {
        using var fixture = new Fixture(128) { Tamper = tamper };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunAsync());
        Assert.Empty(fixture.PutOffsets);
    }

    [Fact]
    public async Task InvalidCompletedChecksum_IsNotAttached()
    {
        using var fixture = new Fixture(128) { Tamper = "checksum" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunAsync());
        Assert.Equal(1, fixture.Completes);
    }

    [Fact]
    public async Task CancelAfterLostCreateResponse_RecoversSameSessionBeforeDelete()
    {
        using var fixture = new Fixture(128) { Failure = "create" };
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.RunAsync());
        Assert.Null(fixture.Ticket!.SessionId);
        await fixture.Client.CancelAsync(fixture.Ticket, CancellationToken.None);
        Assert.Equal(2, fixture.Creates);
        Assert.Single(fixture.Deletes);
        Assert.Equal(fixture.Session!.Id, fixture.Deletes[0]);
        Assert.Empty(fixture.PutOffsets);
    }

    [Fact]
    public async Task CancellationDuringHash_DoesNotCreateSession()
    {
        using var fixture = new Fixture(128);
        using var cts = new CancellationTokenSource();
        fixture.AfterRead = cts.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RunAsync(cts.Token));
        Assert.Equal(0, fixture.Creates);
    }

    [Fact]
    public async Task DeploymentLimit_IsCheckedBeforeFileReads()
    {
        using var fixture = new Fixture(128) { Limit = 127 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunAsync());
        Assert.Empty(fixture.Reads);
        Assert.Equal(0, fixture.Creates);
    }

    private sealed class Fixture : HttpMessageHandler
    {
        public Guid Organization { get; } = Guid.NewGuid();
        public Guid Conversation { get; } = Guid.NewGuid();
        public byte[] Bytes { get; }
        public string Hash { get; }
        public MediaUploadTicket? Ticket { get; set; }
        public MediaUploadSessionResponse? Session { get; private set; }
        public ResumableAttachmentUpload Client { get; }
        public long Limit { get; set; } = MediaAttachmentPolicy.AbsoluteMaximumFileBytes;
        public int ChunkSize { get; set; } = ResumableAttachmentUpload.MaximumBufferBytes;
        public string? Failure { get; set; }
        public string? Tamper { get; set; }
        public Action? AfterRead { get; set; }
        public int Creates, Completes, Gets;
        public List<long> PutOffsets { get; } = [];
        public List<Guid> Deletes { get; } = [];
        public List<string?> CreateKeys { get; } = [];
        public List<(long Offset, int Count)> Reads { get; } = [];
        private readonly HttpClient _http;
        public Fixture(int size)
        {
            Bytes = new byte[size]; Random.Shared.NextBytes(Bytes);
            Hash = Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant();
            _http = new(this, false) { BaseAddress = new("https://host.invalid/") };
            Client = new(_http);
        }
        public Task<MediaAssetResponse> RunAsync(CancellationToken ct = default) => Client.UploadAsync(
            Organization, Conversation, new("video.mp4", "video/mp4", Bytes.Length, (offset, count, token) =>
            {
                token.ThrowIfCancellationRequested(); Reads.Add((offset, count)); AfterRead?.Invoke();
                return Task.FromResult(Bytes.AsSpan((int)offset, count).ToArray());
            }), Ticket, ticket => { Ticket = ticket; return Task.CompletedTask; }, _ => { }, ct);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/policy/")) return Json(new MediaUploadPolicyResponse(Limit));
            if (request.Method == HttpMethod.Post && path.Contains("/organization/"))
            {
                var input = (await request.Content!.ReadFromJsonAsync<CreateMediaUploadSessionRequest>(ct))!;
                Assert.Equal(Hash, input.Sha256); Assert.NotNull(Ticket); Creates++; CreateKeys.Add(input.IdempotencyKey);
                Session ??= new(Guid.NewGuid(), Organization, input.FileName, input.ContentType, input.TotalBytes,
                    0, ChunkSize, "Active", DateTimeOffset.UtcNow.AddHours(1));
                Fail("create");
                return Json(Tamper switch
                {
                    "organization" => Session with { OrganizationId = Guid.NewGuid() },
                    "size" => Session with { TotalBytes = 1 },
                    "chunk" => Session with { ChunkSizeBytes = int.MaxValue },
                    _ => Session
                });
            }
            Assert.NotNull(Session);
            if (request.Method == HttpMethod.Get) { Gets++; return Json(Session); }
            if (request.Method == HttpMethod.Delete)
            {
                Deletes.Add(Guid.Parse(path.Split('/')[^1]));
                Session = Session with { Status = "Cancelled" };
                return new(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Put)
            {
                var offset = long.Parse(path.Split('/')[^1]);
                Assert.Equal(Session.ReceivedBytes, offset); PutOffsets.Add(offset);
                var sent = await request.Content!.ReadAsByteArrayAsync(ct);
                Assert.Equal(Bytes.AsSpan((int)offset, sent.Length).ToArray(), sent);
                Session = Session with { ReceivedBytes = offset + sent.Length };
                Fail("chunk"); return Json(Session);
            }
            Assert.EndsWith("/complete", path);
            Completes++;
            Session = Session with { Status = "Completed", Asset = new(Guid.NewGuid(), "video.mp4", "video/mp4",
                Bytes.Length, Tamper == "checksum" ? new string('0', 64) : Hash, null, null, null, DateTimeOffset.UtcNow) };
            Fail("complete"); return Json(Session);
        }
        private void Fail(string stage)
        {
            if (Failure != stage) return;
            Failure = null; throw new HttpRequestException("Lost response after persisted state.");
        }
        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
        protected override void Dispose(bool disposing) { if (disposing) _http.Dispose(); base.Dispose(disposing); }
    }
}

public sealed class MediaAttachmentPolicyTests
{
    [Theory]
    [InlineData("video/mp4")]
    [InlineData("video/webm")]
    public void VideosUseDeploymentLimitWithoutExpandingDocumentLimits(string type)
    {
        const long large = 4L * 1024 * 1024 * 1024;
        MediaAttachmentPolicy.Validate([(type, large), ("text/vtt", 1024)], large);
        Assert.Throws<InvalidOperationException>(() => MediaAttachmentPolicy.Validate([(type, large + 1)], large));
        Assert.Throws<InvalidOperationException>(() => MediaAttachmentPolicy.Validate([("text/plain", large)], large));
    }

    [Fact]
    public void AbsoluteCeilingCountAndAggregateRemainEnforced()
    {
        Assert.Throws<InvalidOperationException>(() => MediaAttachmentPolicy.Validate(
            [("video/mp4", MediaAttachmentPolicy.AbsoluteMaximumFileBytes + 1)], long.MaxValue));
        Assert.Throws<InvalidOperationException>(() => MediaAttachmentPolicy.Validate(
            Enumerable.Repeat(("video/mp4", 1L), 9), long.MaxValue));
        Assert.Throws<InvalidOperationException>(() => MediaAttachmentPolicy.Validate(
            Enumerable.Repeat(("application/pdf", MediaAttachmentPolicy.SmallFileBytes), 3), long.MaxValue));
    }
}
