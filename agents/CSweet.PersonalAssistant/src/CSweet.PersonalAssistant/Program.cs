using CSweet.Agent.SDK;
using CSweet.PersonalAssistant;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

if (args.Contains("--self-test", StringComparer.Ordinal))
{
    var result = await new AgentTestRuntime().ExecuteCapabilityAsync(
        new PersonalAssistantAgent(new PersonalAssistantReasoner()),
        "unsupported.v1",
        new { });
    Console.WriteLine(result.Value);
    Environment.ExitCode = !result.Succeeded ? 0 : 1;
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<IPersonalAssistantReasoner, PersonalAssistantReasoner>();
builder.AddCSweetAgent<PersonalAssistantAgent>();
await builder.Build().RunAsync();
