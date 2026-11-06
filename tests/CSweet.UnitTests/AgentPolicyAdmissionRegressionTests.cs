using CSweet.Application.Security;
using CSweet.Application.Agents;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Contracts.Agents;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Agents;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CSweet.UnitTests;

public sealed class AgentPolicyAdmissionRegressionTests
{
    [Fact]
    public async Task IndependentPolicyInstancesSerializeAdmissionAgainstTheRealAuditLedger()
    {
        var name = Guid.NewGuid().ToString();
        var collection = new ServiceCollection();
        collection.AddDbContext<CSweetDbContext>(options => options.UseInMemoryDatabase(name));
        using var services = collection.BuildServiceProvider();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var context = new AuditExecutionContextAccessor();
        var writer = new AuditEventWriter(scopes, context, new EphemeralDataProtectionProvider());
        var organization = Guid.NewGuid(); var installation = Guid.NewGuid();
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            db.AgentInstallations.Add(new AgentInstallation { Id = installation, BusinessId = organization.ToString("D") });
            await db.SaveChangesAsync();
        }
        var threshold = new AgentPolicyThreshold("Outward", TimeSpan.FromHours(1), 3);
        var candidate = new AgentPolicyEvent("Outward", installation, organization.ToString("D"), CapabilityRiskClass.Outward, DateTimeOffset.UtcNow);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            new AgentPolicyService(scopes, writer, context, TimeProvider.System).TryAdmitAsync(threshold, candidate)));
        Assert.Equal(3, results.Count(x => x.Allowed));
        var reader = new AgentPolicyService(scopes, writer, context, TimeProvider.System);
        Assert.Equal(3, await reader.CountWithinAsync(new("Outward", installation, organization.ToString("D"), TimeSpan.FromHours(1))));
        Assert.False((await reader.TryAdmitAsync(threshold, candidate with { BusinessId = Guid.NewGuid().ToString("D") })).Allowed);

        // Prove the production dispatcher consumes admission before a destructive handler runs.
        var posture = new AgentPostureService(new InMemoryAgentPostureStore(), new PermissivePostureAuthorization(), TimeProvider.System);
        await posture.SetAsync(installation, AgentPosture.Auto, Guid.NewGuid());
        var probe = new Probe();
        var dispatcher = new PlatformCapabilityDispatcher([probe], postures: posture, approvals: new UnusedApprovalService(), temporalPolicy: reader);
        var session = new AgentSession("session", "test", installation.ToString("D"), organization.ToString("D"), "runtime", "tick",
            new(new HashSet<string>(), new HashSet<string>(), new HashSet<string> { "compute.destroy.v1" }, 1));
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var response = new List<CapabilityResult>();
            await foreach (var item in dispatcher.InvokeAsync(session, new RequestCapability { RequestId = "delete-" + attempt,
                Capability = "compute.destroy.v1", Payload = JsonPayload.FromUtf8("{}") }, default)) response.Add(item);
            Assert.Equal(attempt < 3, Assert.Single(response).Succeeded);
        }
        Assert.Equal(3, probe.Calls);
    }
    private sealed class Probe : IPlatformCapabilityHandler
    {
        public int Calls;
        public bool CanHandle(string capability) => capability == "compute.destroy.v1";
        public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        { Calls++; yield return new() { RequestId = request.RequestId, Succeeded = true, Payload = JsonPayload.FromUtf8("{}") }; await Task.CompletedTask; }
    }

}
