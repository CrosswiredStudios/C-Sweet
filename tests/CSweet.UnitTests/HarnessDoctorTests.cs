using CSweet.AI.Providers;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Agents;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class HarnessDoctorTests
{
    [Fact]
    public async Task BrokenSetupRemainsDiagnosableAndDoctorNeverStartsAWorkloadOrMutatesState()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var installation = Guid.NewGuid(); var provider = Guid.NewGuid();
        db.AgentInstallations.Add(new() { Id = installation, BusinessId = Guid.NewGuid().ToString("D"), IsEnabled = false, SetupState = PluginSetupState.NeedsSetup });
        db.LlmProviderProfiles.Add(new() { Id = provider, Name = "Provider", IsEnabled = true, DefaultChatModel = "model", ApiKeySecretName = "missing-key" });
        db.AgentHarnessPolicies.Add(new() { Id = installation, Revision = 1, PolicyJson = System.Text.Json.JsonSerializer.Serialize(new HarnessPolicy(1, Routes: [new("Default", provider, "model")]), HarnessWorkspaceService.Json) });
        db.AgentCapabilityApprovals.Add(new() { Id = Guid.NewGuid(), AgentInstallationId = installation });
        await db.SaveChangesAsync();
        var auditCount = await db.AuditOutbox.CountAsync();
        var service = new HarnessWorkspaceService(db, new BrokenConfiguration(), new ReadOnlyRuntime(), new BrokenSecrets(), TimeProvider.System);
        var result = await service.ReadAsync(installation, default);
        Assert.Contains(result.Findings, x => x.Code == "configuration" && x.State == "Blocked");
        Assert.Contains(result.Findings, x => x.Code == "grant" && x.State == "Blocked");
        Assert.Contains(result.Findings, x => x.Code.StartsWith("provider:") && x.State == "Blocked");
        Assert.Contains(result.Findings, x => x.Code == "approvals" && x.State == "Blocked");
        Assert.False(db.ChangeTracker.HasChanges()); Assert.Empty(await db.AgentRuntimeInstances.ToListAsync());
        Assert.Equal(auditCount, await db.AuditOutbox.CountAsync());
    }
    [Fact]
    public async Task DoctorReportsVmPlacementFailedVmsAndUnroutedJudgmentsWithoutTouchingThem()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var installation = Guid.NewGuid(); var organization = Guid.NewGuid();
        db.AgentInstallations.Add(new() { Id = installation, BusinessId = organization.ToString("D"), IsEnabled = true, SetupState = PluginSetupState.Ready,
            Grant = new() { Id = Guid.NewGuid(), AgentInstallationId = installation, GrantRevision = 1,
                RequiredCapabilitiesJson = System.Text.Json.JsonSerializer.Serialize(new[] { "compute.provision.v1", "platform.harness.judge" }) } });
        db.ComputeEnvironments.Add(new() { Id = Guid.NewGuid(), OrganizationId = organization, InstallationId = installation,
            State = CSweet.Domain.Compute.ComputeLifecycleState.Failed, LastFailureCode = "compute.image_missing" });
        await db.SaveChangesAsync();
        var service = new HarnessWorkspaceService(db, new BrokenConfiguration(), new ReadOnlyRuntime(), new BrokenSecrets(), TimeProvider.System);
        var result = await service.ReadAsync(installation, default);
        Assert.Contains(result.Findings, x => x.Code == "compute:placement" && x.State == "Blocked");
        var vms = Assert.Single(result.Findings, x => x.Code == "compute:environments");
        Assert.Equal("Attention", vms.State); Assert.Contains("compute.image_missing", vms.Detail);
        Assert.Contains(result.Findings, x => x.Code == "route:Judgment" && x.State == "Blocked");
        Assert.False(db.ChangeTracker.HasChanges());
    }

    private sealed class BrokenSecrets : ILlmProviderSecretStore
    {
        public Task<string?> GetAsync(string name, CancellationToken token = default) => throw new InvalidOperationException("Unavailable");
        public Task StoreAsync(string name, string value, CancellationToken token = default) => throw new NotSupportedException();
        public Task DeleteAsync(string name, CancellationToken token = default) => throw new NotSupportedException();
    }
    private sealed class ReadOnlyRuntime : IAgentInteractiveRuntimeService
    {
        public Task<AgentRuntimeReadinessResponse> EnsureReadyAsync(Guid id, CancellationToken token = default) => throw new InvalidOperationException("Doctor must not start a workload.");
        public Task<AgentRuntimeReadinessResponse> GetStatusAsync(Guid id, CancellationToken token = default) => Task.FromResult(new AgentRuntimeReadinessResponse(id, null, "Offline", null, null, null, null, null, false, false));
    }
    private sealed class BrokenConfiguration : IAgentConfigurationService
    {
        public Task<EffectiveAgentConfiguration> ResolveInstallationAsync(Guid id, CancellationToken token = default) => throw new AgentInstallationException("Broken configuration");
        public Task<AgentConfigurationView> GetDefinitionAsync(Guid id, CancellationToken token = default) => throw new NotSupportedException();
        public Task<AgentConfigurationView> SaveDefinitionAsync(Guid id, PutAgentDefinitionConfigurationRequest request, CancellationToken token = default) => throw new NotSupportedException();
        public Task<AgentConfigurationView> GetEmployeeAsync(Guid org, Guid employee, CancellationToken token = default) => throw new NotSupportedException();
        public Task<AgentConfigurationView> SaveEmployeeOverridesAsync(Guid org, Guid employee, PutAgentConfigurationOverridesRequest request, CancellationToken token = default) => throw new NotSupportedException();
        public Task<AgentConfigurationView> RestoreEmployeeOverrideAsync(Guid org, Guid employee, string key, long revision, CancellationToken token = default) => throw new NotSupportedException();
        public Task<AgentConfigurationView> RestoreAllEmployeeOverridesAsync(Guid org, Guid employee, long revision, CancellationToken token = default) => throw new NotSupportedException();
    }
}
