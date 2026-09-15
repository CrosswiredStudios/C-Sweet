using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using CSweet.Api.Setup;
using CSweet.Domain.Setup;
using CSweet.ExecutionGateway;
using CSweet.Office.Contracts.ControlPlane;
using CSweet.UI.Services;

namespace CSweet.UnitTests;

public sealed class OfficeUpdateTests
{
    [Theory]
    [InlineData("0.5.1", "0.5.2", true)]
    [InlineData("0.5.2", "0.5.2", false)]
    [InlineData("0.5.2.0", "0.5.2", false)]
    [InlineData("0.5.2", "0.5.2.0", false)]
    [InlineData("0.6.0", "0.5.2", false)]
    [InlineData("unknown", "0.5.2", false)]
    [InlineData("0.9.0", "0.10.0", true)]
    public void UpdatesCompareNumericVersionsWithoutDowngrading(string installed, string latest, bool expected) =>
        Assert.Equal(expected, OfficeUpdatePresentation.NeedsUpdate(installed, latest));

    [Theory]
    [InlineData("""{"schemaVersion":1,"protocolVersion":"1.0","officeVersion":"0.5.2","assets":[]}""", "0.5.2")]
    [InlineData("""{"schemaVersion":2,"protocolVersion":"1.0","officeVersion":"0.5.2","assets":[]}""", null)]
    [InlineData("""{"schemaVersion":1,"protocolVersion":"2.0","officeVersion":"0.5.2","assets":[]}""", null)]
    [InlineData("""{"schemaVersion":1,"protocolVersion":"1.0","officeVersion":"latest","assets":[]}""", null)]
    [InlineData("[]", null)]
    public void ReleaseCheckRejectsInvalidAndIncompatibleManifests(string json, string? expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, OfficeUpdateEndpoints.ReadReleaseVersion(document.RootElement));
    }

    [Fact]
    public async Task CheckFetchesFreshManifestAndOffersOnlyMatchingPackages()
    {
        await using var db = new CSweet.Infrastructure.Persistence.CSweetDbContext(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<CSweet.Infrastructure.Persistence.CSweetDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var windows = new ExecutionNode { Id = Guid.NewGuid(), OperatingSystem = "windows", Architecture = "x64" };
        var linux = new ExecutionNode { Id = Guid.NewGuid(), OperatingSystem = "linux", Architecture = "arm64" };
        db.ExecutionNodes.AddRange(windows, linux);
        await db.SaveChangesAsync();
        var json = $$"""
            {"schemaVersion":1,"protocolVersion":"1.0","officeVersion":"0.5.2","assets":[
              {"operatingSystem":"windows","architecture":"x64","packageType":"msi","sha256":"{{new string('a', 64)}}","url":"https://example.test/office.msi"}]}
            """;
        var factory = new ManifestClientFactory(json);
        var result = await OfficeUpdateEndpoints.CheckAsync(db, factory,
            Microsoft.Extensions.Options.Options.Create(new CSweet.Infrastructure.Setup.ExecutionFleetOptions()),
            TimeProvider.System, default);
        var response = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<CSweet.Contracts.Setup.OfficeUpdateCheckResponse>>(result).Value!;
        Assert.Equal("0.5.2", response.LatestVersion);
        Assert.Equal(windows.Id, Assert.Single(response.Packages).OfficeId);
        Assert.True(factory.RequestedFresh);
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.ServiceUnavailable, "Could not check")]
    [InlineData(System.Net.HttpStatusCode.NotFound, "No published Office release manifest")]
    [InlineData(System.Net.HttpStatusCode.Forbidden, "Access to the Office release manifest was denied")]
    public async Task FailedReleaseLookupDoesNotReportOfficesAsUpToDate(System.Net.HttpStatusCode status, string message)
    {
        await using var db = new CSweet.Infrastructure.Persistence.CSweetDbContext(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<CSweet.Infrastructure.Persistence.CSweetDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var result = await OfficeUpdateEndpoints.CheckAsync(db, new ManifestClientFactory("unavailable", status),
            Microsoft.Extensions.Options.Options.Create(new CSweet.Infrastructure.Setup.ExecutionFleetOptions()),
            TimeProvider.System, default);
        var problem = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult>(result);
        Assert.Equal(503, problem.StatusCode);
        Assert.Contains(message, problem.ProblemDetails.Detail);
    }

    [Fact]
    public async Task MissingGitHubReleaseStillOffersInitialSetupSourceOnlyForThisHost()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "office-update-" + Guid.NewGuid().ToString("N"));
        var scripts = Path.Combine(root, "scripts", "windows");
        Directory.CreateDirectory(scripts);
        try
        {
            var bootstrap = Path.Combine(scripts, "bootstrap.ps1");
            var launcher = Path.Combine(scripts, "launcher.ps1");
            File.WriteAllText(bootstrap, "# test");
            File.WriteAllText(launcher, "# test");
            File.WriteAllText(Path.Combine(root, "Directory.Build.props"),
                "<Project><PropertyGroup><VersionPrefix>0.5.2</VersionPrefix></PropertyGroup></Project>");
            await using var db = new CSweet.Infrastructure.Persistence.CSweetDbContext(
                new DbContextOptionsBuilder<CSweet.Infrastructure.Persistence.CSweetDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            var local = new ExecutionNode { Id = Guid.NewGuid(), MachineName = Environment.MachineName,
                OperatingSystem = "windows", Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString() };
            var remote = new ExecutionNode { Id = Guid.NewGuid(), MachineName = "another-host",
                OperatingSystem = "windows", Architecture = local.Architecture };
            db.ExecutionNodes.AddRange(local, remote);
            await db.SaveChangesAsync();
            var result = await OfficeUpdateEndpoints.CheckAsync(db,
                new ManifestClientFactory("missing", System.Net.HttpStatusCode.NotFound),
                Microsoft.Extensions.Options.Options.Create(new CSweet.Infrastructure.Setup.ExecutionFleetOptions {
                    WindowsDevelopmentLauncherScript = launcher, WindowsDevelopmentOfficeBootstrapScript = bootstrap }),
                TimeProvider.System, default);
            var response = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<CSweet.Contracts.Setup.OfficeUpdateCheckResponse>>(result).Value!;
            var candidate = Assert.Single(response.Packages);
            Assert.Equal(local.Id, candidate.OfficeId);
            Assert.Equal("local-setup", candidate.Source);
            Assert.Equal("0.5.2", candidate.Version);
            Assert.Null(candidate.Url);
            Assert.Equal("local-setup", response.Source);
            Assert.NotNull(response.Warning);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    private sealed class ManifestClientFactory(string json, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK)
        : HttpMessageHandler, IHttpClientFactory
    {
        public bool RequestedFresh { get; private set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedFresh = request.Headers.CacheControl?.NoCache == true;
            return Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request, Content = new StringContent(json) });
        }
    }

    [Fact]
    public void HeartbeatRefreshesVersionAndLegacyHeartbeatPreservesLastReport()
    {
        var node = new ExecutionNode { NodeVersion = "0.5.1" };
        OfficeGatewayService.ApplyHeartbeatCapacity(node, new OfficeHeartbeat { OfficeVersion = "0.5.2" }, null);
        Assert.Equal("0.5.2", node.NodeVersion);
        OfficeGatewayService.ApplyHeartbeatCapacity(node, new OfficeHeartbeat(), null);
        Assert.Equal("0.5.2", node.NodeVersion);
    }
}
