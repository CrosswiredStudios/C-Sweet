using CSweet.Promo.Demo;
using CSweet.Promo.Demo.Components;
using CSweet.UI.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.FileProviders;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5198");
builder.WebHost.UseStaticWebAssets();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSignalR();
builder.Services.AddMudServices();
builder.Services.AddCSweetApiClients();
builder.Services.AddScoped<DemoData>();
builder.Services.AddScoped<FixtureHandler>();
builder.Services.AddScoped(sp => new HttpClient(sp.GetRequiredService<FixtureHandler>()) { BaseAddress = new Uri("http://127.0.0.1:5198/") });
var app = builder.Build();
app.UseStaticFiles();
// Reuse source assets directly; no exported marketing asset replaces an app asset.
var appImages = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "../../src/CSweet.App/wwwroot/images"));
app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(appImages), RequestPath = "/images" });
app.UseAntiforgery();
app.MapStaticAssets();
app.MapHub<DemoHub>("/hubs/app-events");
app.MapGet("/health", () => new { ok = true, fixtureFailures = FixtureHandler.Failures.ToArray() });
app.MapRazorComponents<App>().AddInteractiveServerRenderMode().AddAdditionalAssemblies(CSweet.UI.SharedUiAssembly.Value);
app.Run();

public sealed class DemoHub : Hub
{
    public Task SetCommunicationPerspective(Guid organizationId, Guid? agentOrganizationUserId) => Task.CompletedTask;
}
