using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.Testing;
using CSweet.Domain.Compute;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Compute;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentRuntimeManagerTests
{
    [Fact, Trait("Category", "AgentHarness")]
    public async Task Hired_agent_waits_for_office_capacity_across_restart_without_local_setup()
    {
        await using var fixture = new ComputeBrokerTests.Fixture();
        await fixture.SeedAsync();
        fixture.Db.AgentSchedules.Add(new AgentSchedule { Id = Guid.NewGuid(), AgentInstallationId = fixture.Installation,
            ActivationMode = ActivationMode.OnDemand, IsEnabled = true });
        (await fixture.Db.Workstreams.SingleAsync()).Status = WorkstreamStatus.Active;
        await fixture.Db.SaveChangesAsync();

        var delivery = Guid.NewGuid();
        for (var pass = 0; pass < 2; pass++)
        {
            await using var db = new CSweetDbContext(fixture.Options);
            var defaults = new ComputeDefaultsService(db, TimeProvider.System,
                new ComputeGrantAdministration(db, TimeProvider.System, new AuditExecutionContextAccessor()));
            var manager = CreateManager(db, new FakeRunner(), computeDefaults: defaults);
            Assert.Equal(pass == 0, await manager.EnsureRuntimeQueuedAsync(fixture.Installation, "Hired employee"));
            var platform = new AgentTestRuntime().RegisterCapability<JsonElement, CSweet.Application.Compute.ComputeDefaults>(
                CSweet.Agent.SDK.Compute.ComputeCapabilities.Read, (request, ct) =>
                defaults.ReadProjectAsync(fixture.Organization, fixture.Installation, request.GetProperty("workstreamId").GetGuid(), ct));
            var employee = new ComputeStatusReader(fixture.Workstream);
            var host = new AgentTestHost(employee, platform, businessId: fixture.Organization.ToString("D"),
                installationId: fixture.Installation.ToString("D"));
            var reply = await host.DeliverEventAsync("project-work-available", new { }, delivery);
            Assert.True(reply.Succeeded, reply.Error);
            Assert.Equal(OfficeComputeCapacityRequest.WaitingReason, employee.Observed?.ErrorCode);
            Assert.Equal("Pending", employee.Observed?.State);
            Assert.Null(employee.Observed?.TemplateId);
            Assert.Empty(await db.Set<ComputeLocalSetup>().ToListAsync());
            Assert.Equal(fixture.Installation, (await db.Set<OfficeComputeCapacityRequest>().SingleAsync()).InstallationId);
            Assert.Single(await db.AuditOutbox.Where(x => x.RequestJson.Contains("compute.office.capacity-requested.v1")).ToListAsync());
            Assert.Single(await db.AgentRuntimeInstances.ToListAsync());
        }
    }

    private sealed class ComputeStatusReader(Guid project) : ICSweetAgent
    {
        public string AgentId => "compute-status-reader";
        public string Version => "1.0.0";
        public CSweet.Agent.SDK.Compute.ComputeDefaults? Observed { get; private set; }
        public async Task HandleEventAsync(AgentEventEnvelope message, AgentRuntimeContext context, CancellationToken token) =>
            Observed = await context.Platform.Compute.GetProjectDefaultsAsync(project, token);
        public Task<AgentWorkResult> ExecuteCapabilityAsync(AgentCapabilityRequest request, AgentRuntimeContext context, CancellationToken token) =>
            throw new NotSupportedException();
    }
}
