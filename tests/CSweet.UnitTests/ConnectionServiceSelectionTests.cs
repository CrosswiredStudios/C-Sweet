using System.Net;
using System.Net.Http.Json;
using CSweet.ConnectionService.Protocol;
using CSweet.Contracts.Plugins;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;
public sealed class ConnectionServiceSelectionTests
{
    private static CSweetDbContext Database() => new(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    [Fact]
    public async Task ChoiceSurvivesInterruptionAndSkipPreservesOrigin()
    {
        await using var db = Database(); await new SetupService(db).EnsureSeededAsync();
        var options = Options.Create(new PluginConnectionOptions()); var store = new ConnectionServiceSelectionStore(db, options);
        var actor = Guid.NewGuid();
        var first = await store.Save(actor, new("SelfHosted", "https://connections.example.test", 0), default);
        Assert.Equal(first, new ConnectionServiceSelectionStore(db, options).Current());
        var skipped = await store.Save(actor, new("Skip", null, first.Revision), default);
        Assert.Equal(first.Origin, skipped.Origin); Assert.False(store.Effective(options.Value.Managed).Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.Save(actor, new("Official", null, 0), default));
        var official = await store.Save(actor, new("Official", "https://attacker.test", skipped.Revision), default);
        Assert.Equal(ManagedServiceTrust.OfficialOrigin, official.Origin);
    }
    [Fact]
    public async Task ExistingRegistrationCannotBeSilentlyTransferred()
    {
        await using var db = Database(); await new SetupService(db).EnsureSeededAsync();
        var file = Path.GetTempFileName();
        try
        {
            var options = Options.Create(new PluginConnectionOptions { Managed = new() { Enabled = true, ServiceOrigin = "https://old.example.test", RegistrationPath = file } });
            var store = new ConnectionServiceSelectionStore(db, options);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.Save(Guid.NewGuid(), new("SelfHosted", "https://new.example.test", 0), default));
            await store.Save(Guid.NewGuid(), new("Skip", null, 0), default);
            Assert.True(File.Exists(file)); Assert.Equal("https://old.example.test", store.Current().Origin);
        }
        finally { File.Delete(file); }
    }
    [Theory]
    [InlineData("https://selected.example.test", "1.0", true, "OAuthSetupAvailable")]
    [InlineData("https://other.example.test", "1.0", true, "Unsupported")]
    [InlineData("https://selected.example.test", "future", true, "Unsupported")]
    public async Task ProbePinsOriginAndDoesNotEnableUnimplementedFeatures(string origin, string protocol, bool oauth, string state)
    {
        await using var db = Database();
        var options = Options.Create(new PluginConnectionOptions { Managed = new() { Enabled = true, ServiceOrigin = "https://selected.example.test" } });
        var factory = new ClientFactory(new ConnectionServiceCapabilities(origin, protocol, oauth, true, true));
        var result = await new ConnectionServiceSelectionStore(db, options, factory).Probe(default);
        Assert.Equal(state, result.State); Assert.False(result.MobilePairing); Assert.False(result.GeneralProviderActions);
    }
    [Fact]
    public async Task RelationalStaleWriteRollsBackItsAudit()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseSqlite(connection).Options;
        await using var first = new CSweetDbContext(options);
        // Use the actual mapped tables, without unrelated PostgreSQL-specific schema objects.
        var script = first.Database.GenerateCreateScript();
        foreach (var sql in script.Split(";", StringSplitOptions.RemoveEmptyEntries).Where(x => x.Contains("CREATE TABLE \"SystemConfigurations\"") || x.Contains("CREATE TABLE \"AuditEvents\"")))
            await first.Database.ExecuteSqlRawAsync(sql);
        var config = new CSweet.Domain.Setup.SystemConfiguration { Id = Guid.NewGuid(), IsFirstRunComplete = true };
        first.SystemConfigurations.Add(config); await first.SaveChangesAsync();
        Assert.True(first.Model.FindEntityType(typeof(CSweet.Domain.Setup.SystemConfiguration))!.FindProperty(nameof(config.ConnectionServiceSelectionJson))!.IsConcurrencyToken);
        await using var second = new CSweetDbContext(options);
        var stale = await second.SystemConfigurations.SingleAsync();
        config.ConnectionServiceSelectionJson = "winner"; await first.SaveChangesAsync();
        stale.ConnectionServiceSelectionJson = "loser";
        second.AuditEvents.Add(new CSweet.Domain.Setup.AuditEvent { Id = Guid.NewGuid(), Sequence = 42, EventType = "must-roll-back" });
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        Assert.Equal("winner", await first.SystemConfigurations.AsNoTracking().Select(x => x.ConnectionServiceSelectionJson).SingleAsync());
        Assert.False(await first.AuditEvents.AnyAsync(x => x.EventType == "must-roll-back"));
    }
    [Theory]
    [InlineData("127.0.0.1", false)] [InlineData("::1", false)] [InlineData("10.0.0.1", false)]
    [InlineData("169.254.169.254", false)] [InlineData("::ffff:192.168.0.1", false)]
    [InlineData("8.8.8.8", true)] [InlineData("2606:4700:4700::1111", true)]
    public void NetworkPolicyRejectsPrivateDestinationsUnlessExplicitlyOptedIn(string ip, bool allowed) => Assert.Equal(allowed, ConnectionServiceNetworkPolicy.IsPublic(System.Net.IPAddress.Parse(ip)));
    private sealed class ClientFactory(ConnectionServiceCapabilities caps) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(caps));
    }
    private sealed class Handler(ConnectionServiceCapabilities caps) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://selected.example.test/service-info", request.RequestUri!.AbsoluteUri);
            Assert.Empty(request.Headers);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(caps) });
        }
    }
}
