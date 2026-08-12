using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CSweet.AgentRuntime.Abstractions;
using CSweet.AgentRuntime.Core;

namespace CSweet.AgentRuntime.Firecracker.Helper;

internal sealed class FirecrackerHelperController(FirecrackerHelperPaths paths)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan CreationGracePeriod = TimeSpan.FromMinutes(5);
    private const string GuestChannelTransport = "stdio-duplex-v1";

    public async Task<PlatformHelperResponse> ExecuteAsync(
        string operation,
        PlatformHelperRequest request,
        CancellationToken cancellationToken = default) =>
        operation switch
        {
            "probe" => await ProbeAsync(cancellationToken),
            "create" => await CreateAsync(request, cancellationToken),
            "start" => await StartAsync(request, cancellationToken),
            "inspect" => await InspectAsync(request, cancellationToken),
            "stop" => await StopAsync(request, cancellationToken),
            "destroy" => await DestroyAsync(request, cancellationToken),
            "reap" => await ReapAsync(cancellationToken),
            "logs" => await LogsAsync(request, cancellationToken),
            _ => Failure("unsupported-operation", "The requested helper operation is not supported.")
        };

    public async Task<GuestChannelOpenResult> OpenGuestChannelAsync(
        PlatformHelperRequest request,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(request.Handle, cancellationToken);
        if (loaded.Error is not null) return new(loaded.Error, null);
        if (loaded.Metadata!.StartedAt is null || !IsOwnedProcess(loaded.Metadata.ProcessId, loaded.Metadata.JailRoot))
            return new(Failure("workload-not-running", "The Firecracker workload is not running."), null);
        var socketPath = Path.Combine(loaded.Metadata.JailRoot, "run", "guest.vsock");
        try
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
                var stream = new NetworkStream(socket, ownsSocket: true);
                var requestBytes = Encoding.ASCII.GetBytes($"CONNECT {paths.GuestVsockPort}\n");
                await stream.WriteAsync(requestBytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                var acknowledgement = await ReadAsciiLineAsync(stream, 128, cancellationToken);
                if (!acknowledgement.StartsWith("OK ", StringComparison.Ordinal) ||
                    !uint.TryParse(acknowledgement.AsSpan(3), out _))
                {
                    await stream.DisposeAsync();
                    return new(Failure("broker-connect-rejected", "The Firecracker guest rejected the broker connection."), null);
                }
                return new(new PlatformHelperResponse
                {
                    Success = true,
                    GuestChannelTransport = GuestChannelTransport
                }, stream);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException)
        {
            return new(Failure("broker-connect-failed", "The Firecracker guest broker channel is unavailable."), null);
        }
    }

    private async Task<PlatformHelperResponse> ProbeAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) return Failure("unsupported-host", "Firecracker/KVM requires Linux.");
        if (!File.Exists("/sys/fs/cgroup/cgroup.controllers"))
            return Failure("cgroup-v2-required", "Firecracker requires a writable cgroup v2 hierarchy.");
        if (!IsTrustedExecutable(paths.FirecrackerExecutable) || !IsTrustedExecutable(paths.JailerExecutable))
            return Failure("firecracker-not-installed", "Pinned Firecracker and jailer executables are required.");
        if (!IsTrustedReadableFile(paths.KernelImage) || !IsTrustedReadableFile(paths.InitrdImage))
            return Failure("kernel-not-installed", "The pinned Firecracker guest kernel and initrd are required.");
        if (!IsProtectedDirectory(paths.DataRoot))
            return Failure("data-root-unavailable", "The Firecracker data root must be a protected root-only directory.");
        try
        {
            await using var kvm = new FileStream("/dev/kvm", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            var firecrackerVersion = await ReadToolVersionAsync(paths.FirecrackerExecutable, cancellationToken);
            var jailerVersion = await ReadToolVersionAsync(paths.JailerExecutable, cancellationToken);
            if (firecrackerVersion is null || !string.Equals(firecrackerVersion, jailerVersion, StringComparison.Ordinal))
                return Failure("firecracker-version-mismatch", "Firecracker and jailer must report the same pinned version.");
            return new PlatformHelperResponse { Success = true, GuestChannelTransport = GuestChannelTransport };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure("kvm-unavailable", "RuntimeHost cannot open /dev/kvm for Firecracker.");
        }
    }

    private async Task<PlatformHelperResponse> CreateAsync(
        PlatformHelperRequest request,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) return Failure("unsupported-host", "Firecracker/KVM requires Linux.");
        if (!IsTrustedExecutable(paths.FirecrackerExecutable) || !IsTrustedExecutable(paths.JailerExecutable) ||
            !IsTrustedReadableFile(paths.KernelImage) || !IsTrustedReadableFile(paths.InitrdImage))
            return Failure("firecracker-unavailable", "The pinned Firecracker, jailer, kernel, and initrd files are unavailable.");
        var workload = (IsolationWorkloadSpec?)request.BuilderWorkload ?? request.RuntimeWorkload;
        if (workload is null || request.BuilderWorkload is not null && request.RuntimeWorkload is not null)
            return Failure("invalid-workload", "Exactly one typed workload must be supplied.");
        try { workload.ResourceLimits.Validate(); }
        catch (ArgumentOutOfRangeException) { return Failure("invalid-resources", "The workload resource limits are invalid."); }
        if (!TryResolveGuestImage(request.GuestImagePath, out var guestImage))
            return Failure("invalid-guest-image", "The configured Firecracker root filesystem is invalid.");
        string? artifactImage = null;
        if (workload is RuntimeWorkloadSpec runtime)
        {
            if (!TryResolveArtifactImage(request.ArtifactImagePath, runtime.Artifact.Digest, out artifactImage) ||
                !await SingleFileIso9660.VerifyArtifactDigestAsync(artifactImage, runtime.Artifact.Digest, cancellationToken))
                return Failure("invalid-artifact-media", "The runtime artifact media failed its path or integrity check.");
        }
        else if (!string.IsNullOrWhiteSpace(request.ArtifactImagePath))
        {
            return Failure("invalid-artifact-media", "Builder workloads cannot attach runtime artifact media.");
        }

        var instanceId = Guid.NewGuid();
        var jailId = $"csweet-{instanceId:N}";
        var instanceDirectory = paths.InstanceDirectory(instanceId);
        EnsureProtectedDirectory(paths.DataRoot);
        EnsureProtectedDirectory(paths.InstancesRoot);
        EnsureProtectedDirectory(paths.JailerRoot);
        EnsureProtectedDirectory(instanceDirectory);
        FirecrackerInstanceMetadata? metadata = null;
        try
        {
            await LaunchJailerAsync(jailId, workload.ResourceLimits, cancellationToken);
            var jailRoot = paths.JailDirectory(jailId);
            var apiSocket = Path.Combine(jailRoot, "run", "firecracker.socket");
            await WaitForFileAsync(apiSocket, TimeSpan.FromSeconds(10), cancellationToken);
            StageReadOnly(guestImage, Path.Combine(jailRoot, "rootfs.ext4"));
            StageReadOnly(paths.KernelImage, Path.Combine(jailRoot, "vmlinux"));
            StageReadOnly(paths.InitrdImage, Path.Combine(jailRoot, "initrd.img"));
            var scratch = Path.Combine(jailRoot, "scratch.raw");
            await CreateScratchAsync(scratch, workload.ResourceLimits.WritableDiskMegabytes, cancellationToken);
            if (artifactImage is not null) StageReadOnly(artifactImage, Path.Combine(jailRoot, "artifact.iso"));
            var processId = await ReadProcessIdAsync(
                Path.Combine(jailRoot, "firecracker.pid"), jailRoot, cancellationToken);
            PrepareLogFile(Path.Combine(jailRoot, "run", "firecracker.log"));
            PrepareLogFile(Path.Combine(jailRoot, "run", "console.log"));
            var guestCid = GuestCid(instanceId);
            await ConfigureAsync(apiSocket, workload, artifactImage is not null, guestCid, cancellationToken);
            metadata = new FirecrackerInstanceMetadata(
                instanceId, workload.WorkloadId, workload.Kind, jailId, jailRoot, processId, guestCid,
                DateTimeOffset.UtcNow, null, null, workload.BrokerLease.ExpiresAt);
            await SaveMetadataAsync(instanceDirectory, metadata, cancellationToken);
            return new PlatformHelperResponse { Success = true, ProviderInstanceId = instanceId.ToString("N") };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            FirecrackerApiException or SocketException or TimeoutException or HelperProtocolException)
        {
            if (metadata is not null) Terminate(metadata.ProcessId, metadata.JailRoot, TimeSpan.Zero);
            else TryTerminateFromJail(jailId);
            DeleteInstance(instanceDirectory, jailId);
            return Failure("create-failed", "The jailed Firecracker workload could not be created.");
        }
    }

    private async Task<PlatformHelperResponse> StartAsync(PlatformHelperRequest request, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(request.Handle, cancellationToken);
        if (loaded.Error is not null) return loaded.Error;
        try
        {
            using var api = new FirecrackerApiClient(Path.Combine(loaded.Metadata!.JailRoot, "run", "firecracker.socket"));
            await api.PutAsync("/actions", new { action_type = "InstanceStart" }, cancellationToken);
            await SaveMetadataAsync(loaded.Directory!, loaded.Metadata with { StartedAt = DateTimeOffset.UtcNow }, cancellationToken);
            return Success();
        }
        catch (Exception exception) when (exception is HttpRequestException or FirecrackerApiException or IOException)
        {
            return Failure("start-failed", "Firecracker rejected the workload start request.");
        }
    }

    private async Task<PlatformHelperResponse> InspectAsync(PlatformHelperRequest request, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(request.Handle, cancellationToken);
        if (loaded.Error is not null) return loaded.Error;
        var metadata = loaded.Metadata!;
        if (!IsOwnedProcess(metadata.ProcessId, metadata.JailRoot))
            return new PlatformHelperResponse { Success = true, Status = Status(request.Handle!, metadata, IsolationWorkloadState.Stopped) };
        try
        {
            using var api = new FirecrackerApiClient(Path.Combine(metadata.JailRoot, "run", "firecracker.socket"));
            var state = await api.GetInstanceStateAsync(cancellationToken);
            return new PlatformHelperResponse { Success = true, Status = Status(request.Handle!, metadata, MapState(state)) };
        }
        catch (Exception exception) when (exception is HttpRequestException or FirecrackerApiException or IOException)
        {
            return Failure("inspect-failed", "The Firecracker workload state could not be read.");
        }
    }

    private async Task<PlatformHelperResponse> StopAsync(PlatformHelperRequest request, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(request.Handle, cancellationToken);
        if (loaded.Error is not null) return loaded.Error.ErrorCode == "not-found" ? Success() : loaded.Error;
        var grace = TimeSpan.FromSeconds(Math.Clamp(request.GracePeriodSeconds ?? 0, 0, 300));
        try
        {
            Terminate(loaded.Metadata!.ProcessId, loaded.Metadata.JailRoot, grace);
            await SaveMetadataAsync(loaded.Directory!, loaded.Metadata with { FinishedAt = DateTimeOffset.UtcNow }, cancellationToken);
            return Success();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure("stop-failed", "The Firecracker workload could not be stopped.");
        }
    }

    private async Task<PlatformHelperResponse> DestroyAsync(PlatformHelperRequest request, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(request.Handle, cancellationToken);
        if (loaded.Error is not null) return loaded.Error.ErrorCode == "not-found" ? Success() : loaded.Error;
        try
        {
            Terminate(loaded.Metadata!.ProcessId, loaded.Metadata.JailRoot, TimeSpan.Zero);
            DeleteInstance(loaded.Directory!, loaded.Metadata.JailId);
            return Success();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure("destroy-failed", "The Firecracker workload could not be destroyed.");
        }
    }

    private async Task<PlatformHelperResponse> LogsAsync(
        PlatformHelperRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Handle is null || request.MaximumBytes is < 1 or > 1024 * 1024)
            return Failure("invalid-request", "The bounded log request is invalid.");
        var loaded = await LoadAsync(request.Handle, cancellationToken);
        if (loaded.Error is not null) return loaded.Error;
        try
        {
            var maximum = request.MaximumBytes.GetValueOrDefault();
            var consoleMaximum = maximum * 3 / 4;
            var systemMaximum = maximum - consoleMaximum;
            var chunks = new List<IsolationLogChunk>(2);
            await AddLogAsync(chunks, Path.Combine(loaded.Metadata!.JailRoot, "run", "console.log"),
                "console", consoleMaximum, cancellationToken);
            await AddLogAsync(chunks, Path.Combine(loaded.Metadata.JailRoot, "run", "firecracker.log"),
                "provider", systemMaximum, cancellationToken);
            return new PlatformHelperResponse { Success = true, Logs = chunks };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure("logs-failed", "The bounded Firecracker logs could not be read.");
        }
    }

    private async Task<PlatformHelperResponse> ReapAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(paths.InstancesRoot)) return Success();
        var removed = 0;
        foreach (var directory in Directory.EnumerateDirectories(paths.InstancesRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = await TryReadMetadataAsync(directory, cancellationToken);
            if (metadata is null ||
                !Guid.TryParseExact(Path.GetFileName(directory), "N", out var directoryId) ||
                metadata.InstanceId != directoryId ||
                !string.Equals(metadata.JailId, $"csweet-{directoryId:N}", StringComparison.Ordinal) ||
                !string.Equals(metadata.JailRoot, paths.JailDirectory(metadata.JailId), StringComparison.Ordinal) ||
                metadata.Kind != IsolationWorkloadKind.Runtime ||
                !ShouldReap(metadata, DateTimeOffset.UtcNow)) continue;
            try
            {
                Terminate(metadata.ProcessId, metadata.JailRoot, TimeSpan.Zero);
                DeleteInstance(directory, metadata.JailId);
                removed++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return new PlatformHelperResponse { Success = true, WorkloadsRemoved = removed };
    }

    internal static bool ShouldReap(FirecrackerInstanceMetadata metadata, DateTimeOffset now) =>
        metadata.Kind == IsolationWorkloadKind.Runtime &&
        (metadata.LeaseExpiresAt is null || metadata.LeaseExpiresAt <= now || metadata.FinishedAt is not null ||
         !IsProcessRunning(metadata.ProcessId) ||
         metadata.StartedAt is null && metadata.CreatedAt <= now - CreationGracePeriod);

    internal static IReadOnlyList<string> BuildJailerArguments(
        FirecrackerHelperPaths paths,
        string jailId,
        IsolationResourceLimits limits)
    {
        var quota = Math.Max(1000L, limits.CpuPercent * 1000L);
        return
        [
            "--id", jailId,
            "--exec-file", paths.FirecrackerExecutable,
            "--uid", paths.WorkloadUid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--gid", paths.WorkloadGid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--chroot-base-dir", paths.JailerRoot,
            "--cgroup-version", "2",
            "--parent-cgroup", paths.ParentCgroup,
            "--cgroup", $"memory.max={(limits.MemoryMegabytes + 128L) * 1024L * 1024L}",
            "--cgroup", $"pids.max={limits.MaximumProcessCount}",
            "--cgroup", $"cpu.max={quota} 100000",
            "--resource-limit", "no-file=1024",
            "--daemonize",
            "--new-pid-ns",
            "--",
            "--api-sock", "/run/firecracker.socket"
        ];
    }

    private async Task LaunchJailerAsync(
        string jailId,
        IsolationResourceLimits limits,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = paths.JailerExecutable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in BuildJailerArguments(paths, jailId, limits)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("The Firecracker jailer could not be started.");
        var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, 16 * 1024, cancellationToken);
        var stderr = ReadBoundedAsync(process.StandardError.BaseStream, 16 * 1024, cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await stdout;
        await stderr;
        if (process.ExitCode != 0) throw new IOException("The Firecracker jailer rejected the workload.");
    }

    private async Task ConfigureAsync(
        string apiSocket,
        IsolationWorkloadSpec workload,
        bool hasArtifact,
        uint guestCid,
        CancellationToken cancellationToken)
    {
        using var api = new FirecrackerApiClient(apiSocket);
        await api.ProbeAsync(cancellationToken);
        await api.PutAsync("/logger", new
        {
            log_path = "/run/firecracker.log", level = "Info",
            show_level = true, show_log_origin = false
        }, cancellationToken);
        await api.PutAsync("/serial", new { serial_out_path = "/run/console.log" }, cancellationToken);
        await api.PutAsync("/machine-config", new
        {
            vcpu_count = workload.ResourceLimits.VirtualCpuCount,
            mem_size_mib = workload.ResourceLimits.MemoryMegabytes,
            smt = false,
            track_dirty_pages = false
        }, cancellationToken);
        await api.PutAsync("/boot-source", new
        {
            kernel_image_path = "/vmlinux",
            initrd_path = "/initrd.img",
            boot_args = "console=ttyS0 reboot=k panic=1 root=/dev/vda ro systemd.volatile=state"
        }, cancellationToken);
        await api.PutAsync("/drives/rootfs", new
        {
            drive_id = "rootfs", path_on_host = "/rootfs.ext4", is_root_device = true, is_read_only = true
        }, cancellationToken);
        await api.PutAsync("/drives/scratch", new
        {
            drive_id = "scratch", path_on_host = "/scratch.raw", is_root_device = false, is_read_only = false
        }, cancellationToken);
        if (hasArtifact)
            await api.PutAsync("/drives/artifact", new
            {
                drive_id = "artifact", path_on_host = "/artifact.iso", is_root_device = false, is_read_only = true
            }, cancellationToken);
        await api.PutAsync("/vsock", new { guest_cid = guestCid, uds_path = "/run/guest.vsock" }, cancellationToken);
    }

    private async Task<(FirecrackerInstanceMetadata? Metadata, string? Directory, PlatformHelperResponse? Error)> LoadAsync(
        IsolationWorkloadHandle? handle,
        CancellationToken cancellationToken)
    {
        if (handle is null ||
            !string.Equals(handle.ProviderId, IsolationProviderCatalog.Firecracker().ProviderId, StringComparison.Ordinal) ||
            !Guid.TryParseExact(handle.ProviderInstanceId, "N", out var instanceId))
            return (null, null, Failure("invalid-handle", "The Firecracker workload handle is invalid."));
        var directory = paths.InstanceDirectory(instanceId);
        var metadata = await TryReadMetadataAsync(directory, cancellationToken);
        if (metadata is null) return (null, null, Failure("not-found", "The Firecracker workload was not found."));
        if (metadata.InstanceId != instanceId || metadata.WorkloadId != handle.WorkloadId || metadata.Kind != handle.Kind ||
            !string.Equals(metadata.JailId, $"csweet-{instanceId:N}", StringComparison.Ordinal) ||
            !string.Equals(metadata.JailRoot, paths.JailDirectory(metadata.JailId), StringComparison.Ordinal))
            return (null, null, Failure("invalid-metadata", "The Firecracker workload metadata failed validation."));
        return (metadata, directory, null);
    }

    private static async Task<FirecrackerInstanceMetadata?> TryReadMetadataAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var metadataPath = Path.Combine(directory, "instance.json");
        try
        {
            if (!File.Exists(metadataPath) || new FileInfo(metadataPath).Length is < 2 or > 64 * 1024) return null;
            await using var stream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<FirecrackerInstanceMetadata>(stream, JsonOptions, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static async Task SaveMetadataAsync(
        string directory,
        FirecrackerInstanceMetadata metadata,
        CancellationToken cancellationToken)
    {
        var destination = Path.Combine(directory, "instance.json");
        var temporary = Path.Combine(directory, $"instance-{Guid.NewGuid():N}.tmp");
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            await JsonSerializer.SerializeAsync(stream, metadata, JsonOptions, cancellationToken);
        File.Move(temporary, destination, overwrite: true);
        SetUnixMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void StageReadOnly(string source, string destination)
    {
        File.Copy(source, destination, overwrite: false);
        SetUnixMode(destination, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    private void PrepareLogFile(string path)
    {
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite)) { }
        UnixOwnership.Change(path, paths.WorkloadUid, paths.WorkloadGid);
        SetUnixMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead);
    }

    private static async Task AddLogAsync(
        ICollection<IsolationLogChunk> chunks,
        string path,
        string streamName,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (maximumBytes <= 0 || !File.Exists(path)) return;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var truncated = stream.Length > maximumBytes;
        if (truncated) stream.Seek(-maximumBytes, SeekOrigin.End);
        var bytes = new byte[Math.Min(maximumBytes, checked((int)Math.Min(stream.Length, int.MaxValue)))];
        var total = 0;
        while (total < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(total), cancellationToken);
            if (read == 0) break;
            total += read;
        }
        if (total != bytes.Length) Array.Resize(ref bytes, total);
        chunks.Add(new IsolationLogChunk(DateTimeOffset.UtcNow, streamName, bytes, truncated));
    }

    private async Task CreateScratchAsync(string path, int megabytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        stream.SetLength(megabytes * 1024L * 1024L);
        await stream.FlushAsync(cancellationToken);
        UnixOwnership.Change(path, paths.WorkloadUid, paths.WorkloadGid);
        SetUnixMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private async Task<int> ReadProcessIdAsync(
        string path,
        string jailRoot,
        CancellationToken cancellationToken)
    {
        await WaitForFileAsync(path, TimeSpan.FromSeconds(5), cancellationToken);
        var value = (await File.ReadAllTextAsync(path, cancellationToken)).Trim();
        if (!int.TryParse(value, out var processId) || processId <= 1 || !IsOwnedProcess(processId, jailRoot))
            throw new IOException("The Firecracker jailer returned an invalid process identifier.");
        return processId;
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (started.Elapsed >= timeout) throw new TimeoutException("Firecracker did not create its control socket in time.");
            await Task.Delay(25, cancellationToken);
        }
    }

    private static bool TryResolveGuestImage(string? value, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return false;
        path = Path.GetFullPath(value);
        var extension = Path.GetExtension(path);
        return File.Exists(path) && (extension.Equals(".ext4", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".raw", StringComparison.OrdinalIgnoreCase));
    }

    private bool TryResolveArtifactImage(string? value, string digest, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) || !IsSha256(digest)) return false;
        var root = Path.GetFullPath(paths.ArtifactMediaRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        path = Path.GetFullPath(value);
        return path.StartsWith(root, StringComparison.Ordinal) &&
            string.Equals(Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal) &&
            string.Equals(Path.GetFileName(path), $"{digest[7..]}.iso", StringComparison.Ordinal) && File.Exists(path);
    }

    private void TryTerminateFromJail(string jailId)
    {
        var pidPath = Path.Combine(paths.JailDirectory(jailId), "firecracker.pid");
        try
        {
            if (int.TryParse(File.ReadAllText(pidPath).Trim(), out var processId))
                Terminate(processId, paths.JailDirectory(jailId), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private void DeleteInstance(string instanceDirectory, string jailId)
    {
        var resolvedInstance = paths.InstanceDirectory(Guid.ParseExact(Path.GetFileName(instanceDirectory), "N"));
        var resolvedJail = paths.JailDirectory(jailId);
        if (Directory.Exists(resolvedInstance)) Directory.Delete(resolvedInstance, recursive: true);
        if (Directory.Exists(resolvedJail)) Directory.Delete(Path.Combine(paths.JailerRoot, "firecracker", jailId), recursive: true);
    }

    private static void Terminate(int processId, string jailRoot, TimeSpan grace)
    {
        if (!IsOwnedProcess(processId, jailRoot)) return;
        UnixSignal.Terminate(processId);
        using var process = Process.GetProcessById(processId);
        if (grace > TimeSpan.Zero && process.WaitForExit((int)Math.Min(grace.TotalMilliseconds, int.MaxValue))) return;
        if (!process.HasExited) process.Kill(entireProcessTree: true);
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            if (processId <= 1) return false;
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
    }

    private static bool IsOwnedProcess(int processId, string jailRoot)
    {
        if (!OperatingSystem.IsLinux() || !IsProcessRunning(processId)) return false;
        try
        {
            var processRoot = new DirectoryInfo($"/proc/{processId}/root").ResolveLinkTarget(returnFinalTarget: true);
            return processRoot is not null && string.Equals(
                Path.GetFullPath(processRoot.FullName).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(jailRoot).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool IsTrustedExecutable(string path) => IsTrustedReadableFile(path) &&
        (GetUnixMode(path) & UnixFileMode.UserExecute) != 0;

    private static bool IsTrustedReadableFile(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) return false;
        var mode = GetUnixMode(path);
        return (mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0;
    }

    private static bool IsProtectedDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)) return false;
        var info = new DirectoryInfo(path);
        if (info.LinkTarget is not null) return false;
        var mode = GetUnixMode(path);
        return (mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0;
    }

    private static void EnsureProtectedDirectory(string path)
    {
        Directory.CreateDirectory(path);
        var info = new DirectoryInfo(path);
        if (info.LinkTarget is not null)
            throw new IOException("A Firecracker protected directory cannot be a symbolic link.");
        SetUnixMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static UnixFileMode GetUnixMode(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        return File.GetUnixFileMode(path);
    }

    private static void SetUnixMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        File.SetUnixFileMode(path, mode);
    }

    private static async Task<string?> ReadToolVersionAsync(string executable, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--version");
        using var process = Process.Start(start);
        if (process is null) return null;
        var output = ReadBoundedAsync(process.StandardOutput.BaseStream, 16 * 1024, cancellationToken);
        var error = ReadBoundedAsync(process.StandardError.BaseStream, 16 * 1024, cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await output;
        var stderr = await error;
        if (process.ExitCode != 0) return null;
        return ExtractVersion(Encoding.UTF8.GetString(stdout) + " " + Encoding.UTF8.GetString(stderr));
    }

    internal static string? ExtractVersion(string value)
    {
        var match = Regex.Match(value, @"(?<![0-9])([0-9]+\.[0-9]+\.[0-9]+)(?![0-9])",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return match.Success ? match.Groups[1].Value : null;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var remaining = maximumBytes + 1 - (int)output.Length;
            if (remaining <= 0) throw new IOException("A Firecracker helper subprocess exceeded its output limit.");
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0) return output.ToArray();
            output.Write(buffer, 0, read);
        }
    }

    internal static async Task<string> ReadAsciiLineAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using var line = new MemoryStream();
        var single = new byte[1];
        while (line.Length <= maximumBytes)
        {
            var read = await stream.ReadAsync(single, cancellationToken);
            if (read == 0) throw new EndOfStreamException("The Firecracker vsock handshake ended unexpectedly.");
            if (single[0] == (byte)'\n') return Encoding.ASCII.GetString(line.ToArray());
            if (single[0] is 0 or (byte)'\r' || single[0] > 0x7f)
                throw new InvalidDataException("The Firecracker vsock handshake framing is invalid.");
            line.WriteByte(single[0]);
        }
        throw new InvalidDataException("The Firecracker vsock handshake exceeded its limit.");
    }

    private static uint GuestCid(Guid instanceId)
    {
        var value = BitConverter.ToUInt32(instanceId.ToByteArray(), 0) & 0x7fff_ffff;
        return value < 3 ? value + 3 : value;
    }

    private static bool IsSha256(string value) => value.Length == 71 &&
        value.StartsWith("sha256:", StringComparison.Ordinal) && value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static IsolationWorkloadStatus Status(
        IsolationWorkloadHandle handle,
        FirecrackerInstanceMetadata metadata,
        IsolationWorkloadState state) =>
        new(handle, state, IsolationTerminationReason.None, null,
            metadata.StartedAt, metadata.FinishedAt, null, null);

    private static IsolationWorkloadState MapState(string state) => state switch
    {
        "Running" => IsolationWorkloadState.Running,
        "Paused" => IsolationWorkloadState.Running,
        "Not started" => IsolationWorkloadState.Created,
        _ => IsolationWorkloadState.Failed
    };

    private static PlatformHelperResponse Success() => new() { Success = true };
    private static PlatformHelperResponse Failure(string code, string message) => new()
    {
        Success = false,
        ErrorCode = code,
        SanitizedError = message
    };
}

internal sealed record FirecrackerInstanceMetadata(
    Guid InstanceId,
    Guid WorkloadId,
    IsolationWorkloadKind Kind,
    string JailId,
    string JailRoot,
    int ProcessId,
    uint GuestCid,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset? LeaseExpiresAt);

internal sealed record GuestChannelOpenResult(PlatformHelperResponse Response, Stream? Stream);

internal static class UnixOwnership
{
    public static void Change(string path, uint uid, uint gid)
    {
        if (chown(path, uid, gid) != 0)
            throw new IOException("The Firecracker workload file ownership could not be secured.");
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "chown")]
    private static extern int chown(string path, uint owner, uint group);
}

internal static class UnixSignal
{
    private const int SigTerm = 15;

    public static void Terminate(int processId)
    {
        if (kill(processId, SigTerm) != 0 && Marshal.GetLastPInvokeError() != 3)
            throw new IOException("The Firecracker workload could not be terminated.");
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "kill")]
    private static extern int kill(int processId, int signal);
}
