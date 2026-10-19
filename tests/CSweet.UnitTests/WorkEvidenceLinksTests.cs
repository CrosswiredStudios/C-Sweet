using System.Text.Encodings.Web;
using System.Text.Json;
using CSweet.UI.Components.WorkBoards;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using Wire = CSweet.WorkManagement.Contracts;
using CSweet.UI.Localization;

namespace CSweet.UnitTests;

public sealed class WorkEvidenceLinksTests
{
    [Fact]
    public async Task DocumentEvidenceRendersExactRevisionLinksAndDeduplicatesStageReferences()
    {
        var organization = Guid.NewGuid(); var artifact = Guid.NewGuid();
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var evidence = new Wire.WorkExecutionEvidence("artifact-revision", "video-game.technical-design.v1",
            JsonSerializer.Serialize(new { artifactId = artifact, revisionId = first, sha256 = "private-content-hash" }));
        var next = evidence with { Value = JsonSerializer.Serialize(new { artifactId = artifact, revisionId = second }) };
        var html = await Render(organization, [evidence, evidence, next]);

        Assert.Contains("2 document revisions", html);
        Assert.Contains("Technical Design", html);
        foreach (var revision in new[] { first, second })
            Assert.Contains(HtmlEncoder.Default.Encode($"/organizations/{organization:D}/documents?artifact={artifact:D}&revision={revision:D}"), html);
        Assert.DoesNotContain("sha256", html);
        Assert.DoesNotContain("private-content-hash", html);
        Assert.DoesNotContain("artifactId", html);
        Assert.Equal(2, html.Split("<a href=", StringSplitOptions.None).Length - 1);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"artifactId\":123,\"revisionId\":false}")]
    [InlineData("{\"artifactId\":\"javascript:alert(1)\",\"revisionId\":\"missing\"}")]
    [InlineData("{\"artifactId\":\"00000000-0000-0000-0000-000000000000\"}")]
    public async Task InvalidEvidenceShowsAnUnavailableLabelWithoutRawPayloadOrLink(string payload)
    {
        var html = await Render(Guid.NewGuid(), [new("artifact-revision", "Technical design", payload)]);
        Assert.Contains("Revision link unavailable", html);
        Assert.DoesNotContain("<a ", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.DoesNotContain("artifactId", html);
    }

    [Fact]
    public void PascalCaseRevisionReferencesRemainLinkable()
    {
        var artifact = Guid.NewGuid(); var revision = Guid.NewGuid();
        var entry = Assert.Single(WorkEvidencePresentation.Entries([new("artifact-revision", "Scope",
            JsonSerializer.Serialize(new { ArtifactId = artifact, RevisionId = revision }))]));
        Assert.Equal(artifact, entry.ArtifactId);
        Assert.Equal(revision, entry.RevisionId);
    }

    private static async Task<string> Render(Guid organization, Wire.WorkExecutionEvidence[] evidence)
    {
        var services = new ServiceCollection().AddLogging(); services.AddMudServices(); services.AddCSweetLocalization();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<WorkEvidenceLinks>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(WorkEvidenceLinks.OrganizationId)] = organization, [nameof(WorkEvidenceLinks.Evidence)] = evidence }))).ToHtmlString());
    }

    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
