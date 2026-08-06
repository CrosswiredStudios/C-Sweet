using System.Text.Json;
using CSweet.AgentRuntime.Abstractions;
using CSweet.AgentRuntime.Core;
using CSweet.AgentRuntime.HyperV;

namespace CSweet.AgentRuntime.HyperV.Helper;

internal sealed class HyperVHelperController(HyperVHelperPaths paths)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PlatformHelperResponse> ExecuteAsync(string operation, PlatformHelperRequest request) =>
        operation switch
        {
            "probe" => await ProbeAsync(),
            "create" => await CreateAsync(request),
            "start" => await StartAsync(request),
            "inspect" => await InspectAsync(request),
            "stop" => await StopAsync(request),
            "destroy" => await DestroyAsync(request),
            "logs" => Logs(request),
            _ => Failure("unsupported-operation", "The requested helper operation is not supported.")
        };

    private static async Task<PlatformHelperResponse> ProbeAsync()
    {
        if (!OperatingSystem.IsWindows()) return Failure("unsupported-host", "Hyper-V requires Windows.");
        var host = await new WindowsHyperVHostProbe().ProbeAsync();
        if (!host.IsSupportedEdition) return Failure("unsupported-edition", "The installed Windows edition does not include Hyper-V.");
        if (!host.HardwareRequirementsSatisfied) return Failure("hardware-requirements", "The Hyper-V hardware requirements are not satisfied.");
        if (host.FeatureState is not WindowsOptionalFeatureState.Enabled && !host.IsHypervisorPresent)
            return Failure("hyperv-disabled", "The Hyper-V Windows feature is not enabled.");
        if (!host.IsHypervisorPresent) return Failure("restart-required", "Restart Windows to start the Hyper-V hypervisor.");
        if (!HyperVSocketRegistration.IsConfigured())
            return Failure("broker-transport-unavailable", "The C-Sweet Hyper-V socket service is not registered.");
        try
        {
            await PowerShellHyperV.RunAsync(
                "Import-Module Hyper-V -ErrorAction Stop; $null = Get-Command New-VM -ErrorAction Stop; $null = Get-VMHost -ErrorAction Stop");
            return Success();
        }
        catch (HyperVCommandException exception)
        {
            return Failure(exception.ErrorCode, exception.Message);
        }
    }

    private async Task<PlatformHelperResponse> CreateAsync(PlatformHelperRequest request)
    {
        if (!OperatingSystem.IsWindows()) return Failure("unsupported-host", "Hyper-V requires Windows.");
        var workload = (IsolationWorkloadSpec?)request.BuilderWorkload ?? request.RuntimeWorkload;
        if (workload is null || request.BuilderWorkload is not null && request.RuntimeWorkload is not null)
            return Failure("invalid-workload", "Exactly one typed workload must be supplied.");
        workload.ResourceLimits.Validate();
        if (!TryResolveGuestImage(request.GuestImagePath, out var guestImage))
            return Failure("invalid-guest-image", "The configured guest image must be an existing absolute VHDX path.");
        string? artifactImage = null;
        if (workload is RuntimeWorkloadSpec runtime)
        {
            if (!TryResolveArtifactImage(
                    request.ArtifactImagePath, runtime.Artifact.Digest, paths.ArtifactMediaRoot,
                    out artifactImage) ||
                !await SingleFileIso9660.VerifyArtifactDigestAsync(
                    artifactImage, runtime.Artifact.Digest))
                return Failure("invalid-artifact-media", "The runtime artifact media is missing, outside its approved root, or failed integrity validation.");
        }
        else if (!string.IsNullOrWhiteSpace(request.ArtifactImagePath))
        {
            return Failure("invalid-artifact-media", "Builder workloads cannot attach runtime artifact media.");
        }

        var creationId = Guid.NewGuid();
        var vmName = $"CSweet-{workload.Kind}-{creationId:N}";
        Guid instanceId = Guid.Empty;
        string? instanceDirectory = null;

        try
        {
            Directory.CreateDirectory(paths.VmConfigurationRoot);
            var hyperVId = await PowerShellHyperV.CreateShellAsync(
                vmName,
                paths.VmConfigurationRoot,
                workload.ResourceLimits.MemoryMegabytes);
            if (!Guid.TryParse(hyperVId, out instanceId) || instanceId == Guid.Empty)
                throw new HyperVCommandException("invalid-vm-id", "Hyper-V returned an invalid VM identifier.");
            instanceDirectory = paths.InstanceDirectory(instanceId);
            Directory.CreateDirectory(instanceDirectory);
            var osDisk = Path.Combine(instanceDirectory, "os-diff.vhdx");
            var scratchDisk = Path.Combine(instanceDirectory, "scratch.vhdx");
            await PowerShellHyperV.ConfigureAsync(
                vmName,
                guestImage,
                osDisk,
                scratchDisk,
                workload.ResourceLimits.VirtualCpuCount,
                workload.ResourceLimits.CpuPercent,
                workload.ResourceLimits.MemoryMegabytes,
                workload.ResourceLimits.WritableDiskMegabytes,
                artifactImage);
            var metadata = new HyperVInstanceMetadata(
                instanceId, workload.WorkloadId, workload.Kind, vmName,
                DateTimeOffset.UtcNow, null, null);
            await SaveMetadataAsync(instanceDirectory, metadata);
            return new PlatformHelperResponse
            {
                Success = true,
                ProviderInstanceId = instanceId.ToString("N")
            };
        }
        catch (Exception exception) when (exception is HyperVCommandException or IOException or UnauthorizedAccessException)
        {
            await BestEffortRemoveVmAsync(vmName);
            if (instanceDirectory is not null) DeleteInstanceDirectory(instanceDirectory);
            return Failure(exception is HyperVCommandException command ? command.ErrorCode : "create-failed",
                exception is HyperVCommandException ? exception.Message : "The Hyper-V workload could not be created.");
        }
    }

    private async Task<PlatformHelperResponse> StartAsync(PlatformHelperRequest request)
    {
        var loaded = await LoadAsync(request.Handle);
        if (loaded.Error is not null) return loaded.Error;
        try
        {
            await PowerShellHyperV.StartAsync(loaded.Metadata!.VmName);
            await SaveMetadataAsync(loaded.Directory!, loaded.Metadata with { StartedAt = DateTimeOffset.UtcNow });
            return Success();
        }
        catch (HyperVCommandException exception) { return Failure(exception.ErrorCode, exception.Message); }
    }

    private async Task<PlatformHelperResponse> InspectAsync(PlatformHelperRequest request)
    {
        var loaded = await LoadAsync(request.Handle);
        if (loaded.Error is not null) return loaded.Error;
        try
        {
            var state = await PowerShellHyperV.GetStateAsync(loaded.Metadata!.VmName);
            return new PlatformHelperResponse
            {
                Success = true,
                Status = Status(request.Handle!, loaded.Metadata, MapState(state))
            };
        }
        catch (HyperVCommandException exception) when (exception.ErrorCode == "not-found")
        {
            return Failure("not-found", "The Hyper-V workload was not found.");
        }
        catch (HyperVCommandException exception) { return Failure(exception.ErrorCode, exception.Message); }
    }

    private async Task<PlatformHelperResponse> StopAsync(PlatformHelperRequest request)
    {
        var loaded = await LoadAsync(request.Handle);
        if (loaded.Error is not null) return loaded.Error;
        try
        {
            await PowerShellHyperV.StopAsync(loaded.Metadata!.VmName);
            await SaveMetadataAsync(loaded.Directory!, loaded.Metadata with { FinishedAt = DateTimeOffset.UtcNow });
            return Success();
        }
        catch (HyperVCommandException exception) { return Failure(exception.ErrorCode, exception.Message); }
    }

    private async Task<PlatformHelperResponse> DestroyAsync(PlatformHelperRequest request)
    {
        var loaded = await LoadAsync(request.Handle);
        if (loaded.Error is not null)
            return loaded.Error.ErrorCode == "not-found" ? Success() : loaded.Error;
        try
        {
            await PowerShellHyperV.DestroyAsync(loaded.Metadata!.VmName);
            DeleteInstanceDirectory(loaded.Directory!);
            return Success();
        }
        catch (HyperVCommandException exception) { return Failure(exception.ErrorCode, exception.Message); }
    }

    private static PlatformHelperResponse Logs(PlatformHelperRequest request)
    {
        if (request.Handle is null || request.MaximumBytes is < 1 or > 1024 * 1024)
            return Failure("invalid-request", "The bounded log request is invalid.");
        return new PlatformHelperResponse { Success = true, Logs = [] };
    }

    private async Task<(HyperVInstanceMetadata? Metadata, string? Directory, PlatformHelperResponse? Error)> LoadAsync(
        IsolationWorkloadHandle? handle)
    {
        if (handle is null || !string.Equals(handle.ProviderId, IsolationProviderCatalog.HyperV().ProviderId, StringComparison.Ordinal) ||
            !Guid.TryParseExact(handle.ProviderInstanceId, "N", out var instanceId))
            return (null, null, Failure("invalid-handle", "The Hyper-V workload handle is invalid."));
        var directory = paths.InstanceDirectory(instanceId);
        var metadataPath = Path.Combine(directory, "instance.json");
        if (!File.Exists(metadataPath)) return (null, null, Failure("not-found", "The Hyper-V workload was not found."));
        try
        {
            var metadata = JsonSerializer.Deserialize<HyperVInstanceMetadata>(
                await File.ReadAllTextAsync(metadataPath), JsonOptions);
            if (metadata is null || metadata.InstanceId != instanceId || metadata.WorkloadId != handle.WorkloadId || metadata.Kind != handle.Kind)
                return (null, null, Failure("invalid-metadata", "The Hyper-V workload metadata failed validation."));
            return (metadata, directory, null);
        }
        catch (JsonException)
        {
            return (null, null, Failure("invalid-metadata", "The Hyper-V workload metadata failed validation."));
        }
    }

    private static async Task SaveMetadataAsync(string directory, HyperVInstanceMetadata metadata)
    {
        await File.WriteAllTextAsync(Path.Combine(directory, "instance.json"),
            JsonSerializer.Serialize(metadata, JsonOptions));
    }

    private static bool TryResolveGuestImage(string? value, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return false;
        path = Path.GetFullPath(value);
        return Path.GetExtension(path).Equals(".vhdx", StringComparison.OrdinalIgnoreCase) && File.Exists(path);
    }

    private static bool TryResolveArtifactImage(
        string? value,
        string digest,
        string mediaRoot,
        out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) ||
            digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            digest.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") >= 0)
            return false;
        var root = Path.GetFullPath(mediaRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        path = Path.GetFullPath(value);
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFileName(path), $"{digest[7..]}.iso", StringComparison.Ordinal) &&
            File.Exists(path);
    }

    private static IsolationWorkloadStatus Status(
        IsolationWorkloadHandle handle,
        HyperVInstanceMetadata metadata,
        IsolationWorkloadState state) =>
        new(handle, state, IsolationTerminationReason.None, null,
            metadata.StartedAt, metadata.FinishedAt, null, null);

    private static IsolationWorkloadState MapState(string state) => state switch
    {
        "Off" => IsolationWorkloadState.Stopped,
        "Running" => IsolationWorkloadState.Running,
        "Starting" => IsolationWorkloadState.Starting,
        "Stopping" => IsolationWorkloadState.Stopping,
        _ => IsolationWorkloadState.Failed
    };

    private static async Task BestEffortRemoveVmAsync(string vmName)
    {
        try { await PowerShellHyperV.DestroyAsync(vmName); }
        catch (HyperVCommandException) { }
    }

    private void DeleteInstanceDirectory(string directory)
    {
        var resolved = paths.InstanceDirectory(Guid.ParseExact(Path.GetFileName(directory), "N"));
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }

    private static PlatformHelperResponse Success() => new() { Success = true };
    private static PlatformHelperResponse Failure(string code, string message) => new()
    {
        Success = false,
        ErrorCode = code,
        SanitizedError = message
    };
}

internal sealed record HyperVInstanceMetadata(
    Guid InstanceId,
    Guid WorkloadId,
    IsolationWorkloadKind Kind,
    string VmName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);
