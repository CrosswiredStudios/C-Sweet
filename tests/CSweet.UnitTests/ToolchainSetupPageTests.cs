using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using CSweet.Contracts.Core;
using CSweet.UI.Localization;
using CSweet.UI.Pages;
using CSweet.UI.Services;

namespace CSweet.UnitTests;

public sealed class ToolchainSetupPageTests
{
    [Theory]
    [InlineData("reviewed", 1)] [InlineData("not-reviewed", 0)] [InlineData("other-provider", 0)]
    [InlineData("other-image", 0)] [InlineData("other-profile", 0)] [InlineData("already-running", 0)]
    public async Task StartingCertificationRequiresReviewOfTheExactCurrentProviderImageAndProfile(string scenario, int expected)
    {
        var organization = Guid.NewGuid();
        var provider = new ToolchainCertificationProvider(Guid.NewGuid(), Guid.NewGuid(), "Node", "1.0.1", ["office.node.v1"]);
        var handler = new Handler(); var page = new ToolchainSetup();
        typeof(ToolchainSetup).GetProperty("PluginApi", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page,
            new PluginApiClient(new HttpClient(handler) { BaseAddress = new("http://localhost") }));
        typeof(ToolchainSetup).GetProperty("L", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, new Localizer());
        Set(page, "_business", (Guid?)organization);
        Set(page, "_setup", new ToolchainCertificationSetup([provider], [new("Office", "sha256:image")]));
        Set(page, "_provider", scenario == "other-provider" ? provider with { InstallationId = Guid.NewGuid() } : provider);
        Set(page, "_profile", scenario == "other-profile" ? "other" : "office.node.v1");
        Set(page, "_image", scenario == "other-image" ? "sha256:other" : "sha256:image");
        Set(page, "_reviewed", scenario != "not-reviewed");
        if (scenario == "already-running") Set(page, "_runs", new[] { JsonSerializer.Deserialize<ToolchainCertificationSummary>("{}")! with {
            ProviderInstallationId = provider.InstallationId, ToolchainDefinitionId = provider.DefinitionId,
            EnvironmentProfileKey = "office.node.v1", EnvironmentImageDigest = "sha256:image", Status = "Running" } });
        await (Task)typeof(ToolchainSetup).GetMethod("StartAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null)!;
        Assert.Equal(expected, handler.Requests);
        if (expected == 1)
        {
            Assert.Equal(new(organization, provider.DefinitionId, provider.InstallationId, "office.node.v1", "sha256:image"), handler.Request);
            // A replay of the click cannot certify or schedule another run without a new review.
            await (Task)typeof(ToolchainSetup).GetMethod("StartAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null)!;
            Assert.Equal(1, handler.Requests);
        }
    }
    private static void Set(ToolchainSetup page, string field, object value) =>
        typeof(ToolchainSetup).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);
    private sealed class Handler : HttpMessageHandler
    {
        public int Requests; public StartToolchainCertificationRequest? Request;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("/api/admin/toolchain-certifications", request.RequestUri!.AbsolutePath);
            Requests++; Request = await request.Content!.ReadFromJsonAsync<StartToolchainCertificationRequest>(token);
            return new(HttpStatusCode.Accepted) { Content = JsonContent.Create(JsonSerializer.Deserialize<ToolchainCertificationSummary>("{}")! with { Status = "Running" }) };
        }
    }
    private sealed class Localizer : IAppLocalizer
    {
        public string this[string key] => key; public string Format(string key, object? args) => key; public bool Has(string key) => true;
    }
}
