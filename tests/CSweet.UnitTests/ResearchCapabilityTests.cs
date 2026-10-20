using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Contracts.Plugins;

namespace CSweet.UnitTests;

public sealed class ResearchCapabilityTests
{
    [Fact]
    public void SearchReturnsBoundedPublicHitsAndPartialEngineFailures()
    {
        var input=JsonSerializer.SerializeToElement(new { results=new[]{
            new {title="Private",url="http://127.0.0.1/",content="deny"},
            new {title="File",url="file:///etc/passwd",content="deny"},
            new {title="Docs",url="https://example.com/docs",content="API"},
            new {title="Other",url="https://example.org",content="More"}},
            unresponsive_engines=new[]{new[]{"upstream","timeout"}} });
        var result=ResearchCapabilityHandler.ParseSearch(input,1);
        Assert.Equal("Docs",Assert.Single(result.Results).Title);
        Assert.Equal("upstream: timeout",Assert.Single(result.EngineFailures));
    }

    [Fact]
    public void PageExtractionIsBoundedAndDoesNotExecuteScriptsOrFollowEmbeddedUrls()
    {
        var html="<html><head><title>Reference</title></head><body><article><h1>API reference</h1>"+
            string.Concat(Enumerable.Repeat("<p>Documented public method with useful explanatory text and examples.</p>",60))+
            "<script>fetch('http://127.0.0.1/secrets')</script><img src='http://127.0.0.1/secrets'></article></body></html>";
        var page=ResearchCapabilityHandler.Extract(new(200,"https://example.com/docs","text/html; charset=utf-8",Encoding.UTF8.GetBytes(html),false),1000);
        Assert.Equal(1000,page.Text.Length); Assert.True(page.Truncated);
        Assert.DoesNotContain("fetch(",page.Text);
        Assert.Equal("https://example.com/docs",page.Url);
    }

    [Fact]
    public void UnsupportedFormatsAndEmptyStaticPagesHaveExplicitLimitations()
    {
        Assert.NotNull(ResearchCapabilityHandler.Extract(new(200,"https://example.com/a.pdf","application/pdf",[1],false),1000).Limitation);
        var text=ResearchCapabilityHandler.Extract(new(200,"https://example.com/a","text/plain",Encoding.UTF8.GetBytes("content"),true),1000);
        Assert.Equal("content",text.Text); Assert.True(text.Truncated);
    }

    [Fact]
    public void CheckpointToolsAreHiddenAndEveryTransferFitsExistingOfficeFrames()
    {
        var checkpoint=ResearchAndCheckpointToolSchemas.Tools.Where(x=>x.Capability.StartsWith("platform.execution-checkpoint.")).ToArray();
        Assert.Equal(2,checkpoint.Length); Assert.All(checkpoint,x=>Assert.False(x.ModelVisible));
        Assert.All(checkpoint,x=>Assert.InRange(x.MaximumOutputBytes,1,16*1024*1024));
        Assert.All(checkpoint,x=>Assert.InRange(x.MaximumInputBytes,1,16*1024*1024));
    }
}
