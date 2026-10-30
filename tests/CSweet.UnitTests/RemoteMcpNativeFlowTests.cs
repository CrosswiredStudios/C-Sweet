using System.Net;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Auth;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.RemoteMcp;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
namespace CSweet.UnitTests;
public sealed class RemoteMcpNativeFlowTests
{
    private const string Search = "realtyleads.inheritance.search.v1";
    private const string Lookup = "realtyleads.inheritance.lookup.v1";
    [Fact] public async Task ReviewedPluginBuildAndInstallNeverCreateAWorkload()
    {
        await using var f = await Fixture.Create();
        var package = f.Provider.PackageVersion!;
        package.PackageSource = new() { Id = Guid.NewGuid(), RepositoryUrl = "https://github.com/CrosswiredStudios/CSweet.Plugin.RealtyLeadsAI" };
        package.PackageSourceId = package.PackageSource.Id;
        f.Db.AgentPackageSources.Add(package.PackageSource);
        package.CommitSha = new string('c', 40);
        package.ManifestDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(package.ManifestJson))).ToLowerInvariant();
        var manifest = JsonSerializer.Deserialize<PluginManifest>(package.ManifestJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        package.CapabilityDescriptorsDigest = AgentImportPreviewService.CapabilityDescriptorsDigest(manifest);
        Assert.Equal(64, AgentBuildService.VerifyConnectorDeclarations(package).Length);
        f.Provider.RevisionStatus = PluginRevisionStatus.Retired;
        await f.Db.SaveChangesAsync();
        var installed = await AgentInstallationServiceTests.CreatePluginInstallerForAcceptance(f.Db).InstallAsync(package.Id,
            new(f.Business.ToString("D"), "OnDemand", 0, "Skip", [Search, Lookup], [], [], [], [], 0, 0, 0));
        Assert.True(installed.HasNativeApiKeyAccounts); Assert.Equal(0, installed.MemoryMb);
        Assert.Empty(await f.Db.AgentRuntimeInstances.ToArrayAsync());
        Assert.Null((await f.Db.AgentInstallations.Include(x => x.Schedule).SingleAsync(x => x.Id == installed.Id)).Schedule);
    }
    [Fact] public async Task SecondAuthorizedManagerCannotReadOrGrantAnothersConnection()
    {
        await using var f = await Fixture.Create(); var connection = await f.Connect(); var other = Guid.NewGuid();
        f.Db.Users.Add(new ApplicationUser { Id = other, UserName = "second-manager" });
        f.Db.CoreOrganizationUsers.Add(new() { Id = Guid.NewGuid(), ApplicationUserId = other, OrganizationId = f.Business,
            EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Manager, IsActive = true });
        await f.Db.SaveChangesAsync();
        Assert.Empty((await f.Service.GetAsync(other, f.Business, f.Provider.Id, default)).Connections);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.GrantAsync(other, f.Business, f.Provider.Id, connection.Id,
            new(f.Consumer.Id, connection.Generation, "approved-digest"), default));
    }
    [Fact] public async Task NativeConnectGrantSdkReadRevoke()
    {
        await using var f = await Fixture.Create();
        var view = await f.Service.GetAsync(f.Owner, f.Business, f.Provider.Id, default);
        Assert.Empty(view.Connections); Assert.Single(view.Consumers);
        var connection = await f.Connect();
        Assert.False(await f.Service.IsAvailableAsync(f.Business, f.Consumer.Id, Search, default));
        Assert.DoesNotContain(Fixture.Key, JsonSerializer.Serialize(await f.Service.GetAsync(f.Owner, f.Business, f.Provider.Id, default)));
        await f.Grant(connection);
        var runtime = new AgentTestRuntime().RegisterCapability<object, JsonElement>(Search,
            (_, ct) => f.Service.ReadAsync(f.Business, f.Consumer.Id, Search, JsonSerializer.SerializeToElement(new { state = "TX" }), ct, (vault, profile) => new(vault, profile, () => new FakeMcp())));
        var result = await runtime.CreateContext().Platform.InvokeAsync<object, JsonElement>(Search, new { });
        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        var session = new McpAgentSession { Id = Guid.NewGuid(), AgentInstallationId = f.Consumer.Id, OrganizationId = f.Business.ToString("D") };
        var queued = new AgentWorkItem { Id = Guid.NewGuid(), AgentInstallationId = f.Consumer.Id, OrganizationId = f.Business.ToString("D"), Kind = CSweet.Domain.Setup.AgentWorkKind.Capability, Name = "realtyleads.research.search.v1" };
        f.Db.McpAgentSessions.Add(session); f.Db.AgentWorkItems.Add(queued); await f.Db.SaveChangesAsync();
        var current = (await f.Service.GetAsync(f.Owner, f.Business, f.Provider.Id, default)).Connections.Single();
        await f.Service.RevokeAsync(f.Owner, f.Business, f.Provider.Id, connection.Id, current.Generation, default);
        Assert.False(await f.Service.IsAvailableAsync(f.Business, f.Consumer.Id, Search, default));
        Assert.NotNull(session.RevokedAt); Assert.Equal(AgentWorkStatus.Cancelled, queued.Status);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ReadAsync(f.Business, f.Consumer.Id, Search,
            JsonSerializer.SerializeToElement(new { }), default, (vault, profile) => new(vault, profile, () => throw new Exception("Must not send after revoke"))));
    }
    [Theory] [InlineData("owner")] [InlineData("business")] [InlineData("consumer-business")] [InlineData("consumer-grant")] [InlineData("provider-digest")] [InlineData("membership")]
    public async Task IndependentAuthorityCannotBeSubstituted(string mutation)
    {
        await using var f = await Fixture.Create(); var connection = await f.Connect();
        if (mutation == "owner")
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.GrantAsync(Guid.NewGuid(), f.Business, f.Provider.Id, connection.Id,
                new(f.Consumer.Id, connection.Generation, "approved-digest"), default)); return;
        }
        if (mutation == "business")
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.GrantAsync(f.Owner, Guid.NewGuid(), f.Provider.Id, connection.Id,
                new(f.Consumer.Id, connection.Generation, "approved-digest"), default)); return;
        }
        await f.Grant(connection);
        switch (mutation)
        {
            case "consumer-business": f.Consumer.BusinessId = Guid.NewGuid().ToString("D"); break;
            case "consumer-grant": f.Consumer.Grant!.GrantRevision++; break;
            case "provider-digest": f.Provider.PackageVersion!.PackageDigest = "changed"; break;
            case "membership": f.Member.IsActive = false; break;
        }
        await f.Db.SaveChangesAsync();
        Assert.False(await f.Service.IsAvailableAsync(f.Business, f.Consumer.Id, Search, default));
    }
    [Fact] public async Task MembershipLossDuringProviderCallWithholdsResult()
    {
        await using var f = await Fixture.Create(); var connection = await f.Connect(); await f.Grant(connection);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ReadAsync(f.Business, f.Consumer.Id, Search,
            JsonSerializer.SerializeToElement(new { }), default, (vault, profile) => new(vault, profile, () => new FakeMcp(async () =>
            { f.Member.IsActive = false; await f.Db.SaveChangesAsync(); }))));
    }
    [Fact] public async Task RotationClearsBindingsAndRequiresExplicitRegrant()
    {
        await using var f = await Fixture.Create(); var connection = await f.Connect(); await f.Grant(connection);
        var current = (await f.Service.GetAsync(f.Owner, f.Business, f.Provider.Id, default)).Connections.Single();
        await f.Service.RotateAsync(f.Owner, f.Business, f.Provider.Id, connection.Id, new() { Key = Fixture.Key + "new", ExpectedGeneration = current.Generation }, default);
        Assert.False(await f.Service.IsAvailableAsync(f.Business, f.Consumer.Id, Search, default));
        Assert.Null((await f.Service.GetAsync(f.Owner, f.Business, f.Provider.Id, default)).Connections.Single().ConsumerInstallationId);
    }
    [Fact] public async Task NativeGrantAppearsInAuthenticatedCatalogWithoutKeyOrAccountSelectors()
    {
        await using var f = await Fixture.Create(); await f.Grant(await f.Connect());
        var session = new AgentSession("session", "researcher", f.Consumer.Id.ToString(), f.Business.ToString(), "runtime", "tick",
            new(new HashSet<string>(), new HashSet<string>(), new HashSet<string> { Search, Lookup }, 1));
        var catalog = new McpToolCatalog([], remoteMcp: f.Service);
        var tool = await catalog.FindAsync("realtyleads_inheritance_search", session, f.Db, default);
        Assert.NotNull(tool); Assert.Equal(McpToolExecutionPolicy.ReadOnly, tool.ExecutionPolicy);
        Assert.DoesNotContain("connection", tool.InputSchema.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }
    internal sealed class Fixture : IAsyncDisposable
    {
        public const string Key = "synthetic-native-account-key-0001";
        public required CSweetDbContext Db; public required RemoteMcpAccountService Service;
        public Guid Owner = Guid.NewGuid(), Business = Guid.NewGuid();
        public AgentInstallation Provider = null!, Consumer = null!; public OrganizationUser Member = null!;
        public static async Task<Fixture> Create()
        {
            var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            var f = new Fixture { Db = db, Service = new(db, new EphemeralDataProtectionProvider()) };
            db.Users.Add(new ApplicationUser { Id = f.Owner, UserName = "fixture-owner" });
            f.Member = new() { Id = Guid.NewGuid(), ApplicationUserId = f.Owner, OrganizationId = f.Business, EmployeeType = EmployeeType.Human,
                PermissionLevel = OrganizationPermissionLevel.Manager, IsActive = true }; db.CoreOrganizationUsers.Add(f.Member);
            AgentInstallation Installation(string filename, PluginKind kind)
            {
                var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", filename));
                var m = JsonSerializer.Deserialize<PluginManifest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                var package = new AgentPackageVersion { Id = Guid.NewGuid(), PluginKind = kind, AgentId = m.Id, AgentName = m.Name,
                    PublisherId = m.Publisher.Id, PublisherName = m.Publisher.Name, Version = m.Version, ManifestJson = json, PackageDigest = "approved-digest", Status = AgentPackageVersionStatus.Built };
                var installation = new AgentInstallation { Id = Guid.NewGuid(), BusinessId = f.Business.ToString("D"), PackageVersion = package,
                    PackageVersionId = package.Id, Scope = PluginInstallationScope.Organization, IsEnabled = true, SetupState = PluginSetupState.Ready };
                installation.Grant = new() { Id = Guid.NewGuid(), AgentInstallationId = installation.Id, GrantRevision = 1,
                    ProvidedCapabilitiesJson = JsonSerializer.Serialize(m.Provides.Select(x => x.Name)), RequiredCapabilitiesJson = JsonSerializer.Serialize(m.Requires.Select(x => x.Name)) };
                db.AgentInstallations.Add(installation); return installation;
            }
            f.Provider = Installation("realtyleads-plugin.json", PluginKind.Connector); f.Consumer = Installation("realtyleads-agent.json", PluginKind.Agent);
            await db.SaveChangesAsync(); return f;
        }
        public Task<RemoteMcpConnectionView> Connect() => Service.ConnectAsync(Owner, Business, Provider.Id, new() { Key = Key }, default);
        public Task Grant(RemoteMcpConnectionView c) => Service.GrantAsync(Owner, Business, Provider.Id, c.Id, new(Consumer.Id, c.Generation, "approved-digest"), default);
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
    internal sealed class FakeMcp(Func<Task>? onCall = null) : HttpMessageHandler
    {
        // Mirrors the reviewed manifest's own capability-to-tool mapping, as a real provider listing would.
        private static string RemoteTool(JsonElement manifest, string capability) =>
            manifest.GetProperty("reviewedRemoteMcp").GetProperty("tools").EnumerateArray()
                .Single(t => t.GetProperty("capability").GetString() == capability).GetProperty("remoteTool").GetString()!;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https://realtyleads.ai/mcp", request.RequestUri!.OriginalString); Assert.Equal(Fixture.Key, request.Headers.GetValues("X-Api-Key").Single());
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var method = document.RootElement.GetProperty("method").GetString();
            if (method == "notifications/initialized") return new(HttpStatusCode.Accepted);
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "realtyleads-plugin.json")));
            object result = method switch
            {
                "initialize" => new { protocolVersion = "2025-03-26" },
                "tools/list" => new { tools = manifest.RootElement.GetProperty("provides").EnumerateArray().Select(p => new
                    { name = RemoteTool(manifest.RootElement, p.GetProperty("name").GetString()!), inputSchema = p.GetProperty("inputSchema").Clone() }).ToArray() },
                _ => new { content = new[] { new { type = "text", text = "[]" } } }
            };
            if (method == "tools/call" && onCall is not null) await onCall();
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = document.RootElement.GetProperty("id").GetInt32(), result }), Encoding.UTF8, "application/json") };
        }
    }
}
