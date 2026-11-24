using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using CSweet.App;
using CSweet.UI.Services;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var appOrigin = new Uri(builder.HostEnvironment.BaseAddress);
var apiBaseUrl = builder.Configuration["CSweet:ApiBaseUrl"];
if (builder.HostEnvironment.IsDevelopment() && appOrigin.IsLoopback && appOrigin.Scheme == Uri.UriSchemeHttp)
    apiBaseUrl = builder.Configuration["CSweet:LocalHttpApiBaseUrl"] ?? apiBaseUrl;
var httpBaseAddress = string.IsNullOrWhiteSpace(apiBaseUrl)
    ? builder.HostEnvironment.BaseAddress
    : apiBaseUrl;

if (!httpBaseAddress.EndsWith('/'))
{
    httpBaseAddress += "/";
}

if (ShellBridge.IsShellOrigin(appOrigin))
{
    // Inside the C-Sweet phone app: API calls go through the native bridge to this enterprise's Headquarters.
    ShellBridge.IsActive = true;
    builder.Services.AddScoped(sp => new HttpClient(new ShellBridgeHttpHandler(sp.GetRequiredService<Microsoft.JSInterop.IJSRuntime>()))
    {
        BaseAddress = new Uri(builder.HostEnvironment.BaseAddress)
    });
}
else
{
    builder.Services.AddScoped<CookieAndAntiforgeryHandler>();
    builder.Services.AddScoped(sp =>
    {
        var handler = sp.GetRequiredService<CookieAndAntiforgeryHandler>();
        handler.InnerHandler = new HttpClientHandler();
        return new HttpClient(handler) { BaseAddress = new Uri(httpBaseAddress) };
    });
}
builder.Services.AddCSweetApiClients();
builder.Services.AddMudServices();

await builder.Build().RunAsync();
