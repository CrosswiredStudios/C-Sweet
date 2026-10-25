using CSweet.PluginAccount.AcceptanceHarness;
using CSweet.UI.Localization;
using CSweet.UI.Services;
using CSweet.UnitTests;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices(); builder.Services.AddCSweetLocalization();
var state = new AcceptanceState(await PluginAccountFlowTests.Fixture.Create());
builder.Services.AddSingleton(state);
builder.Services.AddScoped<IPluginApiClient, InstallationClient>();
builder.Services.AddScoped<IPluginAccountApiClient, AccountClient>();
builder.Services.AddScoped<IBusinessContext, TestBusinesses>();
var app = builder.Build();
app.UseStaticFiles(); app.UseAntiforgery();
app.MapPost("/test/reset", async (AcceptanceState data) => { await data.Reset(); return Results.NoContent(); });
app.MapGet("/test/provider-return", async (string state, AcceptanceState data) =>
{
    await data.Gate.WaitAsync();
    try
    {
        var result = await data.Fixture.Service.CompleteAsync(data.Fixture.Admin, "test-code", state, null, default);
        await data.Fixture.Service.ImportAsync(result.SetupId, default);
        return Results.Redirect($"/settings/plugins/{result.InstallationId:D}?setup={result.SetupId:D}");
    }
    finally { data.Gate.Release(); }
});
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
await app.RunAsync();
