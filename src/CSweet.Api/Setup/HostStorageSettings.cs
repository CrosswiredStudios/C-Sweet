using System.Text.Json;
using CSweet.Compute.Runtime;
using CSweet.Contracts.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.Win32;

namespace CSweet.Api.Setup;

internal static class HostStorageSettings
{
    private static string LocalRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CSweet");
    private static string PreferenceFile => Path.Combine(LocalRoot, "compute-storage.json");

    internal static string? ReadPreference(string path)
    {
        try
        {
            return (JsonSerializer.Deserialize<ComputeStoragePreference>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Invalid Compute storage preference.")).VolumeId;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static string? OfficeVolumeId()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\CSweet\Office\Storage");
        return key is null ? null : key.GetValue("VolumeId") as string
            ?? throw new InvalidDataException("Office storage registration is incomplete.");
    }

    internal static string? EffectiveComputeVolume() => ReadPreference(PreferenceFile) ?? OfficeVolumeId();

    internal static bool TrySave(string path, ComputeStoragePreference request, IReadOnlyList<LocalOfficeVolumeResponse> volumes)
    {
        if (request.VolumeId is not null && !volumes.Any(v => v.Id == request.VolumeId && v.IsEligible)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(request));
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
        return true;
    }

    internal static void MapHostStorage(this RouteGroupBuilder group)
    {
        group.MapGet("/storage", (bool? measure, IConfiguration config, IWebHostEnvironment host, HttpContext http, CancellationToken token) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(Report(config, host, measure == true, token));
        }).RequireAuthorization("HostAdministration");
        group.MapPut("/storage/compute", (ComputeStoragePreference request) =>
            OperatingSystem.IsWindows() && TrySave(PreferenceFile, request, LocalOfficeStorageProbe.GetWindowsVolumes(refresh: true))
                ? Results.NoContent() : Results.BadRequest("Choose an available local Windows volume."))
            .RequireAuthorization("HostAdministration");
    }

    private static HostStorageReport Report(IConfiguration config, IWebHostEnvironment host, bool measure, CancellationToken token)
    {
        var windows = OperatingSystem.IsWindows();
        var volumes = windows ? LocalOfficeStorageProbe.GetWindowsVolumes(false) : [];
        var preference = ReadPreference(PreferenceFile);
        var officeId = OfficeVolumeId();
        var locations = new List<HostStorageLocation>();
        var repo = config["CSweet:Compute:RepositoryRoot"] ?? Path.GetFullPath(Path.Combine(host.ContentRootPath, "..", ".."));
        var machine = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CSweet");
        if (windows)
        {
            var office = volumes.FirstOrDefault(v => v.Id == officeId || officeId == null && v.IsSystemVolume);
            if (office != null)
            {
                Add("Office programs and recovery images", office.InstallRoot, "Office setup");
                Add("Office VM disks", Path.Combine(office.DataRoot, "hyperv"), "Office setup");
                Add("Office downloads, builds and certification", Path.Combine(office.DataRoot, "setup"), "Office setup");
                Add("Office artifacts", Path.Combine(office.DataRoot, "artifacts"), "Office setup");
                Add("Office artifact media", Path.Combine(office.DataRoot, "artifact-media"), "Office setup");
                Add("Office node and download cache", Path.Combine(office.DataRoot, "node"), "Office setup");
            }
            Add("Legacy Compute image preparation", Path.Combine(repo, "artifacts", "compute", "mvp", "images"), "Development checkout");
            Add("Legacy Linux builder downloads and VM exports", Path.GetFullPath(Path.Combine(repo, "..", "CSweet.Isolation", "artifacts", "linux-images")), "Development checkout");
            Add("Legacy Compute base images", Path.Combine(machine, "ComputeImages"), "Compute installer");
            Add("Shared Compute provider and legacy workloads", Path.Combine(machine, "Compute"), "Compute installer");
            Add("Business Compute providers and legacy workloads", Path.Combine(machine, "ComputeBusinesses"), "Compute installer");
            foreach (var volume in volumes)
            {
                var root = ComputeVolumeStorage.Root(volume.MountPath[0]);
                if (volume.Id == (preference ?? officeId) || Directory.Exists(root))
                {
                    Add("Compute prepared images", Path.Combine(root, "prepared"), "Compute drive");
                    Add("Compute builder downloads and VM exports", Path.Combine(root, "build"), "Compute drive");
                    Add("Compute installed base images", Path.Combine(root, "base"), "Compute drive");
                    try
                    {
                        if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0)
                            foreach (var workloads in Directory.EnumerateDirectories(root, "workloads-*").Take(200))
                                Add("Enrolled provider VM disks", workloads, "Drive selected at enrollment");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { locations.Add(new("Enrolled provider VM disks", root, null, "Unavailable", "Drive selected at enrollment")); }
                }
            }
            Add("Office installer receipts", Path.Combine(machine, "Setup"), "Windows installer");
            Add("Docker Desktop default WSL data disk (shared)", Path.Combine(Path.GetDirectoryName(LocalRoot)!, "Docker", "wsl", "disk", "docker_data.vhdx"), "Docker Desktop", fileOnly: true);
            foreach (var disk in WslDisks()) Add($"WSL distribution · {disk.Name}", disk.Path, "WSL", fileOnly: true);
        }
        var runtime = config.GetSection(AgentRuntimeManagerOptions.SectionName).Get<AgentRuntimeManagerOptions>() ?? new();
        if (config.GetValue<bool>("CSweet:Storage:AppHostVolumes"))
        {
            AppHostVolume("Headquarters PostgreSQL", "csweet-aspire-postgres", "CSweet:Postgres:DataDirectory");
            AppHostVolume("Search cache", "csweet-searxng-cache", "CSweet:Search:Searxng:CacheDirectory");
            Add("Crash reports and process dumps", config["CSweet:Diagnostics:CrashDirectory"] ?? Path.Combine(LocalRoot, "crashes"), "CSweet:Diagnostics:CrashDirectory");
        }
        Add("Workspace snapshots", runtime.WorkspaceSnapshotStorePath, "CSweet:AgentRuntime:WorkspaceSnapshotStorePath");
        Add("Agent source archives", runtime.SourceArchiveStorePath, "CSweet:AgentRuntime:SourceArchiveStorePath");
        Add("Build logs", runtime.BuildLogStorePath, "CSweet:AgentRuntime:BuildLogStorePath");
        Add("Generated media", config["CSweet:GenAi:MediaRoot"] ?? Path.Combine(LocalRoot, "media"), "CSweet:GenAi:MediaRoot");
        if (!string.Equals(config["CSweet:AgentRuntime:Artifacts:Provider"], "s3", StringComparison.OrdinalIgnoreCase))
            Add("Agent artifacts", config["CSweet:AgentRuntime:Artifacts:RootPath"] ?? Path.Combine(windows ? machine : LocalRoot, "AgentRuntime", "artifacts"), "CSweet:AgentRuntime:Artifacts:RootPath");
        if (windows)
            Add("Compute installer logs and published utilities", Path.Combine(LocalRoot, "ComputeSetup"), "Local Windows profile");
        return new(windows, preference, officeId, volumes, locations);

        void AppHostVolume(string name, string volume, string key)
        {
            if (config[key] is { Length: > 0 } path) Add(name, path, key);
            else locations.Add(new(name, volume, null, "Docker volume", key));
        }

        void Add(string name, string path, string control, bool fileOnly = false)
        {
            token.ThrowIfCancellationRequested();
            var (bytes, status) = measure ? Measure(path, token, fileOnly) : (null, "Not measured");
            locations.Add(new(name, path, bytes, status, control));
        }
    }

    // This account's registered distro disks. Read host metadata only, without starting WSL.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IReadOnlyList<(string Name, string Path)> WslDisks()
    {
        var disks = new List<(string, string)>();
        try
        {
            using var registrations = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            if (registrations is null) return disks;
            foreach (var id in registrations.GetSubKeyNames().Take(64))
            {
                using var distro = registrations.OpenSubKey(id);
                if (distro?.GetValue("DistributionName") is not string name || distro.GetValue("BasePath") is not string path ||
                    distro.GetValue("Version") is 1) continue;
                var filename = distro.GetValue("VhdFileName") as string ?? "ext4.vhdx";
                if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
                if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || !Path.IsPathFullyQualified(path) ||
                    filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !filename.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase))
                    continue;
                disks.Add((name, Path.Combine(path, filename)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return disks;
    }

    // A user explicitly requests measurement. Count file lengths without reading contents or following links.
    internal static (long? Bytes, string Status) Measure(string path, CancellationToken token, bool fileOnly = false)
    {
        long bytes = 0;
        var entries = 0;
        var incomplete = false;
        try
        {
            var full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal) || Path.GetPathRoot(full) == full)
                return (null, "External or volume root");
            for (var parent = Directory.GetParent(full); parent != null; parent = parent.Parent)
                if ((File.GetAttributes(parent.FullName) & FileAttributes.ReparsePoint) != 0) return (null, "Linked path");
            var attributes = File.GetAttributes(full);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return (null, "Linked path");
            if ((attributes & FileAttributes.Directory) == 0) return (new FileInfo(full).Length, "Measured");
            if (fileOnly) return (null, "Unavailable");
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new(full));
            while (pending.TryPop(out var directory))
            {
                try
                {
                    foreach (var entry in directory.EnumerateFileSystemInfos())
                    {
                        token.ThrowIfCancellationRequested();
                        if (++entries > 50000) return (bytes, "Partial");
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) { incomplete = true; continue; }
                        if (entry is DirectoryInfo child) pending.Push(child);
                        else bytes += ((FileInfo)entry).Length;
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { incomplete = true; }
            }
            return incomplete && bytes == 0 ? (null, "Unavailable") : (bytes, incomplete ? "Partial" : "Measured");
        }
        catch (FileNotFoundException) { return (null, "Missing"); }
        catch (DirectoryNotFoundException) { return (null, "Missing"); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return (null, "Unavailable"); }
    }
}
