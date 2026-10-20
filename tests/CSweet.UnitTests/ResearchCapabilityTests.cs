using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

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

    [Theory]
    [InlineData(ResearchCapabilities.Search)]
    [InlineData(ResearchCapabilities.Read)]
    public async Task ResearchRequiresItsOwnReviewedGrantBeforeAnyNetworkAccess(string capability)
    {
        var handler=new ResearchCapabilityHandler(null!,new ConfigurationBuilder().Build(),null!);
        var result=await InvokeAsync(handler,Session([]),capability,new WebSearchRequest("docs"));
        Assert.False(result.Succeeded); Assert.Equal("denied",result.FailureCode);
    }

    [Theory]
    [InlineData("http://127.0.0.1/private")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.1.2.3/private")]
    [InlineData("http://[::1]/private")]
    public async Task ReadUsesTheProtectedTransportAndRejectsPrivateDestinations(string url)
    {
        await using var db=new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var session=Session([ResearchCapabilities.Read,PluginPlatformCapabilities.WebFetch]);
        var installation=Guid.Parse(session.InstallationId);
        db.AgentInstallations.Add(new() {Id=installation,IsEnabled=true,SetupState=PluginSetupState.Ready,
            PackageVersion=new() {Id=Guid.NewGuid(),ManifestJson=JsonSerializer.Serialize(new PluginManifest {WebAccess=new(){Mode=PluginWebAccessMode.AllPublic}})},
            Grant=new() {Id=Guid.NewGuid(),AgentInstallationId=installation,NetworkAccessJson="[\"all-public\"]"}});
        await db.SaveChangesAsync();
        var web=new PlatformWebProxyCapabilityHandler(db,null!,null!,new TestAuditEventWriter(),NullLogger<PlatformWebProxyCapabilityHandler>.Instance);
        var handler=new ResearchCapabilityHandler(web,new ConfigurationBuilder().Build(),null!);
        var result=await InvokeAsync(handler,session,ResearchCapabilities.Read,new WebPageRequest(url));
        Assert.False(result.Succeeded); Assert.Contains("Destination",result.Error);
    }

    [Fact]
    public async Task ResearchReadDoesNotReplaceTheUnderlyingPublicWebGrant()
    {
        var web=new PlatformWebProxyCapabilityHandler(null!,null!,null!,null!,NullLogger<PlatformWebProxyCapabilityHandler>.Instance);
        var handler=new ResearchCapabilityHandler(web,new ConfigurationBuilder().Build(),null!);
        var result=await InvokeAsync(handler,Session([ResearchCapabilities.Read]),ResearchCapabilities.Read,new WebPageRequest("https://example.com"));
        Assert.False(result.Succeeded); Assert.Contains("not granted",result.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchOutagesAndOversizedResponsesReturnBoundedFailures(bool oversized)
    {
        var factory=new SearchClients(oversized);
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["CSweet:Search:Searxng:BaseUrl"]="http://search-service" }).Build();
        var result=await InvokeAsync(new(null!,config,factory),Session([ResearchCapabilities.Search]),ResearchCapabilities.Search,new WebSearchRequest("documentation"));
        Assert.False(result.Succeeded); Assert.InRange(result.Error!.Length,1,200); Assert.Equal(1,factory.Calls);
    }

    private static AgentSession Session(string[] capabilities)=>new("session","research",Guid.NewGuid().ToString(),Guid.NewGuid().ToString(),Guid.NewGuid().ToString(),"tick",
        new AuthorizedAgentGrant(new HashSet<string>(),new HashSet<string>(),capabilities.ToHashSet(),1));
    private static async Task<CapabilityResult> InvokeAsync(ResearchCapabilityHandler handler,AgentSession session,string capability,object input)
    {
        var results=new List<CapabilityResult>();
        await foreach(var result in handler.HandleAsync(session,new() {RequestId="research",Capability=capability,Payload=JsonPayload.From(input,new(JsonSerializerDefaults.Web))},default)) results.Add(result);
        return Assert.Single(results);
    }
    private sealed class SearchClients(bool oversized) : HttpMessageHandler,IHttpClientFactory
    {
        internal int Calls;
        public HttpClient CreateClient(string name) {Assert.Equal("searxng-research",name); return new(this,false);}
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(oversized?System.Net.HttpStatusCode.OK:System.Net.HttpStatusCode.ServiceUnavailable)
                {Content=new StringContent(oversized?new string('x',1024*1024+10):"Unavailable")});
        }
    }
}
