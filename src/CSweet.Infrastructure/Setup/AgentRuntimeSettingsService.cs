using System;
using CSweet.Application.Setup;
using CSweet.Contracts.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed class AgentRuntimeSettingsService : IAgentRuntimeSettingsService
{
    private readonly CSweetDbContext _dbContext;
    private readonly IAuditEventWriter _auditWriter;

    public AgentRuntimeSettingsService(CSweetDbContext dbContext, IAuditEventWriter auditWriter)
    {
        _dbContext = dbContext;
        _auditWriter = auditWriter;
    }

    public async Task<AgentRuntimeSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _dbContext.AgentRuntimeGlobalSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);

        if (settings is null)
        {
            throw new InvalidOperationException("Agent runtime settings have not been seeded.");
        }

        return ToResponse(settings);
    }

    public async Task<AgentRuntimeSettingsActionResponse> UpdateAsync(
        UpdateAgentRuntimeSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        var settings = await _dbContext.AgentRuntimeGlobalSettings
            .FirstOrDefaultAsync(cancellationToken);

        if (settings is null)
        {
            return new AgentRuntimeSettingsActionResponse(
                false,
                "Agent runtime settings have not been seeded.",
                null);
        }

        var errors = Validate(request, settings);

        if (errors.Count > 0)
        {
            return new AgentRuntimeSettingsActionResponse(
                false,
                string.Join("; ", errors),
                null);
        }

        Apply(request, settings);

        _dbContext.AgentRuntimeGlobalSettings.Update(settings);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditWriter.WriteAsync(
            "agent-runtime.settings.updated",
            "AgentRuntimeGlobalSettings",
            settings.Id,
            "Agent runtime global settings were updated.",
            null,
            cancellationToken);

        return new AgentRuntimeSettingsActionResponse(
            true,
            "Settings saved.",
            ToResponse(settings));
    }

    private static List<string> Validate(
        UpdateAgentRuntimeSettingsRequest request,
        AgentRuntimeGlobalSettings settings)
    {
        var errors = new List<string>();
        var minimumTickFrequencySeconds = request.MinimumTickFrequencySeconds ?? settings.MinimumTickFrequencySeconds;
        var defaultTickFrequencySeconds = request.DefaultTickFrequencySeconds ?? settings.DefaultTickFrequencySeconds;
        var defaultContainerMemoryMb = request.DefaultContainerMemoryMb ?? settings.DefaultContainerMemoryMb;
        var maximumContainerMemoryMb = request.MaximumContainerMemoryMb ?? settings.MaximumContainerMemoryMb;
        var defaultContainerCpuPercent = request.DefaultContainerCpuPercent ?? settings.DefaultContainerCpuPercent;
        var maximumContainerCpuPercent = request.MaximumContainerCpuPercent ?? settings.MaximumContainerCpuPercent;

        if (minimumTickFrequencySeconds < 60)
        {
            errors.Add("Minimum tick frequency must be at least 60 seconds.");
        }

        if (defaultTickFrequencySeconds < minimumTickFrequencySeconds)
        {
            errors.Add("Default tick frequency must be greater than or equal to minimum tick frequency.");
        }

        if (defaultContainerMemoryMb > maximumContainerMemoryMb)
        {
            errors.Add("Default container memory must be less than or equal to maximum container memory.");
        }

        if (defaultContainerCpuPercent > maximumContainerCpuPercent)
        {
            errors.Add("Default container CPU must be less than or equal to maximum container CPU.");
        }

        if (request.DefaultActivationMode is string activationMode &&
            (!Enum.TryParse<ActivationMode>(activationMode, out var parsedActivationMode) ||
             !Enum.IsDefined(parsedActivationMode)))
        {
            errors.Add("Default activation mode is invalid.");
        }

        if (request.DefaultOverlapPolicy is string overlapPolicy &&
            (!Enum.TryParse<OverlapPolicy>(overlapPolicy, out var parsedOverlapPolicy) ||
             !Enum.IsDefined(parsedOverlapPolicy)))
        {
            errors.Add("Default overlap policy is invalid.");
        }

        if (request.DefaultRestartPolicy is string restartPolicy &&
            (!Enum.TryParse<RestartPolicy>(restartPolicy, out var parsedRestartPolicy) ||
             !Enum.IsDefined(parsedRestartPolicy)))
        {
            errors.Add("Default restart policy is invalid.");
        }

        if (request.GlobalMaxActiveContainers is int g && g <= 0)
        {
            errors.Add("Global max active containers must be positive.");
        }

        if (request.PerBusinessMaxActiveContainers is int p && p <= 0)
        {
            errors.Add("Per-business max active containers must be positive.");
        }

        if (request.PerInstallationMaxActiveContainers is int pi && pi <= 0)
        {
            errors.Add("Per-installation max active containers must be positive.");
        }

        AddPositiveError(request.DefaultTickFrequencySeconds, "Default tick frequency", errors);
        AddPositiveError(request.DefaultMaxRuntimeSeconds, "Default max runtime", errors);
        AddPositiveError(request.DefaultContainerMemoryMb, "Default container memory", errors);
        AddPositiveError(request.MaximumContainerMemoryMb, "Maximum container memory", errors);
        AddPositiveError(request.DefaultContainerCpuPercent, "Default container CPU", errors);
        AddPositiveError(request.MaximumContainerCpuPercent, "Maximum container CPU", errors);
        AddPositiveError(request.DefaultContainerPidsLimit, "Default container PIDs limit", errors);
        AddPositiveError(request.DefaultContainerLogLimitMb, "Default container log limit", errors);
        AddPositiveError(request.ContainerStartTimeoutSeconds, "Container start timeout", errors);
        AddPositiveError(request.McpSessionTimeoutSeconds, "MCP session timeout", errors);
        AddPositiveError(request.ContainerStopGraceSeconds, "Container stop grace", errors);
        AddPositiveError(request.BuildTimeoutSeconds, "Build timeout", errors);
        AddPositiveError(request.BuildMemoryMb, "Build memory", errors);
        AddPositiveError(request.BuildCpuPercent, "Build CPU", errors);
        AddPositiveError(request.MaximumRepositorySizeMb, "Maximum repository size", errors);
        AddPositiveError(request.MaximumBuildLogMb, "Maximum build log", errors);
        AddPositiveError(request.CompletedRuntimeRetentionDays, "Completed runtime retention", errors);
        AddPositiveError(request.FailedRuntimeRetentionDays, "Failed runtime retention", errors);
        AddPositiveError(request.BuildLogRetentionDays, "Build log retention", errors);

        if (request.DotNetBuilderImage is string builderImg && string.IsNullOrWhiteSpace(builderImg))
        {
            errors.Add("Builder image name cannot be empty.");
        }

        if (request.DotNetRuntimeBaseImage is string runtimeImg && string.IsNullOrWhiteSpace(runtimeImg))
        {
            errors.Add("Runtime base image name cannot be empty.");
        }

        return errors;
    }

    private static void AddPositiveError(int? value, string fieldName, List<string> errors)
    {
        if (value <= 0)
        {
            errors.Add($"{fieldName} must be positive.");
        }
    }

    private static void Apply(UpdateAgentRuntimeSettingsRequest request, AgentRuntimeGlobalSettings settings)
    {
        ApplyBool(request.EnableImportedAgents, v => settings.EnableImportedAgents = v);
        ApplyString(request.DefaultActivationMode, v => settings.DefaultActivationMode = ParseActivationMode(v));
        ApplyInt(request.DefaultTickFrequencySeconds, v => settings.DefaultTickFrequencySeconds = v);
        ApplyInt(request.MinimumTickFrequencySeconds, v => settings.MinimumTickFrequencySeconds = v);
        ApplyInt(request.DefaultMaxRuntimeSeconds, v => settings.DefaultMaxRuntimeSeconds = v);
        ApplyString(request.DefaultOverlapPolicy, v => settings.DefaultOverlapPolicy = ParseOverlapPolicy(v));
        ApplyBool(request.AllowAlwaysOnCommunityAgents, v => settings.AllowAlwaysOnCommunityAgents = v);
        ApplyString(request.DefaultRestartPolicy, v => settings.DefaultRestartPolicy = ParseRestartPolicy(v));
        ApplyInt(request.GlobalMaxActiveContainers, v => settings.GlobalMaxActiveContainers = v);
        ApplyInt(request.PerBusinessMaxActiveContainers, v => settings.PerBusinessMaxActiveContainers = v);
        ApplyInt(request.PerInstallationMaxActiveContainers, v => settings.PerInstallationMaxActiveContainers = v);
        ApplyInt(request.DefaultContainerMemoryMb, v => settings.DefaultContainerMemoryMb = v);
        ApplyInt(request.MaximumContainerMemoryMb, v => settings.MaximumContainerMemoryMb = v);
        ApplyInt(request.DefaultContainerCpuPercent, v => settings.DefaultContainerCpuPercent = v);
        ApplyInt(request.MaximumContainerCpuPercent, v => settings.MaximumContainerCpuPercent = v);
        ApplyInt(request.DefaultContainerPidsLimit, v => settings.DefaultContainerPidsLimit = v);
        ApplyInt(request.DefaultContainerLogLimitMb, v => settings.DefaultContainerLogLimitMb = v);
        ApplyInt(request.ContainerStartTimeoutSeconds, v => settings.ContainerStartTimeoutSeconds = v);
        ApplyInt(request.McpSessionTimeoutSeconds, v => settings.McpSessionTimeoutSeconds = v);
        ApplyInt(request.ContainerStopGraceSeconds, v => settings.ContainerStopGraceSeconds = v);
        ApplyString(request.DefaultNetworkPolicy, v => settings.DefaultNetworkPolicy = v);
        ApplyBool(request.AllowPublicInternetByDefault, v => settings.AllowPublicInternetByDefault = v);
        ApplyOptionalString(request.AllowedPackageFeedHosts, v => settings.AllowedPackageFeedHosts = v);
        ApplyOptionalString(request.BlockedNetworkCidrs, v => settings.BlockedNetworkCidrs = v);
        ApplyOptionalString(request.AgentSourceRootPath, v => settings.AgentSourceRootPath = v);
        ApplyOptionalString(request.AgentPackageCachePath, v => settings.AgentPackageCachePath = v);
        ApplyString(request.DotNetBuilderImage, v => settings.DotNetBuilderImage = v);
        ApplyString(request.DotNetRuntimeBaseImage, v => settings.DotNetRuntimeBaseImage = v);
        ApplyInt(request.BuildTimeoutSeconds, v => settings.BuildTimeoutSeconds = v);
        ApplyInt(request.BuildMemoryMb, v => settings.BuildMemoryMb = v);
        ApplyInt(request.BuildCpuPercent, v => settings.BuildCpuPercent = v);
        ApplyInt(request.MaximumRepositorySizeMb, v => settings.MaximumRepositorySizeMb = v);
        ApplyInt(request.MaximumBuildLogMb, v => settings.MaximumBuildLogMb = v);
        ApplyBool(request.KeepFailedBuildWorkspaces, v => settings.KeepFailedBuildWorkspaces = v);
        ApplyInt(request.CompletedRuntimeRetentionDays, v => settings.CompletedRuntimeRetentionDays = v);
        ApplyInt(request.FailedRuntimeRetentionDays, v => settings.FailedRuntimeRetentionDays = v);
        ApplyInt(request.BuildLogRetentionDays, v => settings.BuildLogRetentionDays = v);
        ApplyBool(request.RemoveContainersAfterCompletion, v => settings.RemoveContainersAfterCompletion = v);
        ApplyBool(request.RemoveWorkspacesAfterCompletion, v => settings.RemoveWorkspacesAfterCompletion = v);

        settings.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static void ApplyBool(bool? value, Action<bool> apply)
    {
        if (value is not null) apply(value.Value);
    }

    private static void ApplyInt(int? value, Action<int> apply)
    {
        if (value is not null) apply(value.Value);
    }

    private static void ApplyString(string? value, Action<string> apply)
    {
        if (!string.IsNullOrWhiteSpace(value)) apply(value);
    }

    private static void ApplyOptionalString(string? value, Action<string> apply)
    {
        if (value is not null) apply(value);
    }

    private static AgentRuntimeSettingsResponse ToResponse(AgentRuntimeGlobalSettings settings)
    {
        return new AgentRuntimeSettingsResponse(
            settings.Id,
            settings.EnableImportedAgents,
            settings.DefaultActivationMode.ToString()!,
            settings.DefaultTickFrequencySeconds,
            settings.MinimumTickFrequencySeconds,
            settings.DefaultMaxRuntimeSeconds,
            settings.DefaultOverlapPolicy.ToString()!,
            settings.AllowAlwaysOnCommunityAgents,
            settings.DefaultRestartPolicy.ToString()!,
            settings.GlobalMaxActiveContainers,
            settings.PerBusinessMaxActiveContainers,
            settings.PerInstallationMaxActiveContainers,
            settings.DefaultContainerMemoryMb,
            settings.MaximumContainerMemoryMb,
            settings.DefaultContainerCpuPercent,
            settings.MaximumContainerCpuPercent,
            settings.DefaultContainerPidsLimit,
            settings.DefaultContainerLogLimitMb,
            settings.ContainerStartTimeoutSeconds,
            settings.McpSessionTimeoutSeconds,
            settings.ContainerStopGraceSeconds,
            settings.DefaultNetworkPolicy,
            settings.AllowPublicInternetByDefault,
            settings.AllowedPackageFeedHosts,
            settings.BlockedNetworkCidrs,
            settings.AgentSourceRootPath,
            settings.AgentPackageCachePath,
            settings.DotNetBuilderImage,
            settings.DotNetRuntimeBaseImage,
            settings.BuildTimeoutSeconds,
            settings.BuildMemoryMb,
            settings.BuildCpuPercent,
            settings.MaximumRepositorySizeMb,
            settings.MaximumBuildLogMb,
            settings.KeepFailedBuildWorkspaces,
            settings.CompletedRuntimeRetentionDays,
            settings.FailedRuntimeRetentionDays,
            settings.BuildLogRetentionDays,
            settings.RemoveContainersAfterCompletion,
            settings.RemoveWorkspacesAfterCompletion,
            settings.UpdatedAt);
    }

    private static ActivationMode ParseActivationMode(string value)
    {
        return value switch
        {
            "AlwaysOn" => ActivationMode.AlwaysOn,
            "Periodic" => ActivationMode.Periodic,
            "Manual" => ActivationMode.Manual,
            _ => throw new ArgumentException($"Invalid activation mode: {value}")
        };
    }

    private static OverlapPolicy ParseOverlapPolicy(string value)
    {
        return value switch
        {
            "Skip" => OverlapPolicy.Skip,
            "Queue" => OverlapPolicy.Queue,
            "CancelPrevious" => OverlapPolicy.CancelPrevious,
            _ => throw new ArgumentException($"Invalid overlap policy: {value}")
        };
    }

    private static RestartPolicy ParseRestartPolicy(string value)
    {
        return value switch
        {
            "Never" => RestartPolicy.Never,
            "OnFailure" => RestartPolicy.OnFailure,
            "Always" => RestartPolicy.Always,
            _ => throw new ArgumentException($"Invalid restart policy: {value}")
        };
    }
}
