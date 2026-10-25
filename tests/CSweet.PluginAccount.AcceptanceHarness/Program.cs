using CSweet.PluginAccount.AcceptanceHarness;
using CSweet.UI.Localization;
using CSweet.UI.Services;
using CSweet.UnitTests;
using MudBlazor.Services;
using Microsoft.EntityFrameworkCore;

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
app.MapPost("/test/run-cleanup",async(AcceptanceState data)=>{await data.Run(()=>data.Fixture.Service.ProcessPendingAsync(default));return Results.NoContent();});
app.MapGet("/test/cleanup-receipt-count",async(AcceptanceState data)=>await data.Run(()=>data.Fixture.Inner.Db.PluginAccountCleanupReceipts.CountAsync()));
app.MapGet("/test/memory-confirmation-count",async(AcceptanceState data)=>await data.Run(()=>data.Fixture.Inner.Db.PluginAccountDataCopies.CountAsync(c=>c.Store=="external:memory" && c.DeletedAt!=null)));
// Test-only lineage for an already erased synthetic memory source; no private user data or live memory store.
app.MapPost("/test/seed-synthetic-memory",async(AcceptanceState data)=>
{
    await data.Run(async()=>
    {
        var f=data.Fixture;var account=await f.Inner.Db.PluginAccountGroups.SingleAsync();
        foreach(var store in new[]{"external:memory","memory-origin"})
            f.Inner.Db.PluginAccountDataCopies.Add(new(){Id=Guid.NewGuid(),AccountGroupId=account.Id,OrganizationId=f.Business,
                ConsumerInstallationId=f.Inner.Requester.Id,Store=store,RecordId=store=="memory-origin"?Guid.NewGuid():f.Inner.Requester.Id,CreatedAt=DateTimeOffset.UtcNow});
        await f.Inner.Db.SaveChangesAsync();
    });return Results.NoContent();
});
app.MapPost("/test/confirm-synthetic-cleanup",async(AcceptanceState data)=>
{
    await data.Run(async()=>
    {
        var stores=await data.Fixture.Inner.Db.PluginAccountDataCopies.Where(x=>x.Store.StartsWith("external:")).Select(x=>x.Store).Distinct().ToArrayAsync();
        foreach(var store in stores) if(!data.Fixture.DeletionParticipants.Any(x=>x.Store==store))
        {
            if(store=="external:backups")
            {
                var gateway=new SignedSyntheticCleanupGateway();
                data.Fixture.DeletionParticipants.Add(new CSweet.Infrastructure.Setup.PluginSignedCleanupParticipant(store,data.Fixture.Inner.Db,
                    Microsoft.Extensions.Options.Options.Create(new CSweet.Infrastructure.Setup.PluginCleanupOptions{Destinations=[new(){Store=store,Endpoint="https://cleanup.example.com/erase",ReceiptPublicKeys=new(){["test-key"]=gateway.PublicKey}}]}),gateway,TimeProvider.System));
            }
            else if(store=="external:memory")
            {
                var f=data.Fixture;var partition=new CSweet.Memory.MemoryPartition(f.Business.ToString("D"));var employee=Guid.NewGuid();
                foreach(var source in await f.Inner.Db.PluginAccountDataCopies.Where(c=>c.Store=="memory-origin").ToArrayAsync())
                    f.Inner.Db.MemoryErasureReceipts.Add(new(){Id=Guid.NewGuid(),OrganizationId=f.Business,EmployeeId=employee,EpisodeId=source.RecordId,
                        OperationId=Guid.NewGuid(),ActorApplicationUserId=f.Admin,ActorOrganizationUserId=Guid.NewGuid(),RequestHash=new string('a',64),CreatedAt=DateTimeOffset.UtcNow,
                        InventoryJson=System.Text.Json.JsonSerializer.Serialize(new{audiences=new[]{partition},pendingRuntimes=Array.Empty<object>(),turnIds=Array.Empty<Guid>(),
                            owners=new[]{new{employeeId=employee,partition}},ownershipVersion=1},new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))});
                await f.Inner.Db.SaveChangesAsync();
                f.DeletionParticipants.Add(new CSweet.Infrastructure.Setup.PluginMemoryDeletionParticipant(f.Inner.Db));
            }
            else data.Fixture.DeletionParticipants.Add(new SyntheticDeletionConfirmation(store));
        }
        await data.Fixture.Service.ProcessPendingAsync(default);
    });
    return Results.NoContent();
});
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
