using System.Reflection;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Application.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class PersonalTodoAttributionTests
{
    [Theory]
    [InlineData("active", true)]
    [InlineData("other-runtime", false)]
    [InlineData("other-business", false)]
    [InlineData("expired", false)]
    [InlineData("ambiguous", false)]
    public async Task BrokerBindsClaimsOnlyToTheSingleAuthenticatedRuntimeAttempt(string scenario, bool bound)
    {
        await using var db = WorkEfficiencyTests.Database();
        var org = Guid.NewGuid(); var installation = Guid.NewGuid(); var runtime = Guid.NewGuid();
        var owner = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org,
            AgentInstallationId = installation, IsActive = true };
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = scenario == "other-business" ? Guid.NewGuid().ToString() : org.ToString(),
            AgentInstallationId = installation, Status = AgentWorkStatus.Leased, DeadlineAt = DateTimeOffset.UtcNow.AddHours(1) };
        var attempt = new AgentWorkAttempt { Id = Guid.NewGuid(), AgentWorkItem = work, AgentWorkItemId = work.Id,
            RuntimeInstanceId = scenario == "other-runtime" ? Guid.NewGuid() : runtime,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(scenario == "expired" ? -1 : 5) };
        db.AddRange(owner, work, attempt);
        if (scenario == "ambiguous")
            db.Add(new AgentWorkAttempt { Id = Guid.NewGuid(), AgentWorkItem = work, AgentWorkItemId = work.Id,
                RuntimeInstanceId = runtime, LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) });
        await db.SaveChangesAsync();
        var service = DispatchProxy.Create<IPersonalTodoService, RecordingService>();
        var capture = (RecordingService)service;
        var session = new AgentSession("test", "test-agent", installation.ToString(), org.ToString(),
            runtime.ToString(), Guid.NewGuid().ToString(), new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(),
                new HashSet<string> { Wire.PersonalTodoCapabilities.Claim }, 1));
        var request = new RequestCapability { RequestId = "claim", Capability = Wire.PersonalTodoCapabilities.Claim,
            Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new
            {
                eventId = Guid.NewGuid(), idempotencyKey = "claim",
                workAttemptId = Guid.NewGuid() // Caller telemetry must not choose the accounting attempt.
            })) };
        await foreach (var result in new PersonalTodoCapabilityHandler(db, service).HandleAsync(session, request, default))
            Assert.True(result.Succeeded, result.Error);
        Assert.NotNull(capture.Actor);
        Assert.Equal(bound ? attempt.Id : (Guid?)null, capture.Actor.WorkAttemptId);
    }

    public class RecordingService : DispatchProxy
    {
        public PersonalTodoActor? Actor { get; private set; }
        protected override object Invoke(MethodInfo? method, object?[]? args)
        {
            Assert.Equal(nameof(IPersonalTodoService.ClaimAsync), method!.Name);
            Actor = Assert.IsType<PersonalTodoActor>(args![1]);
            return Task.FromResult(new Wire.PersonalTodoClaim(null));
        }
    }
}
