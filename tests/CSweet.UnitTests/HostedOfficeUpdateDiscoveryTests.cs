using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Api.Setup;
using CSweet.Contracts.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class HostedOfficeUpdateDiscoveryTests
{
    private static JsonObject Manifest(bool multipart = true)
    {
        const string root = "https://github.com/CrosswiredStudios/CSweet.Office/releases/download/v0.14.0/";
        var support = "csweet-office-0.14.0-windows-support.zip";
        var bundle = "csweet-office-0.14.0-windows-x64.zip";
        JsonObject Asset(string name, int size) => new() { ["name"] = name, ["size"] = size, ["sha256"] = new string('a', 64), ["url"] = root + name };
        var full = Asset(bundle, 100);
        if (multipart) { full.Remove("url"); full["parts"] = new JsonArray(Asset(bundle + ".part001", 50), Asset(bundle + ".part002", 50)); }
        return new() { ["schemaVersion"] = multipart ? 2 : 1, ["officeVersion"] = "0.14.0", ["protocolVersion"] = "1.0",
            ["signing"] = "development", ["requiresHostCertification"] = true, ["guestBrokerProtocolVersion"] = "1.1",
            ["environmentProfiles"] = new JsonArray("software-development-polyglot-v1"),
            ["assets"] = new JsonArray(Asset(support, 50), full) };
    }

    [Theory]
    [InlineData("valid", "0.14.0")] [InlineData("single", "0.14.0")]
    [InlineData("no-certification", null)] [InlineData("old-broker", null)] [InlineData("old-image", null)]
    [InlineData("wrong-origin", null)] [InlineData("wrong-version", null)] [InlineData("wrong-digest", null)]
    [InlineData("missing-part", null)] [InlineData("wrong-order", null)] [InlineData("wrong-total", null)]
    [InlineData("duplicate", null)] [InlineData("string-schema", null)] [InlineData("string-size", null)]
    public void CatalogAcceptsOnlyCompatibleTaggedHostedBundles(string scenario, string? expected)
    {
        var manifest = Manifest(scenario != "single");
        var assets = manifest["assets"]!.AsArray();
        switch (scenario)
        {
            case "no-certification": manifest["requiresHostCertification"] = false; break;
            case "old-broker": manifest["guestBrokerProtocolVersion"] = "1.0"; break;
            case "old-image": manifest["environmentProfiles"] = new JsonArray("ordinary-v1"); break;
            case "wrong-origin": assets[0]!["url"] = "https://untrusted.test/package.zip"; break;
            case "wrong-version": manifest["officeVersion"] = "0.15.0"; break;
            case "wrong-digest": assets[0]!["sha256"] = "bad"; break;
            case "missing-part": assets[1]!["parts"]!.AsArray().RemoveAt(1); break;
            case "wrong-order": assets[1]!["parts"]![0]!["name"] = "part002"; break;
            case "wrong-total": assets[1]!["size"] = 99; break;
            case "duplicate": assets.Add(assets[0]!.DeepClone()); break;
            case "string-schema": manifest["schemaVersion"] = "2"; break;
            case "string-size": assets[0]!["size"] = "50"; break;
        }
        using var json = JsonDocument.Parse(manifest.ToJsonString());
        Assert.Equal(expected, OfficeUpdateEndpoints.ReadHostedReleaseVersion(json.RootElement));
    }

    [Theory]
    [InlineData(true, false, 1)] [InlineData(false, false, 0)] [InlineData(true, true, 0)]
    public async Task DiscoveryRequiresExplicitDevelopmentPolicyAndOfficialSource(bool development, bool customSource, int count)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var windows = new ExecutionNode { Id = Guid.NewGuid(), OperatingSystem = "windows", Architecture = "x64" };
        db.ExecutionNodes.AddRange(windows, new ExecutionNode { Id = Guid.NewGuid(), OperatingSystem = "linux", Architecture = "x64" });
        await db.SaveChangesAsync();
        var factory = new Factory(Manifest().ToJsonString());
        var options = new ExecutionFleetOptions { AllowUnpinnedDevelopmentImages = development };
        if (customSource) options.ReleaseManifestUrl = "https://example.test/office-release.json";
        var result = await OfficeUpdateEndpoints.CheckAsync(db, factory, Options.Create(options), TimeProvider.System, default);
        if (count == 0) Assert.IsType<ProblemHttpResult>(result);
        else
        {
            var response = Assert.IsType<Ok<OfficeUpdateCheckResponse>>(result).Value!;
            var package = Assert.Single(response.Packages);
            Assert.Equal(windows.Id, package.OfficeId); Assert.Equal("0.14.0", package.Version);
            Assert.Equal("github", package.Source);
            Assert.EndsWith("/v0.14.0/office-bootstrap.json", package.Url);
            Assert.Contains("certify", response.Warning);
        }
        Assert.Equal(count, factory.HostedReads);
        Assert.Empty(db.LocalOfficeSetupSessions);
    }

    private sealed class Factory(string json) : HttpMessageHandler, IHttpClientFactory
    {
        public int HostedReads { get; private set; }
        public HttpClient CreateClient(string name) => new(this, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var hosted = request.RequestUri!.AbsolutePath.EndsWith("/office-bootstrap.json", StringComparison.Ordinal);
            if (hosted) { HostedReads++; Assert.True(request.Headers.CacheControl!.NoCache); }
            return Task.FromResult(new HttpResponseMessage(hosted ? HttpStatusCode.OK : HttpStatusCode.NotFound)
                { RequestMessage = request, Content = new StringContent(hosted ? json : "missing") });
        }
    }
}
