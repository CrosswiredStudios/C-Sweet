using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Application.Setup;
using CSweet.ConnectionService.Protocol;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.Communications;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ManagedConnectorAuthorityTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task PublishedYouTube022FourOperationOnboardingUsesBoundChannelAndNoUploadOrSchedule()
    {
        await using var f=await Fixture.Create();await f.UsePublishedYouTube();
        var original=await f.Base.Work();
        await f.Base.Recover(await f.Base.Preview());
        var session=f.Base.Session();var inbox=new AgentWorkInbox(f.Db,f.Base.Protection,f.Base.Clock);
        var claim=Assert.IsType<ClaimedAgentWork>(await inbox.ClaimAsync(session,default));
        Assert.Equal(f.Base.Org,claim.Payload.Deserialize<AgentOnboardedEvent>(Json)!.OrganizationId);
        var tools=await ConnectorTools(f.Catalog.ListAsync(f.AgentSession(),f.Db,default));
        Assert.Equal(new[]{"youtube.api.channel.read.v1","youtube.api.video-category.list.v1","youtube.api.video.read.v1"},tools.Select(x=>x.Capability).Order().ToArray());
        var result=await f.Read();Assert.Equal("UC_SYNTHETIC_CHANNEL",result.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal("youtube.api.channel.read.v1",f.Gateway.LastRead!.Capability);
        Assert.Equal("UC_SYNTHETIC_CHANNEL",f.Gateway.LastRead.ResourceId);
        Assert.True(await f.Base.Acknowledge());
        await inbox.CompleteAsync(session,claim.WorkId,claim.Attempt,claim.LeaseToken,new(true,null,null),default);
        Assert.Equal(original.Id,(await f.Base.Work()).Id);Assert.Equal(AgentOnboardingEventOutboxStatus.Delivered,(await f.Base.Event()).Status);
        Assert.Equal(1,f.Gateway.Reads);Assert.Equal(0,f.Direct.Calls);
        Assert.Empty(await f.Db.ActionProposals.ToArrayAsync());Assert.Empty(await f.Db.AgentSchedules.ToArrayAsync());
        Assert.Empty(await f.Db.ConnectorProfileApprovals.ToArrayAsync());
    }

    [Fact]
    public async Task ProtectedOriginalOnboardingReachesCatalogManagedReadAndAcknowledgementWithoutLocalApproval()
    {
        await using var f = await Fixture.Create();
        var original = await f.Base.Work();
        var receipt = await f.Base.Recover(await f.Base.Preview());
        var session = f.Base.Session();
        var inbox = new AgentWorkInbox(f.Db, f.Base.Protection, f.Base.Clock);
        var claim = Assert.IsType<ClaimedAgentWork>(await inbox.ClaimAsync(session, default));
        Assert.Equal(original.Id, claim.WorkId); Assert.Equal(2, claim.Attempt);
        var payload = claim.Payload.Deserialize<AgentOnboardedEvent>(Json)!;
        Assert.Equal(f.Base.Org, payload.OrganizationId);
        Assert.Equal(f.Base.Employee, payload.AgentOrganizationUserId);
        Assert.Equal(f.Base.Human, payload.HiringOrganizationUserId);
        var tool = Assert.Single(await ConnectorTools(f.Catalog.ListAsync(f.AgentSession(), f.Db, default)));
        Assert.Equal(ConnectorPlanServiceTests.Fixture.Capability, tool.Capability);
        var result = await f.Read();
        Assert.Equal("synthetic-channel", result.GetProperty("data").GetString());
        Assert.True(await f.Base.Acknowledge());
        await inbox.CompleteAsync(session, claim.WorkId, claim.Attempt, claim.LeaseToken, new(true, null, null), default);
        Assert.Equal(AgentOnboardingEventOutboxStatus.Delivered, (await f.Base.Event()).Status);
        Assert.Equal(AgentWorkStatus.Completed, (await f.Base.Work()).Status);
        Assert.Equal(receipt.WorkId, (await f.Base.Work()).Id);
        Assert.Single(await f.Db.AgentOnboardingEventOutbox.ToArrayAsync());
        Assert.Single(await f.Db.AgentWorkItems.ToArrayAsync());
        Assert.Equal(1, f.Gateway.Reads); Assert.Equal(0, f.Direct.Calls);
        Assert.Empty(await f.Db.ConnectorProfileApprovals.ToArrayAsync());
        Assert.All(f.Store.ReadKeys, key => Assert.StartsWith("managed.context.", key));
        Assert.Equal(original.ProtectedPayload, (await f.Base.Work()).ProtectedPayload);
    }

    [Theory]
    [InlineData("missing-context")]
    [InlineData("malformed-context")]
    [InlineData("corrupt-context")]
    [InlineData("corrupt-registration")]
    [InlineData("unsupported-registration-api")]
    [InlineData("unknown-context-field")]
    [InlineData("context-origin")]
    [InlineData("empty-remote")]
    [InlineData("empty-session")]
    [InlineData("registered-tenant")]
    [InlineData("registered-origin")]
    [InlineData("unpaired")]
    [InlineData("mode-disabled")]
    [InlineData("remote-revoked")]
    [InlineData("remote-wrong-id")]
    [InlineData("remote-zero-revision")]
    [InlineData("remote-scope")]
    [InlineData("profile-id")]
    [InlineData("profile-binding")]
    [InlineData("profile-revision")]
    [InlineData("missing-consent")]
    [InlineData("consent-account")]
    [InlineData("consent-status")]
    [InlineData("consent-profile")]
    [InlineData("consent-package")]
    [InlineData("business-revoked")]
    [InlineData("business-foreign")]
    [InlineData("binding-revoked")]
    [InlineData("binding-digest")]
    [InlineData("resource-stale")]
    [InlineData("resource-proof")]
    [InlineData("provider-grant")]
    [InlineData("local-scopes")]
    [InlineData("unproven-subject")]
    public async Task EveryManagedAndNativeBoundaryFailsClosedWithoutLocalFallback(string change)
    {
        await using var f = await Fixture.Create(keepLocalApproval: true);
        Assert.True(await f.Plans.IsAvailableAsync(f.Base.Org, f.Base.Installation, ConnectorPlanServiceTests.Fixture.Capability, default));
        Assert.Single(await ConnectorTools(f.Catalog.ListAsync(f.AgentSession(), f.Db, default)));
        await f.Change(change);
        Assert.False(await f.Plans.IsAvailableAsync(f.Base.Org, f.Base.Installation, ConnectorPlanServiceTests.Fixture.Capability, default));
        Assert.Empty(await ConnectorTools(f.Catalog.ListAsync(f.AgentSession(), f.Db, default)));
        await Assert.ThrowsAnyAsync<Exception>(() => f.Base.Preview());
        await Assert.ThrowsAnyAsync<Exception>(() => f.Prepare());
        Assert.Equal(0, f.Gateway.Reads); Assert.Equal(0, f.Direct.Calls);
        Assert.Empty(await f.Db.ConnectorExecutions.ToArrayAsync());
        Assert.Equal(AgentWorkStatus.DeadLetter, (await f.Base.Work()).Status);
    }

    [Theory]
    [InlineData("deployment-id")]
    [InlineData("deployment-revision")]
    [InlineData("profile-public-revision")]
    [InlineData("remote-session")]
    [InlineData("consent-revision")]
    public async Task ChangedManagedAuthorityInvalidatesFrozenPlansResultsAndRecoveryReview(string change)
    {
        await using var f = await Fixture.Create();
        var review = await f.Base.Preview(); var plan = await f.Prepare();
        await f.Change(change);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Plans.RevalidateAsync(f.Base.Org, f.Base.Installation, plan.Id, plan.PlanHash, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Plans.ValidateResultAuthorityAsync(f.Base.Org, f.Base.Installation, plan.Id, plan.PlanHash, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Base.Recover(review));
        Assert.Equal(2, (await f.Base.Preview()).PermittedNextAttempt);
        Assert.Equal(0, f.Gateway.Reads);
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("hash")]
    public async Task ProtectedOnboardingPayloadCannotBeChangedToGainRecovery(string change)
    {
        await using var f = await Fixture.Create();
        await f.Base.Change(async db => { var work = await db.AgentWorkItems.SingleAsync();
            if (change == "payload") work.ProtectedPayload = [1,2,3]; else work.PayloadHash = new string('b',64); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Base.Preview());
        Assert.Equal(0, f.Gateway.Reads); Assert.Empty(await f.Db.PluginOperationalStates.ToArrayAsync());
    }

    [Fact]
    public async Task ServiceReadPolicyIsIndependentAndCannotBeGrantedByCoreAuthority()
    {
        await using var f = await Fixture.Create(); f.Gateway.AllowReads = false;
        Assert.Single(await ConnectorTools(f.Catalog.ListAsync(f.AgentSession(), f.Db, default)));
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Read());
        Assert.Equal(1, f.Gateway.Reads); Assert.Equal(0, f.Gateway.ProviderEffects);
        Assert.Null((await f.Db.ConnectorExecutions.SingleAsync()).ResultJson);
    }

    [Fact]
    public async Task RemoteRevocationWhileReadIsInFlightWithholdsResultAndCache()
    {
        await using var f = await Fixture.Create(); f.Gateway.AfterRead = () => f.Gateway.Status = "Revoked";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Read());
        Assert.Equal(1, f.Gateway.Reads); Assert.Null((await f.Db.ConnectorExecutions.SingleAsync()).ResultJson);
    }

    [Fact]
    public async Task OrdinaryRemoteRefreshRevisionDoesNotChangeFrozenExecutionAuthority()
    {
        await using var f=await Fixture.Create();var plan=await f.Prepare();
        f.Gateway.Revision++;
        await f.Plans.RevalidateAsync(f.Base.Org,f.Base.Installation,plan.Id,plan.PlanHash,default);
        f.Gateway.AfterRead=()=>f.Gateway.Revision+=2;
        Assert.Equal("synthetic-channel",(await f.Read()).GetProperty("data").GetString());
    }

    [Fact]
    public async Task MissingResolverOrUnsupportedGatewayCannotCreateManagedAuthority()
    {
        await using var f = await Fixture.Create(keepLocalApproval:true);
        Assert.False(await new ConnectorPlanService(f.Db).IsAvailableAsync(f.Base.Org, f.Base.Installation, ConnectorPlanServiceTests.Fixture.Capability, default));
        var missing = new ConnectorPlanService(f.Db, new ConnectorCredentialAuthority(f.Db, f.Store));
        Assert.False(await missing.IsAvailableAsync(f.Base.Org, f.Base.Installation, ConnectorPlanServiceTests.Fixture.Capability, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedAdoptionAndArchivedConsentRetainManagedExecutionAuthority(bool archived)
    {
        await using var f=await Fixture.Create();await f.Adopt();
        if(archived)await f.Base.Change(async db=>{(await db.PluginAccountSetupSessions.SingleAsync()).Status="Archived";});
        var plan=await f.Prepare();var frozen=JsonSerializer.Deserialize<FrozenConnectorPlan>(plan.PlanJson,Json)!;
        Assert.Equal(f.AdoptionId,frozen.CredentialAuthority!.AdoptionId);
        Assert.Single(await ConnectorTools(f.Catalog.ListAsync(f.AgentSession(),f.Db,default)));
        Assert.Equal("synthetic-channel",(await f.Read()).GetProperty("data").GetString());
        Assert.True(f.Gateway.AdoptionReads>0);Assert.Equal(1,f.Gateway.Reads);
        Assert.Empty(await f.Db.ConnectorProfileApprovals.ToArrayAsync());
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("origin")]
    [InlineData("target")]
    [InlineData("source-digest")]
    [InlineData("target-digest")]
    [InlineData("actor")]
    [InlineData("remote-session")]
    [InlineData("account")]
    [InlineData("ambiguous")]
    [InlineData("missing-remote-receipt")]
    [InlineData("receipt-hash")]
    [InlineData("receipt-target")]
    [InlineData("receipt-revoked")]
    public async Task InvalidAdoptionHistoryCannotAuthorizeManagedExecution(string change)
    {
        await using var f=await Fixture.Create();
        Assert.True(await f.Plans.IsAvailableAsync(f.Base.Org,f.Base.Installation,ConnectorPlanServiceTests.Fixture.Capability,default));
        await f.Adopt(change);
        Assert.False(await f.Plans.IsAvailableAsync(f.Base.Org,f.Base.Installation,ConnectorPlanServiceTests.Fixture.Capability,default));
        Assert.Empty(await ConnectorTools(f.Catalog.ListAsync(f.AgentSession(),f.Db,default)));
        await Assert.ThrowsAnyAsync<Exception>(()=>f.Base.Preview());
        Assert.Equal(0,f.Gateway.Reads);
    }

    [Fact]
    public async Task ManagedStandingPolicyUsesFrozenProvenanceAndDoesNotCreateLocalProfileApproval()
    {
        await using var f=await Fixture.Create();
        PluginProviderOperationDeclaration operation=null!;
        await f.Base.Change(async db=>
        {
            var connector=await db.AgentInstallations.Include(x=>x.PackageVersion).SingleAsync(x=>x.Id==f.Connector);
            var manifest=JsonSerializer.Deserialize<PluginManifest>(connector.PackageVersion!.ManifestJson,Json)!;
            operation=manifest.ProviderOperations[0] with {Effect="write",Idempotency="caller-key",
                Http=manifest.ProviderOperations[0].Http! with {Method="POST",BodyInputs=new Dictionary<string,string>{{"/title","/search"}}}};
            connector.PackageVersion.ManifestJson=JsonSerializer.Serialize(manifest with {ProviderOperations=[operation,..manifest.ProviderOperations.Skip(1)],
                Provides=[manifest.Provides[0] with {Idempotency="caller-key"},..manifest.Provides.Skip(1)]},Json);
            db.AgentInstallationConfigurations.Add(new(){Id=Guid.NewGuid(),AgentInstallationId=f.Base.Installation,SchemaVersion="1",SettingsJson="{\"approvalMode\":\"Fully Autonomous\"}"});
        });
        var plan=await f.Prepare();var policies=new ConnectorStandingPolicyService(f.Db,f.Plans,new TestAuditEventWriter());
        var review=await policies.ReviewAsync(f.Base.Org,f.Base.Installation,f.Base.Actor,plan.Id,plan.PlanHash,default);
        var now=DateTimeOffset.UtcNow;
        var definition=new ConnectorStandingPolicyDefinition(ConnectorStandingPolicyRules.Fields(operation).Select(x=>new ConnectorPolicyFieldRule(x,true,[])).ToArray(),
            [0,1,2,3,4,5,6],0,1440,2,now.AddMinutes(-1),now.AddHours(1),[]);
        await policies.ApproveAsync(f.Base.Org,f.Base.Installation,f.Base.Actor,new(plan.Id,plan.PlanHash,review.ReviewHash,definition,null),default);
        var authorization=Assert.IsType<ConnectorStandingPolicyService.Authorization>(await policies.TryReserveAsync(f.Base.Org,f.Base.Installation,plan.Id,plan.PlanHash,default));
        f.Gateway.Binding=f.Gateway.Binding with {Revision=2};
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>policies.RequireAuthorizationAsync(f.Base.Org,f.Base.Installation,plan.Id,plan.PlanHash,authorization,default));
        Assert.Empty(await f.Db.ConnectorProfileApprovals.ToArrayAsync());Assert.Equal(0,f.Gateway.Reads);
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        public OperatorOnboardingRecoveryTests.Fixture Base = null!;
        public CSweetDbContext Db => Base.Db;
        public Store Store = new(); public Gateway Gateway = null!; public ForbiddenTransport Direct = new();
        public ConnectorPlanService Plans => new(Db, Base.Credentials);
        public McpToolCatalog Catalog => new([], credentials: Base.Credentials);
        public Guid Connector => Base.Native.Legacy.Connector.Id;
        public Guid Connection => Base.Native.Connection.Id;
        public string ContextKey => $"managed.context.{Connection:N}";
        public ManagedAccountAttempt Context = null!;
        public Guid Consent;
        public Guid? AdoptionId;
        public string Capability=ConnectorPlanServiceTests.Fixture.Capability;
        public string[] ApprovedCapabilities=[ConnectorPlanServiceTests.Fixture.Capability,AgentLifecycleCapabilities.CompleteOnboarding];
        public JsonElement Input=ConnectorPlanServiceTests.Fixture.Input("synthetic");
        public Task<ConnectorExecution> Prepare() => Plans.PrepareAsync(Base.Org, Base.Installation, Capability,Input,"once", default);
        public AgentSession AgentSession() => new("synthetic", "example.agent", Base.Installation.ToString("D"), Base.Org.ToString("D"), "runtime", "tick",
            new(new HashSet<string>(),new HashSet<string>(),ApprovedCapabilities.ToHashSet(StringComparer.Ordinal),1));
        public Task<JsonElement> Read()
        {
            var plans = Plans; var audit = new TestAuditEventWriter();
            return new ConnectorReadExecutor(Db,plans,Direct,Store,audit,new ManagedConnectorRelay(Db,Store,Gateway,plans,new(Db,plans,audit)))
                .ExecuteAsync(Base.Org,Base.Installation,Capability,Input,"once",default);
        }
        public async Task UsePublishedYouTube()
        {
            using var stream=typeof(ManagedConnectorAuthorityTests).Assembly.GetManifestResourceStream("YouTubeConnector.0.2.2.json")!;
            using var bytes=new MemoryStream();await stream.CopyToAsync(bytes);var raw=bytes.ToArray();
            Assert.Equal("19fbc935b5ea4040778601b08efd27acd6918196f5f6db6231637d47873577c3",Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(raw)).ToLowerInvariant());
            var manifest=JsonSerializer.Deserialize<PluginManifest>(raw,Json)!;PluginManifestReader.ValidateConnectorContracts(manifest);
            Capability="youtube.api.channel.read.v1";Input=JsonSerializer.SerializeToElement(new{});
            var selected=new[]{Capability,"youtube.api.video-category.list.v1","youtube.api.video.read.v1","youtube.api.video.upload.v1"};
            ApprovedCapabilities=[..selected,AgentLifecycleCapabilities.CompleteOnboarding];
            var declaration=manifest.Connections.Single();var sets=manifest.ProviderOperations.Where(x=>selected.Contains(x.Capability)).SelectMany(x=>x.Http!.ScopeSets).Distinct().ToArray();
            Gateway.Profile=Gateway.Profile with {ProfileId=declaration.ProviderProfile};Gateway.ScopeSets=sets;
            Gateway.Result=JsonSerializer.SerializeToElement(new{items=new[]{new{id="UC_SYNTHETIC_CHANNEL",snippet=new{title="Synthetic Channel"}}}});
            await Base.Change(async db=>
            {
                var connector=await db.AgentInstallations.Include(x=>x.PackageVersion).Include(x=>x.Grant).SingleAsync(x=>x.Id==Connector);
                connector.PackageVersion!.AgentId=manifest.Id;connector.PackageVersion.Version=manifest.Version;connector.PackageVersion.PublisherId="com.csweet";
                connector.PackageVersion.ManifestJson=System.Text.Encoding.UTF8.GetString(raw);
                connector.Grant!.ProvidedCapabilitiesJson=JsonSerializer.Serialize(manifest.Provides.Select(x=>x.Name));
                var requester=await db.AgentInstallations.Include(x=>x.PackageVersion).Include(x=>x.Grant).SingleAsync(x=>x.Id==Base.Installation);
                requester.PackageVersion!.ManifestJson=JsonSerializer.Serialize(new PluginManifest{Requires=[..selected.Select(x=>new PluginCapabilityRequirement{Name=x,Dependency="youtube"}),new(){Name=AgentLifecycleCapabilities.CompleteOnboarding}],
                    Dependencies=[new(){Id="youtube",PluginId=manifest.Id,PublisherId="com.csweet",MinimumVersion="0.2.2",MaximumVersionExclusive="0.3.0"}]},Json);
                requester.Grant!.RequiredCapabilitiesJson=JsonSerializer.Serialize(ApprovedCapabilities);
                var connection=await db.PluginConnections.SingleAsync();connection.DeclarationId=declaration.Id;connection.ProviderProfile=declaration.ProviderProfile;
                connection.GrantedScopesJson=JsonSerializer.Serialize(declaration.ScopeSets.Where(x=>sets.Contains(x.Id)).SelectMany(x=>x.Scopes).Distinct().ToArray());
                var group=await db.PluginAccountGroups.SingleAsync();group.ProviderProfile=declaration.ProviderProfile;
                group.IdentityAuthority=manifest.ProviderOperations.Single(x=>x.Capability==manifest.Accounts!.Identity!.Capability).Http!.Endpoint;
                var resource=await db.PluginAccountResources.SingleAsync();resource.ResourceType=manifest.Accounts!.ResourceType;resource.ExternalResourceId="UC_SYNTHETIC_CHANNEL";
                var grant=await db.PluginAccountBusinessGrants.SingleAsync();grant.CapabilitiesJson=JsonSerializer.Serialize(selected);grant.PolicyVersion=manifest.Accounts.PolicyVersion;
                var session=await db.PluginAccountSetupSessions.SingleAsync();session.PolicyVersion=manifest.Accounts.PolicyVersion;session.ScopeSetIdsJson=JsonSerializer.Serialize(sets);
                var binding=await db.AgentCapabilityBindings.SingleAsync();binding.Capability=Capability;binding.DependencyId="youtube";
                foreach(var capability in selected.Skip(1))db.AgentCapabilityBindings.Add(new(){Id=Guid.NewGuid(),OrganizationId=Base.Org.ToString("D"),RequesterInstallationId=Base.Installation,
                    ProviderInstallationId=Connector,DependencyId="youtube",Capability=capability,GrantRevision=binding.GrantRevision,ProviderPackageDigest=binding.ProviderPackageDigest,
                    PluginAccountBusinessGrantId=grant.Id,PluginAccountResourceId=resource.Id,PluginAccountConnectionId=connection.Id});
            });
        }
        public async Task Change(string change)
        {
            switch (change)
            {
                case "missing-context": Store.Values.Clear(); return;
                case "malformed-context": Store.Values[ContextKey]="malformed"; return;
                case "corrupt-context": Store.Corrupt=true; return;
                case "corrupt-registration": Gateway.Corrupt=true; return;
                case "unsupported-registration-api": Gateway.Unsupported=true; return;
                case "unknown-context-field": Store.Values[ContextKey]=JsonSerializer.Serialize(Context,Json).TrimEnd('}')+",\"unknown\":true}"; return;
                case "context-origin": Store.Values[ContextKey]=JsonSerializer.Serialize(Context with {ServiceOrigin="https://other.example"},Json); return;
                case "empty-remote": Store.Values[ContextKey]=JsonSerializer.Serialize(Context with {RemoteConnectionId=Guid.Empty},Json); return;
                case "empty-session": Store.Values[ContextKey]=JsonSerializer.Serialize(Context with {RemoteSessionId=Guid.Empty},Json); return;
                case "registered-tenant": Gateway.Binding=Gateway.Binding with {TenantId=Guid.NewGuid().ToString("D")}; return;
                case "registered-origin": Gateway.Binding=Gateway.Binding with {ServiceOrigin="https://other.example"}; return;
                case "unpaired": Gateway.Binding=Gateway.Binding with {DeploymentId=Guid.Empty}; return;
                case "mode-disabled": Gateway.Enabled=false; return;
                case "remote-revoked": Gateway.Status="Revoked"; return;
                case "remote-wrong-id": Gateway.WrongId=true; return;
                case "remote-zero-revision": Gateway.Revision=0; return;
                case "remote-scope": Gateway.ScopeSets=[]; return;
                case "profile-id": Gateway.Profile=Gateway.Profile with {ProfileId="other.profile"}; return;
                case "profile-binding": Gateway.Profile=Gateway.Profile with {BindingHash=new string('c',64)}; return;
                case "profile-revision": Gateway.Profile=Gateway.Profile with {PublicRevision=0}; return;
                case "deployment-id": Gateway.Binding=Gateway.Binding with {DeploymentId=Guid.NewGuid()}; return;
                case "deployment-revision": Gateway.Binding=Gateway.Binding with {Revision=2}; return;
                case "profile-public-revision": Gateway.Profile=Gateway.Profile with {PublicRevision=2}; return;
                case "remote-revision": Gateway.Revision++; return;
                case "remote-session": Store.Values[ContextKey]=JsonSerializer.Serialize(Context with {RemoteSessionId=Guid.NewGuid()},Json); return;
            }
            await Base.Change(async db =>
            {
                switch(change)
                {
                    case "missing-consent": db.PluginAccountSetupSessions.Remove(await db.PluginAccountSetupSessions.SingleAsync()); break;
                    case "consent-account": (await db.PluginAccountSetupSessions.SingleAsync()).AccountGroupId=Guid.NewGuid(); break;
                    case "consent-status": (await db.PluginAccountSetupSessions.SingleAsync()).Status="ImportFailed"; break;
                    case "consent-profile": (await db.PluginAccountSetupSessions.SingleAsync()).ProfileHash=new string('c',64); break;
                    case "consent-package": (await db.PluginAccountSetupSessions.SingleAsync()).PackageDigest=new string('c',64); break;
                    case "consent-revision": (await db.PluginAccountSetupSessions.SingleAsync()).Revision++; break;
                    case "business-revoked": (await db.PluginAccountBusinessGrants.SingleAsync()).RevokedAt=DateTimeOffset.UtcNow; break;
                    case "business-foreign": (await db.PluginAccountBusinessGrants.SingleAsync()).OrganizationId=Guid.NewGuid(); break;
                    case "binding-revoked": (await db.AgentCapabilityBindings.SingleAsync()).RevokedAt=DateTimeOffset.UtcNow; break;
                    case "binding-digest": (await db.AgentCapabilityBindings.SingleAsync()).ProviderPackageDigest=new string('c',64); break;
                    case "resource-stale": (await db.PluginAccountResources.SingleAsync()).VerifiedAt=DateTimeOffset.UtcNow.AddDays(-31); break;
                    case "resource-proof": (await db.PluginAccountResourceAuthorizations.SingleAsync()).IsAvailable=false; break;
                    case "provider-grant": (await db.AgentInstallationGrants.SingleAsync(x=>x.AgentInstallationId==Connector)).ProvidedCapabilitiesJson="[]"; break;
                    case "local-scopes": (await db.PluginConnections.SingleAsync()).GrantedScopesJson="[]"; break;
                    case "unproven-subject": (await db.PluginAccountGroups.SingleAsync()).VerifiedSubject="foreign-subject"; break;
                    default: throw new ArgumentException("Unknown synthetic mutation.");
                }
            });
        }
        public async Task Adopt(string? change=null)
        {
            var source=Guid.NewGuid();var id=Guid.NewGuid();AdoptionId=id;
            var sourceDigest=new string('d',64);
            var request=new ConnectionAdoptionRequest(id,source,Connector,sourceDigest,Base.Native.Legacy.Connector.PackageVersion!.PackageDigest!,
                1,Gateway.Profile.BindingHash,Base.Actor.ToString("D"),Context.RemoteSessionId);
            if(change=="source-digest")request=request with {SourcePackageDigest=new string('c',64)};
            if(change=="target-digest")request=request with {TargetPackageDigest=new string('c',64)};
            if(change=="actor")request=request with {ActorId=Guid.NewGuid().ToString("D")};
            if(change=="remote-session")request=request with {OriginalConsentSessionId=Guid.NewGuid()};
            await Base.Change(async db=>
            {
                var target=await db.AgentInstallations.SingleAsync(x=>x.Id==Connector);target.SupersedesInstallationId=source;
                var original=await db.PluginAccountSetupSessions.SingleAsync();original.PluginInstallationId=source;original.PackageDigest=sourceDigest;
                db.AgentInstallations.Add(new(){Id=source,Scope=PluginInstallationScope.System,BusinessId="enterprise",IsEnabled=false,RevisionStatus=PluginRevisionStatus.Retired,
                    PackageVersion=new(){Id=Guid.NewGuid(),PackageDigest=sourceDigest,ManifestJson=Base.Native.Legacy.Connector.PackageVersion!.ManifestJson,PluginKind=PluginKind.Connector}});
                db.PluginAccountUpgrades.Add(new(){Id=id,SourceInstallationId=source,TargetInstallationId=change=="target"?Guid.NewGuid():Connector,
                    AccountGroupId=change=="account"?Guid.NewGuid():Base.Native.Group.Id,ConnectionId=Connection,OriginalSetupSessionId=Consent,ApplicationUserId=Base.Actor,
                    SourceSnapshotHash=new string('a',64),ServiceOrigin=change=="origin"?"https://other.example":Gateway.Binding.ServiceOrigin,RemoteConnectionId=Gateway.Remote,
                    ServiceRequestJson=JsonSerializer.Serialize(request,Json),ServiceReviewHash=new string('c',64),CompatibilityHash=new string('e',64)});
            });
            if(change!="pending")await Base.Change(async db=>{var row=await db.PluginAccountUpgrades.SingleAsync();row.Status="Completed";row.Revision++;row.CompletedAt=DateTimeOffset.UtcNow;});
            if(change=="ambiguous")
            {
                var another=Guid.NewGuid();
                await Base.Change(async db=>{var row=await db.PluginAccountUpgrades.AsNoTracking().SingleAsync();row.Id=another;row.Status="Pending";row.Revision=1;row.CompletedAt=null;db.PluginAccountUpgrades.Add(row);});
                await Base.Change(async db=>{var row=await db.PluginAccountUpgrades.SingleAsync(x=>x.Id==another);row.Status="Completed";row.Revision++;row.CompletedAt=DateTimeOffset.UtcNow;});
            }
            Gateway.Revision=2;
            Gateway.AdoptionReceipt=new(id,Gateway.Remote,CompatibleConnectionContract.Hash(JsonSerializer.SerializeToElement(request,Json)),new string('c',64),new string('e',64),
                "Adopted",2,"Ready",2,Connector,Base.Native.Legacy.Connector.PackageVersion!.PackageDigest!);
            if(change=="missing-remote-receipt")Gateway.AdoptionReceipt=null;
            if(change=="receipt-hash")Gateway.AdoptionReceipt=Gateway.AdoptionReceipt! with {RequestHash=new string('f',64)};
            if(change=="receipt-target")Gateway.AdoptionReceipt=Gateway.AdoptionReceipt! with {CurrentInstallationId=Guid.NewGuid()};
            if(change=="receipt-revoked")Gateway.AdoptionReceipt=Gateway.AdoptionReceipt! with {CurrentStatus="Revoked"};
        }
        public static async Task<Fixture> Create(bool keepLocalApproval=false)
        {
            var f=new Fixture{Base=await OperatorOnboardingRecoveryTests.Fixture.Create()};
            f.Gateway=new(f.Base.Org);f.Context=new(Guid.NewGuid(),f.Gateway.Remote,DeploymentProofCodec.Random(),f.Gateway.Binding.ServiceOrigin);
            f.Consent=Guid.NewGuid();
            await f.Base.Change(async db =>
            {
                if(!keepLocalApproval) db.ConnectorProfileApprovals.RemoveRange(await db.ConnectorProfileApprovals.ToArrayAsync());
                var connector=await db.AgentInstallations.Include(x=>x.PackageVersion).SingleAsync(x=>x.Id==f.Connector);
                var manifest=JsonSerializer.Deserialize<PluginManifest>(connector.PackageVersion!.ManifestJson,Json)!;
                var empty=JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{},"additionalProperties":false}""");
                var identity=manifest.ProviderOperations[1] with {Capability="example.api.identity.v1",InputSchema=empty,
                    Http=manifest.ProviderOperations[1].Http! with {Endpoint="https://api.example.com/identity"}};
                manifest=manifest with {ProviderOperations=[..manifest.ProviderOperations,identity],
                    Provides=[..manifest.Provides,manifest.Provides[1] with {Name=identity.Capability,InputSchema=empty}],
                    Accounts=manifest.Accounts! with {Identity=new(){Capability=identity.Capability,SubjectPointer="/sub",NamePointer="/name"}}};
                connector.PackageVersion.ManifestJson=JsonSerializer.Serialize(manifest,Json);
                var connection=await db.PluginConnections.SingleAsync();connection.CredentialProfileHash=f.Gateway.Profile.BindingHash;connection.ExternalAccountId="synthetic-subject";
                var group=await db.PluginAccountGroups.SingleAsync();group.IdentityKind=PluginAccountIdentityKind.ProviderSubject;
                group.VerifiedSubject="synthetic-subject";group.IdentityAuthority=identity.Http!.Endpoint;
                db.PluginAccountSetupSessions.Add(new(){Id=f.Consent,PluginInstallationId=f.Connector,ApplicationUserId=f.Base.Actor,AccountGroupId=group.Id,
                    ConnectionId=f.Connection,PackageDigest=connector.PackageVersion.PackageDigest!,ProfileHash=f.Gateway.Profile.BindingHash,PolicyVersion="v1",
                    ScopeSetIdsJson="[\"base\"]",RedirectUri=f.Gateway.ReturnUri,Status="Ready",CreatedAt=DateTimeOffset.UtcNow.AddHours(-2),
                    ExpiresAt=DateTimeOffset.UtcNow.AddHours(-1),ConsumedAt=DateTimeOffset.UtcNow.AddHours(-2),PolicyAcceptedAt=DateTimeOffset.UtcNow.AddHours(-2)});
            });
            f.Store.Values[f.ContextKey]=JsonSerializer.Serialize(f.Context,Json);
            f.Base.Credentials=new(f.Db,f.Store,f.Gateway);return f;
        }
        public ValueTask DisposeAsync()=>Base.DisposeAsync();
    }

    internal sealed class Store : IPluginSecretStore
    {
        public Dictionary<string,string> Values=[];public List<string> ReadKeys=[];public bool Corrupt;
        public Task<string?> GetAsync(Guid installation,string key,CancellationToken ct=default){ReadKeys.Add(key);if(Corrupt)throw new System.Security.Cryptography.CryptographicException("Synthetic protected-content corruption");return Task.FromResult(Values.GetValueOrDefault(key));}
        public Task SetAsync(Guid installation,string key,string value,CancellationToken ct=default){Values[key]=value;return Task.CompletedTask;}
        public Task RemoveAsync(Guid installation,string key,CancellationToken ct=default){Values.Remove(key);return Task.CompletedTask;}
    }
    internal sealed class ForbiddenTransport : IConnectorHttpTransport
    {
        public int Calls;
        public Task<ConnectorProviderResponse> SendAsync(Guid connector,Guid connection,ConnectorPreparedRequest request,Func<CancellationToken,Task> revalidate,CancellationToken ct)
        {Calls++;throw new InvalidOperationException("Direct provider transport is forbidden in the managed test.");}
    }
    internal sealed class Gateway(Guid organization) : IManagedPluginConnectionGateway
    {
        public ManagedDeploymentAuthority Binding=new(Guid.NewGuid(),organization.ToString("D"),1,"https://service.example");
        public ProfileAvailability Profile=new("example.profile",1,new string('b',64),"https://service.example/callback");
        public readonly Guid Remote=Guid.NewGuid();public string Status="Ready";public long Revision=1;public bool Enabled=true,WrongId,AllowReads=true,Corrupt,Unsupported;
        public int AdoptionReads;public ConnectionAdoptionReceipt? AdoptionReceipt;
        public JsonElement Result=JsonSerializer.SerializeToElement(new{data="synthetic-channel"});public ManagedReadRequest? LastRead;
        public IReadOnlyList<string> ScopeSets=["base"];public int Reads,ProviderEffects;public Action? AfterRead;
        public bool UsesManaged(string profile)=>Enabled;public string ReturnUri=>"https://app.example/managed-return";
        public Task<ManagedDeploymentAuthority> BoundAuthority(CancellationToken ct)
        {if(Corrupt)throw new System.Security.Cryptography.CryptographicException("Synthetic protected-registration corruption");if(Unsupported)throw new NotSupportedException();return Task.FromResult(Binding);}
        public Task<ConnectionAdoptionStatus> Adoption(Guid id,string origin,CancellationToken ct){AdoptionReads++;return Task.FromResult(new ConnectionAdoptionStatus(AdoptionReceipt is not null,AdoptionReceipt));}
        public Task<string> BoundServiceOrigin(CancellationToken ct)=>Task.FromResult(Binding.ServiceOrigin);
        public Task<ProfileAvailability> Availability(string digest,CancellationToken ct)=>Task.FromResult(Profile);
        public Task<ConnectionSnapshot> Snapshot(Guid id,CancellationToken ct,string? expectedServiceOrigin=null)
        {Assert.Equal(Binding.ServiceOrigin,expectedServiceOrigin);return Task.FromResult(new ConnectionSnapshot(WrongId?Guid.NewGuid():id,Status,Revision,ScopeSets));}
        public Task<ManagedReadResult> ReadOperation(Guid connection,ManagedReadRequest request,string origin,CancellationToken ct)
        {
            Reads++;LastRead=request;Assert.Equal(Remote,connection);Assert.Equal(Binding.ServiceOrigin,origin);Assert.Equal(Binding.TenantId,request.TenantId);
            if(!AllowReads)throw new HttpRequestException("Synthetic service policy denial",null,System.Net.HttpStatusCode.Forbidden);
            ProviderEffects++;AfterRead?.Invoke();return Task.FromResult(new ManagedReadResult(connection,Revision,ManagedExecutionBinding.Hash(connection,request),Result));
        }
        public Task<ConnectionAttempt> Begin(BeginConnection r,CancellationToken ct,string? expectedServiceOrigin=null)=>throw new NotSupportedException();
        public Task<ConnectionReceipt> Cancel(Guid id,CancellationToken ct,string? expectedServiceOrigin=null)=>throw new NotSupportedException();
        public Task<ConnectionReceipt> CancelAttempt(Guid id,CancellationToken ct,string? expectedServiceOrigin=null)=>throw new NotSupportedException();
        public Task<ConnectionSnapshot> Supersede(Guid previous,Guid replacement,CancellationToken ct,string? expectedServiceOrigin=null)=>throw new NotSupportedException();
        public Task<ConnectionReceipt> Redeem(RedeemConnection request,CancellationToken ct,string? expectedServiceOrigin=null)=>throw new NotSupportedException();
        public Task<ConnectionReceipt> Recover(Guid session,RecoverConnection request,CancellationToken ct,string? expectedServiceOrigin=null)=>throw new NotSupportedException();
        public Task<ConnectionInventory> Discover(Guid connection,CancellationToken ct,string? expectedServiceOrigin=null)=>throw new NotSupportedException();
        public Task<ConnectionSnapshot> Disconnect(Guid connection,CancellationToken ct,string? expectedServiceOrigin=null)=>throw new NotSupportedException();
    }

    // Every agent session also lists its own implicit self-read tools (posture, harness status);
    // these tests are about connector authority, so count connector tools only.
    private static async Task<McpToolDescriptor[]> ConnectorTools(Task<IReadOnlyList<McpToolDescriptor>> listing) =>
        (await listing).Where(tool => !McpToolCatalog.SelfReadCapabilities.Contains(tool.Capability)).ToArray();
}
