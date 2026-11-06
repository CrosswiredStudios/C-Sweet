using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Contracts.Agents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CSweet.UnitTests;

public sealed class CodemodeBudgetRegressionTests
{
    [Fact]
    public async Task RepeatedReferencesCannotExpandBeyondTheIntermediateBudget()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["CSweet:Codemode:Enabled"] = "true" }).Build();
        var handler = new CodemodeCapabilityHandler(services, configuration);
        var script = "let v0 = \"seed\"\n" + string.Join("\n", Enumerable.Range(1, 24)
            .Select(i => $"let v{i} = [\"$v{i - 1}\",\"$v{i - 1}\"]")) + "\nemit $v24";
        var session = new AgentSession("session", "test", Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "runtime", "tick",
            new(new HashSet<string>(), new HashSet<string>(), new HashSet<string> { CodemodeCapabilities.Execute }, 1));
        var request = new RequestCapability { RequestId = "expand", Capability = CodemodeCapabilities.Execute,
            Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new { script })) };
        var results = new List<CapabilityResult>();
        await foreach (var result in handler.HandleAsync(session, request, default)) results.Add(result);
        var failure = Assert.Single(results);
        Assert.False(failure.Succeeded);
        Assert.Contains("intermediate value budget", failure.Payload.ToStringUtf8());
        Assert.True(failure.Payload.Length < 4096);
    }
}
