using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.Testing;
using CSweet.Compute.Contracts;
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
            var observed = await ReadFromHiredAgentAsync(fixture, defaults, delivery);
            Assert.Equal(OfficeComputeCapacityRequest.WaitingReason, observed.ErrorCode);
            Assert.Equal("Pending", observed.State);
            Assert.Null(observed.TemplateId);
            Assert.Empty(await db.Set<ComputeLocalSetup>().ToListAsync());
            Assert.Equal(fixture.Installation, (await db.Set<OfficeComputeCapacityRequest>().SingleAsync()).InstallationId);
            Assert.Single(await db.AuditOutbox.Where(x => x.RequestJson.Contains("compute.office.capacity-requested.v1")).ToListAsync());
            Assert.Single(await db.AgentRuntimeInstances.ToListAsync());
        }
    }

    [Fact, Trait("Category", "AgentHarness")]
    public async Task Only_explicit_development_mode_uses_a_ready_legacy_template_after_an_office_wait()
    {
        await using var fixture = new ComputeBrokerTests.Fixture();
        await fixture.SeedAsync();
        (await fixture.Db.Workstreams.SingleAsync()).Status = WorkstreamStatus.Active;
        var template = new ComputeTemplate("test-linux", "linux", "x64", "sha256:" + new string('a', 64), ["docker"]);
        fixture.Db.ComputeTemplates.Add(new() { Id = Guid.NewGuid(), OrganizationId = fixture.Organization,
            TemplateId = template.Id, Enabled = true, TemplateJson = JsonSerializer.Serialize(template, ComputeProtocol.Json) });
        fixture.Db.Add(new ComputeLocalSetup { Id = Guid.NewGuid(), OrganizationId = fixture.Organization,
            State = "Ready", TemplateId = template.Id, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await fixture.Db.SaveChangesAsync();

        var delivery = Guid.NewGuid();
        foreach (var legacy in new[] { false, true, false })
        {
            await using var db = new CSweetDbContext(fixture.Options);
            var defaults = new ComputeDefaultsService(db, TimeProvider.System,
                new ComputeGrantAdministration(db, TimeProvider.System, new AuditExecutionContextAccessor()),
                preparation: new ComputePreparationMode(legacy));
            await defaults.EnsureRequestedAsync(fixture.Installation, default);
            var observed = await ReadFromHiredAgentAsync(fixture, defaults, delivery);
            Assert.Equal(legacy ? "Ready" : "Pending", observed.State);
            Assert.Equal(legacy ? template.Id : null, observed.TemplateId);
            Assert.Equal(legacy ? null : OfficeComputeCapacityRequest.WaitingReason, observed.ErrorCode);
            Assert.Equal(fixture.Installation, (await db.Set<OfficeComputeCapacityRequest>().SingleAsync()).InstallationId);
        }
    }

    private static async Task<CSweet.Agent.SDK.Compute.ComputeDefaults> ReadFromHiredAgentAsync(
        ComputeBrokerTests.Fixture fixture, ComputeDefaultsService defaults, Guid delivery)
    {
        var platform = new AgentTestRuntime().RegisterCapability<JsonElement, CSweet.Application.Compute.ComputeDefaults>(
            CSweet.Agent.SDK.Compute.ComputeCapabilities.Read, (request, ct) =>
            defaults.ReadProjectAsync(fixture.Organization, fixture.Installation, request.GetProperty("workstreamId").GetGuid(), ct));
        var employee = new ComputeStatusReader(fixture.Workstream);
        var host = new AgentTestHost(employee, platform, businessId: fixture.Organization.ToString("D"),
            installationId: fixture.Installation.ToString("D"));
        var reply = await host.DeliverEventAsync("project-work-available", new { }, delivery);
        Assert.True(reply.Succeeded, reply.Error);
        return Assert.IsType<CSweet.Agent.SDK.Compute.ComputeDefaults>(employee.Observed);
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
