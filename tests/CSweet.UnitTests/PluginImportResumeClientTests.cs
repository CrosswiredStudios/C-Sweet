using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.UI.Services;

namespace CSweet.UnitTests;

public sealed class PluginImportResumeClientTests
{
    [Fact]
    public async Task OneSubmissionReturnsBoundedReceiptAndSendsExpectedRevision()
    {
        var setup = Guid.NewGuid(); var receipt = new PluginImportResumeReceipt(setup, 8, Guid.NewGuid());
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = JsonContent.Create(receipt) });
        var client = Client(handler);
        Assert.Equal(receipt, await client.ResumeImportAsync(Guid.NewGuid(), setup, 7));
        Assert.Equal(1, handler.Requests); Assert.Contains("\"expectedRevision\":7", handler.LastBody);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(503)]
    public async Task FailedAcknowledgmentNeverRendersRawResponseOrAutomaticallyRetries(int status)
    {
        using var handler = new Handler(_ => new((HttpStatusCode)status) { Content = new StringContent("CANARY access_token=private proof=signature") });
        var error = await Assert.ThrowsAsync<ApiClientException>(() => Client(handler).ResumeImportAsync(Guid.NewGuid(), Guid.NewGuid(), 7));
        Assert.DoesNotContain("CANARY", error.Message); Assert.DoesNotContain("private", error.Message);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task LegacyNoContentIsUnconfirmedRatherThanInventedSuccess()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.NoContent));
        Assert.Null(await Client(handler).ResumeImportAsync(Guid.NewGuid(), Guid.NewGuid(), 7)); Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData("wrong-setup")]
    [InlineData("stale-revision")]
    [InlineData("empty-correlation")]
    [InlineData("duplicate")]
    [InlineData("malformed")]
    [InlineData("oversized")]
    public async Task InvalidReceiptIsUnconfirmedAndNeverResubmitted(string variant)
    {
        var setup = Guid.NewGuid(); var receipt = new PluginImportResumeReceipt(setup, 8, Guid.NewGuid());
        var json = variant switch
        {
            "wrong-setup" => JsonSerializer.Serialize(receipt with { SetupId = Guid.NewGuid() }),
            "stale-revision" => JsonSerializer.Serialize(receipt with { Revision = 7 }),
            "empty-correlation" => JsonSerializer.Serialize(receipt with { CorrelationId = Guid.Empty }),
            "duplicate" => $"{{\"setupId\":\"{setup}\",\"SetupId\":\"{setup}\",\"revision\":8,\"correlationId\":\"{receipt.CorrelationId}\"}}",
            "oversized" => new string('x', 2049) + "CANARY",
            _ => "CANARY malformed response"
        };
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Client(handler).ResumeImportAsync(Guid.NewGuid(), setup, 7));
        Assert.DoesNotContain("CANARY", error.Message); Assert.Equal(1, handler.Requests);
    }

    private static PluginAccountApiClient Client(Handler handler) => new(new HttpClient(handler, false) { BaseAddress = new("https://synthetic.invalid/") });
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Requests; public string? LastBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Requests++; LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct); return response(request); }
    }
}
