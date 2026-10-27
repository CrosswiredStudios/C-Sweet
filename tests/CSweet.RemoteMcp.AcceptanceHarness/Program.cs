using System.Security.Claims;
using CSweet.Api.Agents;
using CSweet.UI.Services;
using CSweet.UnitTests;
using CSweet.Agent.SDK;
using CSweet.Agent.RealtyLeadsAI;
using CSweet.RemoteMcp;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using System.Text.Encodings.Web;
using System.Text.Json;
using CSweet.RemoteMcp.AcceptanceHarness;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();
var fixture = await RemoteMcpNativeFlowTests.Fixture.Create();
builder.Services.AddSingleton(fixture);
builder.Services.AddSingleton(fixture.Service);
builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("fixture", _ => { });
builder.Services.AddAuthorization();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddScoped(_ => new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri("http://127.0.0.1:5287/") });
builder.Services.AddScoped<RemoteMcpAccountApiClient>();
var app = builder.Build();
app.UseStaticFiles(); app.UseAuthentication(); app.UseAuthorization(); app.UseAntiforgery();
app.MapRemoteMcpAccountEndpoints();
app.MapGet("/test/identity", () => new { fixture.Business, provider = fixture.Provider.Id, consumer = fixture.Consumer.Id });
app.MapPost("/test/agent-read", async () =>
{
    var runtime = new AgentTestRuntime().RegisterCapability<LeadSearch, InheritanceLead[]>(RealtyLeadsAgent.ProviderSearch, async (query, ct) =>
    {
        var json = JsonSerializer.SerializeToElement(query, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var result = await fixture.Service.ReadAsync(fixture.Business, fixture.Consumer.Id, RealtyPolicy.Search, json, ct,
            vault => new RemoteMcpBroker(vault, () => new RemoteMcpNativeFlowTests.FakeMcp()));
        return result.Deserialize<InheritanceLead[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    });
    try
    {
        var result = await runtime.ExecuteCapabilityAsync(new RealtyLeadsAgent(), RealtyLeadsAgent.Search, new LeadSearch(State: "TX", Limit: 1));
        return result.Succeeded ? Results.Ok(new { succeeded = true }) : Results.StatusCode(403);
    }
    catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
});
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
await app.RunAsync();

internal sealed class FixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    RemoteMcpNativeFlowTests.Fixture fixture) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var id = Request.Headers["X-Fixture-Other-User"] == "1" ? Guid.NewGuid() : fixture.Owner;
        return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], "fixture")), "fixture")));
    }
}
