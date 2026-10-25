using System.Text.Json;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Core;
using CSweet.Contracts.Plugins;
using CSweet.UI.Services;
using CSweet.UnitTests;
using CSweet.Domain.Setup;

namespace CSweet.PluginAccount.AcceptanceHarness;

// Uses the production verifier, but the gateway/key and data are explicitly synthetic.
internal sealed class SignedSyntheticCleanupGateway:CSweet.Infrastructure.Setup.IPluginCleanupTransport
{
    private readonly System.Security.Cryptography.RSA key=System.Security.Cryptography.RSA.Create(3072);
    public string PublicKey=>key.ExportSubjectPublicKeyInfoPem();
    public bool IsConfigured=>true;
    public Task<CSweet.Infrastructure.Setup.PluginSignedCleanupConfirmation?> RequestAsync(Uri endpoint,CSweet.Infrastructure.Setup.PluginCleanupRequest request,CancellationToken ct)
    {
        var now=DateTimeOffset.UtcNow;
        var payload=JsonSerializer.SerializeToUtf8Bytes(new CSweet.Infrastructure.Setup.PluginCleanupConfirmation("1",request.RequestHash,"Deleted",now,now.AddHours(1)),new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Task.FromResult<CSweet.Infrastructure.Setup.PluginSignedCleanupConfirmation?>(new("test-key",Convert.ToBase64String(payload),
            Convert.ToBase64String(key.SignData(payload,System.Security.Cryptography.HashAlgorithmName.SHA256,System.Security.Cryptography.RSASignaturePadding.Pkcs1))));
    }
}

// Test-only acknowledgement: never registered in the production application.
internal sealed class SyntheticDeletionConfirmation(string store):CSweet.Application.Setup.IPluginAccountDataDeletionParticipant
{
    public string Store=>store;
    public Task<bool> DeleteAsync(Guid accountId,Guid? organizationId,Guid consumerInstallationId,Guid recordId,CancellationToken ct)=>Task.FromResult(true);
}

internal sealed class AcceptanceState(PluginAccountFlowTests.Fixture fixture)
{
    public PluginAccountFlowTests.Fixture Fixture { get; private set; } = fixture;
    public SemaphoreSlim Gate { get; } = new(1,1);
    public bool Installed { get; set; }
    public Task Reset() => Run(async () => {
        await Fixture.DisposeAsync(); Fixture=await PluginAccountFlowTests.Fixture.Create(); Installed=false;
        Fixture.Environment.EnvironmentName="Production";Fixture.AllowDevelopmentPreview=false;
        var package=Fixture.Inner.Connector.PackageVersion!;
        package.PackageSourceId=Guid.NewGuid();
        Fixture.Inner.Db.AgentPackageSources.Add(new(){Id=package.PackageSourceId,RepositoryUrl="https://github.com/example/platform-connector",RepositoryOwner="example",RepositoryName="platform-connector",DefaultBranch="main"});
        await Fixture.Inner.Db.SaveChangesAsync();
    });
    public async Task<T> Run<T>(Func<Task<T>> action) { await Gate.WaitAsync(); try { return await action(); } finally { Gate.Release(); } }
    public async Task Run(Func<Task> action) { await Gate.WaitAsync(); try { await action(); } finally { Gate.Release(); } }
    public PluginManifest Manifest => JsonSerializer.Deserialize<PluginManifest>(Fixture.Inner.Connector.PackageVersion!.ManifestJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    public AgentInstallationResponse Installation => new(Fixture.Id, Fixture.Inner.Connector.PackageVersionId, "enterprise", Manifest.Id,
        "Example Connection", Manifest.Version, "Test publisher", new string('a',40), true, Manifest.Provides.Select(x=>x.Name).ToArray(), [], [], [], [], 512, 50,
        null!, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { PluginKind="Connector", InstallationScope="System", Accounts=Manifest.Accounts, ConnectionDeclarations=Manifest.Connections };
}
internal sealed class AccountClient(AcceptanceState data) : IPluginAccountApiClient
{
    public Task SaveOperatorAsync(Guid id,SavePluginOperatorRequest r,CancellationToken ct=default)=>data.Run(()=>data.Fixture.Service.SaveOperatorAsync(data.Fixture.Admin,id,r,ct));
    public Task RevokeOperatorAsync(Guid id,Guid op,long revision,CancellationToken ct=default)=>data.Run(()=>data.Fixture.Service.RevokeOperatorAsync(data.Fixture.Admin,id,op,revision,ct));
    public Task<PluginInstallationImpact> GetImpactAsync(Guid id,CancellationToken ct=default)=>data.Run(()=>data.Fixture.Service.GetInstallationImpactAsync(data.Fixture.Admin,id,ct));
    public Task ControlAsync(Guid id,PluginInstallationControlRequest r,CancellationToken ct=default)=>data.Run(()=>data.Fixture.Service.ControlInstallationAsync(data.Fixture.Admin,id,r,ct));
    public Task RetryBuildAsync(Guid id,CancellationToken ct=default)=>Task.CompletedTask;
    public Task<PluginPackageReview> GetReviewAsync(Guid id,CancellationToken ct=default)=>Task.FromResult(new PluginPackageReview(data.Fixture.Digest,data.Fixture.Inner.Connector.PackageVersion!.ManifestJson,"enterprise"));
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
    public Task<AgentBuildLogResponse?> GetBuildLogAsync(Guid id,CancellationToken ct=default)=>Task.FromResult<AgentBuildLogResponse?>(new(Guid.NewGuid(),"Succeeded","Synthetic package build diagnostics. No real repository compilation was performed.",false));
    public Task<PluginInstallationPolicyResponse> GetInstallationPolicyAsync(CancellationToken ct = default) => Task.FromResult(new PluginInstallationPolicyResponse(true));
    public Task<IReadOnlyList<AgentInstallationResponse>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AgentInstallationResponse>>(data.Installed ? [data.Installation] : []);
    public Task<AgentImportPreviewResponse> PreviewAsync(PreviewAgentImportRequest r, CancellationToken ct = default) => Task.FromResult(new AgentImportPreviewResponse(Guid.NewGuid(),r.RepositoryUrl,new string('a',40),new string('b',64),data.Manifest.Id,
        "Example Connection",data.Manifest.Version,"com.example","Test publisher","dotnet-project","Example.csproj","net10.0","OnDemand",data.Manifest.Provides.Select(x=>x.Name).ToArray(),[],[],[],[],[],"Previewed")
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
