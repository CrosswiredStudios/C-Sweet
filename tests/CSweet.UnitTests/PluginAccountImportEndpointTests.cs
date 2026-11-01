using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CSweet.Api.Agents;
using CSweet.ConnectionService.Protocol;
using CSweet.Contracts.Plugins;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CSweet.UnitTests;

public sealed class PluginAccountImportEndpointTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AuthenticatedResumeReturnsSavedReceiptForModernAndLegacyBodiesWithoutStartingDiscovery(bool modernBody)
    {
        await using var f = await PluginAccountFlowTests.Fixture.Create();
        using var manifest = JsonDocument.Parse(f.Inner.Connector.PackageVersion!.ManifestJson);
        var gateway = new ManagedPluginAccountFlowTests.Gateway(manifest.RootElement.GetProperty("connections")[0].GetProperty("providerProfile").GetString()!)
        { DiscoveryError = new HttpRequestException() };
        f.Managed = gateway; var binding = DeploymentProofCodec.Random();
        var begin = await f.Service.BeginAsync(f.Admin, f.Id, new(["base"], "v1", true, f.Digest), default, binding);
        await f.Service.CompleteManagedAsync(f.Admin, begin.SetupId, new string('h', 43), binding, default);
        await f.Service.ImportAsync(begin.SetupId, default);
        var original = await f.Inner.Db.PluginAccountSetupSessions.SingleAsync(x => x.Id == begin.SetupId);
        var expected = original.Revision;

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddScoped(_ => f.Service);
        builder.Services.AddScoped<ManagedPluginConnectionGateway>(_ => throw new NotSupportedException());
        await using var app = builder.Build(); // No server/listener or production hosted services are started.
        app.MapPluginAccountEndpoints();
        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints).OfType<RouteEndpoint>(),
            x => x.RoutePattern.RawText!.EndsWith("/setup/{setupId:guid}/retry", StringComparison.Ordinal));
        Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());

        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.User = new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, f.Admin.ToString("D"))], "synthetic"));
        context.Request.Method = "POST";
        context.Request.RouteValues["installationId"] = f.Id.ToString("D");
        context.Request.RouteValues["setupId"] = begin.SetupId.ToString("D");
        var body = modernBody ? Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new ResumePluginImportRequest(expected))) : [];
        context.Request.ContentType = "application/json"; context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body); context.Response.Body = new MemoryStream();
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection(body.Length > 0));
        await endpoint.RequestDelegate!(context);
        Assert.Equal(200, context.Response.StatusCode); context.Response.Body.Position = 0;
        var receipt = await JsonSerializer.DeserializeAsync<PluginImportResumeReceipt>(context.Response.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(receipt); Assert.Equal(begin.SetupId, receipt.SetupId); Assert.Equal(expected + 1, receipt.Revision);
        Assert.NotEqual(Guid.Empty, receipt.CorrelationId); Assert.Equal(1, gateway.Discoveries);
        Assert.Equal("Importing", (await f.Inner.Db.PluginAccountSetupSessions.SingleAsync(x => x.Id == begin.SetupId)).Status);
    }

    private sealed class BodyDetection(bool hasBody) : IHttpRequestBodyDetectionFeature
    { public bool CanHaveBody => hasBody; }
}
