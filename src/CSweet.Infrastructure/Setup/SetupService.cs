using CSweet.Application.Setup;
using CSweet.Contracts.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed class SetupService : ISetupService
{
    public static readonly IReadOnlyList<(string Key, string DisplayName, bool IsRequired)> RequiredSteps =
    [
        ("welcome", "Welcome", true),
        ("llm-provider", "LLM Provider", true),
        ("genai-provider", "GenAI Provider", false),
        ("email-delivery", "Email Delivery", false),
        ("communications", "Communications", false)
    ];

    private readonly CSweetDbContext _dbContext;

    public SetupService(CSweetDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task EnsureSeededAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        if (!await _dbContext.SystemConfigurations.AnyAsync(cancellationToken))
        {
            _dbContext.SystemConfigurations.Add(new SystemConfiguration
            {
                Id = Guid.NewGuid(),
                IsFirstRunComplete = false,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        var existingSteps = await _dbContext.OnboardingSteps
            .ToDictionaryAsync(x => x.Key, StringComparer.OrdinalIgnoreCase, cancellationToken);

        foreach (var stepDefinition in RequiredSteps)
        {
            if (existingSteps.TryGetValue(stepDefinition.Key, out var existingStep))
            {
                existingStep.DisplayName = stepDefinition.DisplayName;
                existingStep.IsRequired = stepDefinition.IsRequired;
                existingStep.UpdatedAt = now;
                continue;
            }

            _dbContext.OnboardingSteps.Add(new OnboardingStep
            {
                Id = Guid.NewGuid(),
                Key = stepDefinition.Key,
                DisplayName = stepDefinition.DisplayName,
                IsRequired = stepDefinition.IsRequired,
                IsComplete = false,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        var activeStepKeys = RequiredSteps.Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var obsoleteStep in existingSteps.Values.Where(x => !activeStepKeys.Contains(x.Key)))
        {
            obsoleteStep.IsRequired = false;
            obsoleteStep.UpdatedAt = now;
        }

        if (!await _dbContext.AgentRuntimeGlobalSettings.AnyAsync(cancellationToken))
        {
            _dbContext.AgentRuntimeGlobalSettings.Add(new AgentRuntimeGlobalSettings
            {
                Id = Guid.NewGuid(),
                EnableImportedAgents = false,
                DefaultActivationMode = ActivationMode.Periodic,
                DefaultTickFrequencySeconds = 3600,
                MinimumTickFrequencySeconds = 300,
                DefaultMaxRuntimeSeconds = 600,
                DefaultOverlapPolicy = OverlapPolicy.Skip,
                AllowAlwaysOnCommunityAgents = false,
                DefaultRestartPolicy = RestartPolicy.Never,
                GlobalMaxActiveContainers = 10,
                PerBusinessMaxActiveContainers = 5,
                PerInstallationMaxActiveContainers = 1,
                DefaultContainerMemoryMb = 512,
                MaximumContainerMemoryMb = 2048,
                DefaultContainerCpuPercent = 50,
                MaximumContainerCpuPercent = 200,
                DefaultContainerPidsLimit = 100,
                DefaultContainerLogLimitMb = 10,
                ContainerStartTimeoutSeconds = 60,
                McpSessionTimeoutSeconds = 30,
                ContainerStopGraceSeconds = 15,
                DefaultNetworkPolicy = "McpOnly",
                AllowPublicInternetByDefault = false,
                AllowedPackageFeedHosts = string.Empty,
                BlockedNetworkCidrs = string.Empty,
                AgentSourceRootPath = string.Empty,
                AgentPackageCachePath = string.Empty,
                DotNetBuilderImage = "mcr.microsoft.com/dotnet/sdk:10.0",
                DotNetRuntimeBaseImage = "mcr.microsoft.com/dotnet/runtime:10.0",
                BuildTimeoutSeconds = 600,
                BuildMemoryMb = 2048,
                BuildCpuPercent = 200,
                MaximumRepositorySizeMb = 500,
                MaximumBuildLogMb = 10,
                KeepFailedBuildWorkspaces = false,
                CompletedRuntimeRetentionDays = 14,
                FailedRuntimeRetentionDays = 30,
                BuildLogRetentionDays = 30,
                RemoveContainersAfterCompletion = true,
                RemoveWorkspacesAfterCompletion = true,
                UpdatedAt = now
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<SetupStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSeededAsync(cancellationToken);

        var configuration = await GetConfigurationAsync(cancellationToken);
        var activeStepKeys = RequiredSteps.Select(x => x.Key).ToList();
        var steps = await _dbContext.OnboardingSteps
            .Where(x => activeStepKeys.Contains(x.Key))
            .Select(x => new OnboardingStepStatusDto(
                x.Key,
                x.DisplayName,
                x.IsRequired,
                x.IsComplete))
            .ToListAsync(cancellationToken);

        steps = steps
            .OrderBy(x => activeStepKeys.IndexOf(x.Key))
            .ToList();

        return new SetupStatusResponse(
            configuration.IsFirstRunComplete,
            configuration.DefaultChatProviderId,
            configuration.DefaultEmbeddingProviderId,
            steps);
    }

    public async Task<SetupActionResponse> CompleteStepAsync(string key, CancellationToken cancellationToken = default)
    {
        var normalizedKey = RequiredSteps
            .FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase))
            .Key;

        if (normalizedKey is null)
        {
            return await FailureAsync("unknown_step", $"Unknown setup step '{key}'.", cancellationToken);
        }

        await EnsureSeededAsync(cancellationToken);

        var step = await _dbContext.OnboardingSteps
            .SingleAsync(x => x.Key == normalizedKey, cancellationToken);

        if (!step.IsComplete)
        {
            var now = DateTimeOffset.UtcNow;
            step.IsComplete = true;
            step.CompletedAt = now;
            step.UpdatedAt = now;

            _dbContext.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                EventType = "setup.step.completed",
                EntityType = nameof(OnboardingStep),
                EntityId = step.Id,
                Summary = $"Setup step completed: {step.Key}",
                CreatedAt = now
            });

            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return await SuccessAsync(cancellationToken);
    }

    public async Task<SetupActionResponse> CompleteFirstRunAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSeededAsync(cancellationToken);

        if (!await _dbContext.LlmProviderProfiles.AnyAsync(x => x.IsEnabled, cancellationToken))
        {
            return await FailureAsync("provider_profile_required", "At least one enabled provider profile is required.", cancellationToken);
        }

        if (!await ArePriorRequiredStepsCompleteAsync(cancellationToken))
        {
            return await FailureAsync("required_steps_incomplete", "Required setup steps must be complete before first-run setup can finish.", cancellationToken);
        }

        var configuration = await GetConfigurationAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        configuration.IsFirstRunComplete = true;
        configuration.UpdatedAt = now;

        var communicationsStep = await _dbContext.OnboardingSteps.SingleAsync(x => x.Key == "communications", cancellationToken);
        communicationsStep.IsComplete = true;
        communicationsStep.CompletedAt ??= now;
        communicationsStep.UpdatedAt = now;

        _dbContext.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            EventType = "setup.first_run.completed",
            EntityType = nameof(SystemConfiguration),
            EntityId = configuration.Id,
            Summary = "First-run setup completed.",
            CreatedAt = now
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        return await SuccessAsync(cancellationToken);
    }

    private async Task<SystemConfiguration> GetConfigurationAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.SystemConfigurations
            .OrderBy(x => x.CreatedAt)
            .FirstAsync(cancellationToken);
    }

    private async Task<bool> ArePriorRequiredStepsCompleteAsync(CancellationToken cancellationToken)
    {
        var requiredPriorStepKeys = RequiredSteps
            .Where(x => x.IsRequired)
            .Select(x => x.Key)
            .ToList();

        return !await _dbContext.OnboardingSteps
            .AnyAsync(x => requiredPriorStepKeys.Contains(x.Key) && !x.IsComplete, cancellationToken);
    }

    private async Task<SetupActionResponse> SuccessAsync(CancellationToken cancellationToken)
    {
        return new SetupActionResponse(true, null, null, await GetStatusAsync(cancellationToken));
    }

    private async Task<SetupActionResponse> FailureAsync(string errorCode, string message, CancellationToken cancellationToken)
    {
        return new SetupActionResponse(false, errorCode, message, await GetStatusAsync(cancellationToken));
    }
}
