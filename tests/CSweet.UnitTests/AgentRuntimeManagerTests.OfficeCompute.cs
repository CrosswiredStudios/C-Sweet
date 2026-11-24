using System.Text.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CSweet.Agent.SDK;
using CSweet.Agent.Testing;
using CSweet.AgentHost.Broker;
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
            var beforeDelivery = await ReadFromHiredAgentAsync(fixture, defaults, delivery);
            Assert.Equal(legacy ? template.Id : null, beforeDelivery.TemplateId);
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

    [Fact, Trait("Category", "AgentHarness")]
    public async Task Hired_employee_replays_office_availability_and_only_the_assigned_office_claims_the_vm()
        => await VerifyHiredOfficePlacementAsync(new ComputeBrokerTests.Fixture());

    [Fact, Trait("Category", "AgentHarness")]
    public async Task Mac_office_claims_an_arm64_workspace_after_replayed_availability()
        => await VerifyHiredOfficePlacementAsync(new ComputeBrokerTests.Fixture(), architecture: "arm64");

    internal static async Task VerifyHiredOfficePlacementAsync(ComputeBrokerTests.Fixture storage,
        Func<ICSweetAgent>? createEmployee = null, Action<AgentTestRuntime>? configurePlatform = null, string architecture = "x64")
    {
        await using var fixture = storage;
        await fixture.SeedAsync();
        using var nodeKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var clock = TimeProvider.System;
        var now = clock.GetUtcNow();
        using var cert = new CertificateRequest("CN=Office-test", nodeKey, HashAlgorithmName.SHA256)
            .CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
        var pool = new ExecutionPool { Id = Guid.NewGuid(), AllowedBusinessIdsJson = JsonSerializer.Serialize(new[] { fixture.Organization }) };
        var office = new ExecutionNode { Id = Guid.NewGuid(), ExecutionPoolId = pool.Id, ApprovedAt = now,
            OperatingSystem = architecture == "arm64" ? "macos" : "windows", Architecture = architecture, NodeVersion = "0.17.2",
            Status = ExecutionNodeStatus.Ready, LastHeartbeatAt = now, CertificateExpiresAt = now.AddDays(1),
            IssuedCertificateBase64 = Convert.ToBase64String(cert.RawData), CertificateThumbprint = cert.Thumbprint };
        var db = fixture.Db;
        db.ExecutionPools.Add(pool);
        db.ExecutionNodes.Add(office);
        var agentImage = "sha256:" + new string('c', 64);
        var fleet = new ExecutionFleetOptions { PublicLaunchEnabled = office.OperatingSystem == "macos" };
        if (office.OperatingSystem == "windows")
        {
            var enrollment = new ExecutionNodeEnrollment { Id = Guid.NewGuid(), ExecutionPoolId = pool.Id, ExecutionNodeId = office.Id,
                ApprovedAt = now, Status = ExecutionEnrollmentStatus.Approved, TokenHash = Guid.NewGuid().ToString("N") };
            db.ExecutionNodeEnrollments.Add(enrollment);
            db.ExecutionNodeProviders.Add(new() { Id = Guid.NewGuid(), ExecutionNodeId = office.Id, ProviderId = "hyperv-gen2",
                BrokerProtocolVersion = "1.1", GuestImageDigest = agentImage, CertificationSuiteVersion = "hire-suite",
                CertificationEvidenceDigest = "sha256:" + new string('e', 64), CertifiedAt = now.AddHours(-1),
                IsAvailable = true, SupportsBuilderWorkloads = true, SupportsRuntimeWorkloads = true });
            fleet.CombinedOfficeEnrollmentId = enrollment.Id;
        }
        var pinnedImages = Microsoft.Extensions.Options.Options.Create(new AgentRuntimeManagerOptions {
            RequiredCertificationSuiteVersion = "hire-suite", BuilderGuestImageDigest = agentImage, RuntimeGuestImageDigest = agentImage });
        db.AgentSchedules.Add(new AgentSchedule { Id = Guid.NewGuid(), AgentInstallationId = fixture.Installation,
            ActivationMode = ActivationMode.OnDemand, IsEnabled = true });
        (await db.Workstreams.SingleAsync()).Status = WorkstreamStatus.Active;
        await db.SaveChangesAsync();
        ComputeDefaultsService Defaults(CSweetDbContext store) => new(store, clock,
            new ComputeGrantAdministration(store, clock, new AuditExecutionContextAccessor()));
        var defaults = Defaults(db);
        Assert.True(await CreateManager(db, new FakeRunner(), computeDefaults: defaults)
            .EnsureRuntimeQueuedAsync(fixture.Installation, "Hired employee"));
        var digest = "sha256:" + new string('a', 64);
        var template = OfficeComputePlacement.TemplateId(digest);
        var offer = new Office.Contracts.ControlPlane.OfficeComputeOffer { OrganizationId = fixture.Organization.ToString("D"),
            TemplateId = template, ImageDigest = digest, MaximumEnvironments = 1, CpuCount = 2, MemoryMib = 2048, DiskMib = 20480 };
        offer.Features.Add("docker");
        await OfficeComputePlacement.ApplyOfferAsync(db, office, offer, now, default);
        Assert.Null(office.ComputeTemplateId);
        var installed = new OfficeComputeImageApproval(fixture.Organization, digest, ["docker"]);
        await OfficeComputePlacement.ApplyOfferAsync(db, office, offer, now, default,
            new() { CombinedInstallImage = installed with { OrganizationId = Guid.NewGuid() } });
        Assert.Null(office.ComputeTemplateId);
        await OfficeComputePlacement.ApplyOfferAsync(db, office, offer, now, default,
            new() { CombinedInstallImage = installed, ApprovedImages = [installed with { ImageDigest = "sha256:" + new string('b', 64) }] });
        Assert.Null(office.ComputeTemplateId);
        await OfficeComputePlacement.ApplyOfferAsync(db, office, offer, now, default,
            new() { CombinedInstallImage = installed });
        Assert.Equal(template, office.ComputeTemplateId);
        await OfficeComputePlacement.WakeWaitingAsync(db, fixture.Organization, office.Id, 1, template, now, default);
        await db.SaveChangesAsync();
        await OfficeComputePlacement.WakeWaitingAsync(db, fixture.Organization, office.Id, 1, template, now, default);
        await db.SaveChangesAsync();
        var wake = await db.AgentPlatformEventOutbox.SingleAsync(x => x.EventType == "com.csweet.compute.available.v1");
        Assert.Equal(fixture.Installation, wake.TargetInstallationId);
        using var signer = new ComputeDispatchTests.Signer();
        for (var delivery = 0; delivery < 2; delivery++)
        {
            await using var restarted = new CSweetDbContext(fixture.Options, fleetOptions: Microsoft.Extensions.Options.Options.Create(fleet), runtimeOptions: pinnedImages);
            var registry = new ComputeRegistry(restarted, clock, new AuditExecutionContextAccessor());
            var broker = new ComputeBroker(restarted, registry, clock, fixture.Ledger);
            var employee = createEmployee?.Invoke() ?? new ProjectWorkspaceRequester(architecture);
            var handler = new ComputeCapabilityHandler(broker, Defaults(restarted));
            var capabilities = new HashSet<string> { InfrastructureActions.Read, InfrastructureActions.Provision };
            var session = new AgentSession("session", employee.AgentId, fixture.Installation.ToString("D"),
                fixture.Organization.ToString("D"), "runtime", "tick", new(new HashSet<string>(), new HashSet<string>(), capabilities, 1));
            var platform = new AgentTestRuntime();
            foreach (var action in capabilities)
                platform.RegisterCapability<JsonElement, JsonElement>(action, async (input, _) =>
                {
                    var response = await ComputeCapabilityTests.Invoke(handler, session, action, input);
                    Assert.True(response.Succeeded, response.Error);
                    return response.Payload.ToElement();
                });
            configurePlatform?.Invoke(platform);
            var host = new AgentTestHost(employee, platform,
                businessId: fixture.Organization.ToString("D"), installationId: fixture.Installation.ToString("D"));
            var reply = await host.DeliverEventAsync(wake.EventType, JsonSerializer.Deserialize<JsonElement>(wake.DataJson), wake.Id);
            Assert.True(reply.Succeeded, reply.Error);
            var op = await restarted.ComputeOperations.SingleAsync();
            var authorizer = new ComputeDispatchAuthorizer(restarted, broker, signer, clock);
            Assert.Null(await authorizer.ClaimAsync(op.Id, default));
            Assert.Null(await authorizer.ClaimOfficeWorkAsync(Guid.NewGuid(), signer, default));
            var packet = await authorizer.ClaimOfficeWorkAsync(office.Id, signer, default);
            Assert.Equal(delivery == 0, packet is not null);
            var claim = Assert.Single(signer.Claims);
            var env = await restarted.ComputeEnvironments.SingleAsync();
            Assert.Null(await registry.ResolveAsync(fixture.Organization, template, default));
            Assert.Equal(architecture == "arm64" ? OfficeComputePlacement.AppleProviderId : OfficeComputePlacement.ProviderId, claim.ProviderId);
            Assert.Equal(architecture, JsonSerializer.Deserialize<ComputeSpecification>(env.SpecificationJson, ComputeProtocol.Json)!.Architecture);
            Assert.Equal(office.Id, env.ProviderNodeId);
            Assert.Equal(fixture.Workstream, env.WorkstreamId);
            Assert.Equal(env.Id, claim.EnvironmentId);
            Assert.Equal(fixture.Installation, claim.InstallationId);
            Assert.Equal(office.Id, claim.NodeId);
            Assert.Equal(claim.DispatchId, op.DispatchLeaseId);
        }
    }

    private sealed class ProjectWorkspaceRequester(string architecture) : ICSweetAgent
    {
        public string AgentId => "office-compute-test";
        public string Version => "1.0.0";
        public async Task HandleEventAsync(AgentEventEnvelope message, AgentRuntimeContext context, CancellationToken token)
        {
            var defaults = await context.Platform.Compute.GetDefaultsAsync(token);
            Assert.Equal("Ready", defaults.State);
            var project = defaults.WorkstreamId!.Value;
            var key = $"project:{project:N}:developer:{context.InstallationId}:generation:0";
            await context.Platform.Compute.ProvisionAsync(new(project, key, key,
                new("linux", architecture, defaults.TemplateId!, new(2, 2048, 20480), 0)), token);
        }
        public Task<AgentWorkResult> ExecuteCapabilityAsync(AgentCapabilityRequest request, AgentRuntimeContext context, CancellationToken token) =>
            throw new NotSupportedException();
    }
}
