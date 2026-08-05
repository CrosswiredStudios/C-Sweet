using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed class PluginBootstrapCapabilityService(
    CSweetDbContext db,
    IAgentInteractiveRuntimeService runtime,
    AgentWorkInbox inbox) : IPluginBootstrapCapabilityService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public async Task<JsonElement> InvokeAsync(Guid organizationId, Guid installationId, string stepId,
        JsonElement arguments, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        var businessId = organizationId.ToString("D");
        var installation = await db.AgentInstallations.AsNoTracking().Include(x => x.PackageVersion)
            .SingleOrDefaultAsync(x => x.Id == installationId && x.BusinessId == businessId && x.IsEnabled,
                timeout.Token) ?? throw new UnauthorizedAccessException("The plugin installation was not found.");
        if (installation.SetupState == PluginSetupState.Ready)
            throw new InvalidOperationException("Bootstrap callbacks are unavailable after activation.");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(installation.PackageVersion?.ManifestJson ?? "{}")
            ?? throw new InvalidOperationException("The plugin manifest is unavailable.");
        var step = manifest.Setup?.Flows.SelectMany(x => x.Steps).SingleOrDefault(x => x.Id == stepId)
            ?? throw new InvalidOperationException("The setup step is not declared.");
        if (string.IsNullOrWhiteSpace(step.Capability))
            throw new InvalidOperationException("The setup step does not declare a callback.");
        var provided = manifest.Provides.SingleOrDefault(x => x.Name == step.Capability && x.RiskClass == "bootstrap")
            ?? throw new InvalidOperationException("The setup callback is not declared as a bootstrap capability.");
        if (!string.Equals(installation.SetupStepId, stepId, StringComparison.Ordinal))
            throw new InvalidOperationException("Only the current setup step may invoke a callback.");

        await runtime.EnsureReadyAsync(installationId, timeout.Token);
        var work = await inbox.EnqueueAsync(businessId, installationId, AgentWorkKind.Capability,
            provided.Name, arguments, $"plugin-setup:{stepId}:{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow.Add(Timeout), sourceType: "plugin-setup", maximumAttempts: 1,
            cancellationToken: timeout.Token);
        while (true)
        {
            var state = await inbox.ReadStateAsync(work.Id, timeout.Token);
            if (state.Status == AgentWorkStatus.Completed)
            {
                if (state.Completion?.Succeeded == true && state.Completion.Value.HasValue)
                    return state.Completion.Value.Value;
                throw new InvalidOperationException(state.Completion?.Error ?? "The setup callback failed.");
            }
            if (state.Status is AgentWorkStatus.Cancelled or AgentWorkStatus.DeadLetter)
                throw new InvalidOperationException(state.Error ?? "The setup callback did not complete.");
            await Task.Delay(PollInterval, timeout.Token);
        }
    }
}
