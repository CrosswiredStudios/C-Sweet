using System.Net;
using System.Net.Http.Json;
using CSweet.Contracts.Plugins;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.ConnectionSelection.AcceptanceHarness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
if (args.Length == 2 && args[0] == "--migration-sql")
{
    using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql("Host=unused;Database=unused").Options);
    var migrator = db.GetService<IMigrator>();
    var before = db.Database.GetMigrations().TakeWhile(x => x != "20261028160000_ConnectionServiceSelection").Last();
    Directory.CreateDirectory(args[1]);
    File.WriteAllText(Path.Combine(args[1], "baseline.sql"), migrator.GenerateScript("0", before));
    File.WriteAllText(Path.Combine(args[1], "upgrade.sql"), migrator.GenerateScript(before, "20261028160000_ConnectionServiceSelection"));
    File.WriteAllText(Path.Combine(args[1], "downgrade.sql"), migrator.GenerateScript("20261028160000_ConnectionServiceSelection", before));
    return;
}
var builder = WebApplication.CreateBuilder(args); builder.WebHost.UseStaticWebAssets();
builder.Services.AddRazorComponents().AddInteractiveServerComponents(); builder.Services.AddMudServices();
var dbOptions = new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase("isolated-selection-" + Guid.NewGuid()).Options;
using (var db = new CSweetDbContext(dbOptions)) await new SetupService(db).EnsureSeededAsync();
builder.Services.AddScoped(_ => new HttpClient(new FixtureHandler(dbOptions)) { BaseAddress = new Uri("https://fixture.invalid/") });
var app = builder.Build(); app.UseStaticFiles(); app.UseAntiforgery(); app.MapRazorComponents<App>().AddInteractiveServerRenderMode(); app.Run();
sealed class FixtureHandler(DbContextOptions<CSweetDbContext> options) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var db = new CSweetDbContext(options); var store = new ConnectionServiceSelectionStore(db, Options.Create(new PluginConnectionOptions()));
        object result;
        if (request.RequestUri!.AbsolutePath.EndsWith("/probe")) result = new ConnectionServiceProbe("UnavailableOrUnsupported", false, false, false);
        else if (request.Method == HttpMethod.Get) result = store.Current();
        else result = await store.Save(Guid.Parse("11111111-1111-1111-1111-111111111111"), (await request.Content!.ReadFromJsonAsync<SaveConnectionServiceChoice>(ct))!, ct);
        return new(HttpStatusCode.OK) { Content = JsonContent.Create(result) };
    }
}
