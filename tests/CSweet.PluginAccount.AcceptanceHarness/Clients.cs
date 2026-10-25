using System.Text.Json;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Core;
using CSweet.Contracts.Plugins;
using CSweet.UI.Services;
using CSweet.UnitTests;

namespace CSweet.PluginAccount.AcceptanceHarness;

internal sealed class AcceptanceState(PluginAccountFlowTests.Fixture fixture)
{
    public PluginAccountFlowTests.Fixture Fixture { get; private set; } = fixture;
    public SemaphoreSlim Gate { get; } = new(1,1);
    public bool Installed { get; set; }
    public Task Reset() => Run(async () => { await Fixture.DisposeAsync(); Fixture=await PluginAccountFlowTests.Fixture.Create(); Installed=false; });
    public async Task<T> Run<T>(Func<Task<T>> action) { await Gate.WaitAsync(); try { return await action(); } finally { Gate.Release(); } }
    public async Task Run(Func<Task> action) { await Gate.WaitAsync(); try { await action(); } finally { Gate.Release(); } }
    public PluginManifest Manifest => JsonSerializer.Deserialize<PluginManifest>(Fixture.Inner.Connector.PackageVersion!.ManifestJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    public AgentInstallationResponse Installation => new(Fixture.Id, Fixture.Inner.Connector.PackageVersionId, "enterprise", Manifest.Id,
        "Example Connection", "0.2.0", "Test publisher", new string('a',40), true, Manifest.Provides.Select(x=>x.Name).ToArray(), [], [], [], [], 512, 50,
        null!, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { PluginKind="Connector", InstallationScope="System", Accounts=Manifest.Accounts, ConnectionDeclarations=Manifest.Connections };
}
internal sealed class AccountClient(AcceptanceState data) : IPluginAccountApiClient
{
    public Task<PluginAccountManagementView> GetAsync(Guid id, CancellationToken ct = default) => data.Run(() => data.Fixture.Service.GetAsync(data.Fixture.Admin,id,ct));
    public Task<BeginPluginAccountResponse> ConnectAsync(Guid id, ConnectPluginAccountRequest r, CancellationToken ct = default) => data.Run(() => data.Fixture.Service.BeginAsync(data.Fixture.Admin,id,r,ct));
    public Task RetryAsync(Guid id, Guid setup, CancellationToken ct = default) => data.Run(() => data.Fixture.Service.RetryImportAsync(data.Fixture.Admin,id,setup,ct));
    public Task SaveGrantAsync(Guid id, Guid account, SavePluginAccountGrantRequest r, CancellationToken ct = default) => data.Run(() => data.Fixture.Service.SaveGrantAsync(data.Fixture.Admin,id,account,r,ct));
    public Task RevokeGrantAsync(Guid id, Guid grant, long revision, CancellationToken ct = default) => data.Run(() => data.Fixture.Service.RevokeGrantAsync(data.Fixture.Admin,id,grant,revision,ct));
    public Task<PluginAccountImpactView> GetDisconnectImpactAsync(Guid id, Guid account, CancellationToken ct = default) => data.Run(() => data.Fixture.Service.GetDisconnectImpactAsync(data.Fixture.Admin,id,account,ct));
    public Task DisconnectAsync(Guid id, Guid account, DisconnectPluginAccountRequest r, CancellationToken ct = default) => data.Run(() => data.Fixture.Service.DisconnectAsync(data.Fixture.Admin,id,account,r,ct));
}
internal sealed class InstallationClient(AcceptanceState data) : IPluginApiClient
{
    public Task<PluginInstallationPolicyResponse> GetInstallationPolicyAsync(CancellationToken ct = default) => Task.FromResult(new PluginInstallationPolicyResponse(true));
    public Task<IReadOnlyList<AgentInstallationResponse>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AgentInstallationResponse>>(data.Installed ? [data.Installation] : []);
    public Task<AgentImportPreviewResponse> PreviewAsync(PreviewAgentImportRequest r, CancellationToken ct = default) => Task.FromResult(new AgentImportPreviewResponse(Guid.NewGuid(),r.RepositoryUrl,new string('a',40),new string('b',64),data.Manifest.Id,
        "Example Connection","0.2.0","com.example","Test publisher","dotnet-project","Example.csproj","net10.0","OnDemand",data.Manifest.Provides.Select(x=>x.Name).ToArray(),[],[],[],[],[],"Previewed")
        { PluginKind="Connector",Accounts=data.Manifest.Accounts,Connections=data.Manifest.Connections,Setup=data.Manifest.Setup,PackageSourceMode="PrebuiltRelease" });
    public Task<AgentInstallationResponse> InstallAsync(Guid id, InstallAgentRequest r, CancellationToken ct = default)
    { if(r.PluginScope!="System") throw new InvalidOperationException("Enterprise installation scope required."); data.Installed=true; return Task.FromResult(data.Installation); }
    public Task<IReadOnlyList<PluginProviderProfileResponse>> ListProviderProfilesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PluginProviderProfileResponse>>([]);
    public Task<AgentImportPreviewResponse> PreviewBundleAsync(Stream s,string n,CancellationToken ct=default)=>throw new NotSupportedException();
    public Task SaveConfigurationAsync(Guid id,IReadOnlyDictionary<string,string> s,CancellationToken ct=default)=>throw new NotSupportedException();
    public Task SetSecretAsync(Guid id,string k,string v,CancellationToken ct=default)=>throw new NotSupportedException();
    public Task<AgentInstallationResponse> SetEnabledAsync(Guid id,bool e,CancellationToken ct=default)=>throw new NotSupportedException();
    public Task<RemoveAgentInstallationResponse> RemoveAsync(Guid id,CancellationToken ct=default)=>throw new NotSupportedException();
    public Task<PluginProviderProfileResponse> SaveProviderProfileAsync(string id,UpsertPluginProviderProfileRequest r,CancellationToken ct=default)=>throw new NotSupportedException();
    public Task DeleteProviderProfileAsync(string id,CancellationToken ct=default)=>throw new NotSupportedException();
}
internal sealed class TestBusinesses(AcceptanceState data) : IBusinessContext
{
    public IReadOnlyList<OrganizationResponse> Businesses => [new(data.Fixture.Business,"Business A",null,null,null,null,null,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow)];
    public OrganizationResponse? SelectedBusiness => Businesses[0]; public bool IsLoading=>false;public string? ErrorMessage=>null;
    public event Action? Changed { add { } remove { } }
    public Task InitializeAsync(CancellationToken ct=default)=>Task.CompletedTask; public Task RefreshAsync(CancellationToken ct=default)=>Task.CompletedTask;
    public Task SelectAsync(Guid id,bool navigate=true,CancellationToken ct=default)=>Task.CompletedTask;public Task RemoveDeletedBusinessAsync(Guid id,CancellationToken ct=default)=>Task.CompletedTask;
}
