using System.Text;
using System.Text.Json;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class AppHostManagedConnectionConfigurationTests
{
    private const string Root = "CSweet:PluginConnections:Managed:";
    private static Dictionary<string, string?> Complete() => new()
    {
        [Root + "Enabled"] = "true", [Root + "ServiceOrigin"] = "https://connections.example.test",
        [Root + "ReturnUri"] = "https://localhost:7125/settings/plugins/managed-return",
        [Root + "RegistrationPath"] = Path.GetFullPath("synthetic-existing-deployment.protected.json")
    };
    private static IConfiguration Config(Dictionary<string, string?> input) => new ConfigurationBuilder().AddInMemoryCollection(input).Build();
    private static IConfiguration Child(IReadOnlyDictionary<string, string> shared, Dictionary<string, string?>? defaults = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(defaults ?? [])
            .AddInMemoryCollection(shared.Select(x => KeyValuePair.Create<string, string?>(x.Key.Replace("__", ":"), x.Value))).Build();
    private static void Check(IConfiguration child)
    {
        var value = child.GetSection(ManagedConnectionHostConfiguration.Section).Get<ManagedPluginConnectionOptions>()!;
        ManagedConnectionHostConfiguration.RequireMatch(child[ManagedConnectionHostConfiguration.HashKey], value.Enabled,
            value.ServiceOrigin, value.ReturnUri, value.RegistrationPath, value.CustomerOwnedProfiles);
    }

    [Fact]
    public void ApiAndWorkerReceiveSameExistingDeploymentOptionsWithoutCopyingOtherAuthority()
    {
        var input = Complete();
        input["CSweet:ConnectionServiceOwner:PrivateKey"] = "sensitive-synthetic-owner-key";
        input["CSweet:PluginConnections:Providers:test:ClientSecret"] = "sensitive-synthetic-provider-secret";
        var shared = ManagedConnectionHostConfiguration.ReadEnvironment(Config(input));
        var api = Child(shared, new() { [Root + "Enabled"] = "false", [Root + "RegistrationPath"] = "old-default" });
        var worker = Child(shared);
        Check(api); Check(worker);
        foreach (var key in Complete().Keys) Assert.Equal(api[key], worker[key]);
        Assert.Equal(5, shared.Count); // Four client fields and a content-free consistency hash.
        Assert.DoesNotContain(shared.Values, x => x.Contains("sensitive-synthetic", StringComparison.Ordinal));
        Assert.All(shared.Keys, x => Assert.StartsWith("CSweet__PluginConnections__Managed", x));
        Assert.False(File.Exists(input[Root + "RegistrationPath"])); // Reading/wiring never prepares a key or opens a registration.
    }

    [Fact]
    public void ResolvedAppHostOverridesRetainConfigurationProviderPrecedence()
    {
        using var json = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"CSweet":{"PluginConnections":{"Managed":{"Enabled":false,"ServiceOrigin":"https://old.example.test","ReturnUri":"https://localhost:7000/settings/plugins/managed-return","RegistrationPath":"old-relative-default"}}}}
            """));
        var config = new ConfigurationBuilder().AddJsonStream(json).AddInMemoryCollection(Complete()).Build();
        var shared = ManagedConnectionHostConfiguration.ReadEnvironment(config);
        Check(Child(shared));
        Assert.Equal("https://connections.example.test", shared["CSweet__PluginConnections__Managed__ServiceOrigin"]);
    }

    [Fact]
    public async Task BothGatewaysCanReadSameSyntheticExistingRegistrationWithoutPreparingOrContactingService()
    {
        var path = Path.Combine(Path.GetTempPath(), "csweet-host-parity-" + Guid.NewGuid().ToString("N") + ".dat");
        var input = Complete(); input[Root + "RegistrationPath"] = path;
        var shared = ManagedConnectionHostConfiguration.ReadEnvironment(Config(input));
        var protection = new EphemeralDataProtectionProvider();
        var stored = protection.CreateProtector("CSweet.ManagedConnection.Deployment.v1").Protect(JsonSerializer.Serialize(
            new ManagedDeploymentRegistration(Guid.NewGuid(), "synthetic-tenant", 1, "synthetic-key-id", "inert-not-a-private-key", input[Root + "ServiceOrigin"])));
        await File.WriteAllTextAsync(path, stored);
        var http = new NoNetwork();
        try
        {
            foreach (var child in new[] { Child(shared, new() { [Root + "RegistrationPath"] = "old-default" }), Child(shared) })
            {
                Check(child);
                var settings = child.GetSection("CSweet:PluginConnections").Get<PluginConnectionOptions>()!;
                var gateway = new ManagedPluginConnectionGateway(http, protection, Options.Create(settings));
                Assert.Equal(input[Root + "ServiceOrigin"], await gateway.BoundServiceOrigin(default));
                Assert.True((await gateway.Status(default)).Paired);
            }
            Assert.Equal(stored, await File.ReadAllTextAsync(path));
            Assert.Equal(0, http.Calls);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("Enabled")]
    [InlineData("ServiceOrigin")]
    [InlineData("ReturnUri")]
    [InlineData("RegistrationPath")]
    public void MissingSharedFieldFailsClosedWithoutIncludingValues(string field)
    {
        var input = Complete(); input.Remove(Root + field);
        var error = Assert.Throws<InvalidOperationException>(() => ManagedConnectionHostConfiguration.ReadEnvironment(Config(input)));
        Assert.Equal("managed_shared_configuration_invalid", error.Message);
        Assert.DoesNotContain("example.test", error.ToString());
    }

    [Theory]
    [InlineData("Enabled", "not-a-boolean")]
    [InlineData("ServiceOrigin", "https://user:synthetic-secret@connections.example.test")]
    [InlineData("ServiceOrigin", "http://connections.example.test")]
    [InlineData("ServiceOrigin", "https://connections.example.test/other")]
    [InlineData("ReturnUri", "https://localhost:7125/wrong-path")]
    [InlineData("ReturnUri", "https://localhost:7125/settings/plugins/managed-return?synthetic-secret")]
    [InlineData("ReturnUri", "http://public.example.test/settings/plugins/managed-return")]
    [InlineData("RegistrationPath", "relative-synthetic-secret")]
    [InlineData("ClientSecret", "synthetic-secret")]
    [InlineData("CustomerOwnedProfiles", "synthetic-secret")]
    [InlineData("CustomerOwnedProfiles:1", "gap")]
    public void InvalidOrUnexpectedSharedAuthorityFailsWithFixedSafeError(string key, string value)
    {
        var input = Complete(); input[Root + key] = value;
        var error = Assert.Throws<InvalidOperationException>(() => ManagedConnectionHostConfiguration.ReadEnvironment(Config(input)));
        Assert.Equal("managed_shared_configuration_invalid", error.Message);
        Assert.DoesNotContain("synthetic-secret", error.ToString());
    }

    [Theory]
    [InlineData("Enabled", "false")]
    [InlineData("ServiceOrigin", "https://other.example.test")]
    [InlineData("ReturnUri", "https://localhost:8000/settings/plugins/managed-return")]
    [InlineData("RegistrationPath", "another-path")]
    [InlineData("CustomerOwnedProfiles:0", "unexpected-profile")]
    public void ConflictingHigherPriorityChildOverrideIsRejected(string field, string value)
    {
        var shared = ManagedConnectionHostConfiguration.ReadEnvironment(Config(Complete()));
        var child = new ConfigurationBuilder().AddConfiguration(Child(shared))
            .AddInMemoryCollection(new Dictionary<string, string?> { [Root + field] = value }).Build();
        Assert.Equal("managed_shared_configuration_conflict", Assert.Throws<InvalidOperationException>(() => Check(child)).Message);
    }

    [Fact]
    public void CustomerOwnedProfileSelectionsAreSharedAndLeftoverArrayEntriesFailClosed()
    {
        var input = Complete(); input[Root + "CustomerOwnedProfiles:0"] = "customer-owned.test";
        var shared = ManagedConnectionHostConfiguration.ReadEnvironment(Config(input));
        Check(Child(shared));
        Assert.Equal("customer-owned.test", shared["CSweet__PluginConnections__Managed__CustomerOwnedProfiles__0"]);
        var stale = Child(shared, new() { [Root + "CustomerOwnedProfiles:1"] = "stale-other-backend" });
        Assert.Equal("managed_shared_configuration_conflict", Assert.Throws<InvalidOperationException>(() => Check(stale)).Message);
    }

    [Fact]
    public void AbsentSharedSectionPreservesStandaloneModeAndDisabledSharedModeKeepsCleanupConfiguration()
    {
        Assert.Empty(ManagedConnectionHostConfiguration.ReadEnvironment(Config([])));
        ManagedConnectionHostConfiguration.RequireMatch(null, false, "", "", "", []);
        var input = Complete(); input[Root + "Enabled"] = "false";
        var shared = ManagedConnectionHostConfiguration.ReadEnvironment(Config(input));
        Check(Child(shared)); Assert.Equal("false", shared["CSweet__PluginConnections__Managed__Enabled"]);
        Assert.Equal(input[Root + "RegistrationPath"], shared["CSweet__PluginConnections__Managed__RegistrationPath"]);
    }

    [Fact]
    public void AppHostWiresBothConsumersAndInfrastructureChecksFinalBoundOptions()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CSweet.slnx"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("Repository root unavailable.");
        var appHost = File.ReadAllText(Path.Combine(root, "src/CSweet.AppHost/Program.cs"));
        Assert.Contains("ManagedConnectionHostConfiguration.ReadEnvironment(builder.Configuration)", appHost);
        var loop = appHost[appHost.IndexOf("foreach (var setting in sharedManagedEnvironment)", StringComparison.Ordinal)..];
        loop = loop[..loop.IndexOf("\n}", StringComparison.Ordinal)];
        Assert.Contains("api.WithEnvironment(setting.Key, setting.Value)", loop);
        Assert.Contains("agentHost.WithEnvironment(setting.Key, setting.Value)", loop);
        Assert.DoesNotContain("workerHost.WithEnvironment", loop);
        var infrastructure = File.ReadAllText(Path.Combine(root, "src/CSweet.Infrastructure/DependencyInjection.cs"));
        Assert.Contains("PostConfigure<PluginConnectionOptions>(settings => ManagedConnectionHostConfiguration.RequireMatch(", infrastructure);
    }

    private sealed class NoNetwork : IHttpClientFactory
    {
        internal int Calls;
        public HttpClient CreateClient(string name) { Calls++; throw new InvalidOperationException("Synthetic test forbids network access."); }
    }
}
