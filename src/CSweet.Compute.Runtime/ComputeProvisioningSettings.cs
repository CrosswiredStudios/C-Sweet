using System.Text.Json;
using CSweet.Compute.Contracts;

namespace CSweet.Compute.Runtime;

/// <summary>Protected installer settings; no private credentials and no agent-controlled values.</summary>
public sealed record ComputeProvisioningSettings(int Version, string CatalogPath, string ReleasePublicKey,
    string ProviderVersion, string RuntimeDirectory, HashSet<string> RuntimeFiles);

public static class ComputeProvisioningSettingsLoader
{
    public static Task<ComputeTemplateCatalog> ReadCatalogAsync(string path, ComputeProviderConfiguration provider,
        TimeProvider clock, CancellationToken token) => ReadCatalogAsync(path, provider, clock, WindowsComputeProtectedPaths.Verify, token);

    internal static async Task<ComputeTemplateCatalog> ReadCatalogAsync(string path, ComputeProviderConfiguration provider,
        TimeProvider clock, Action<string> protection, CancellationToken token)
    {
        CheckPath(path); protection(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > 65536) throw new InvalidDataException("Provisioning settings exceed their size limit.");
        var bytes = new byte[checked((int)stream.Length)]; await stream.ReadExactlyAsync(bytes, token);
        var settings = JsonSerializer.Deserialize<ComputeProvisioningSettings>(bytes, ComputeProtocol.Json);
        if (settings is not { Version: 1, RuntimeFiles.Count: > 0 and <= 512, ProviderVersion.Length: > 0 and <= 128 } ||
            settings.ProviderVersion.Any(char.IsControl)) throw new InvalidDataException("Provisioning settings are invalid.");
        CheckPath(settings.CatalogPath); CheckPath(settings.RuntimeDirectory);
        return await ComputeTemplateCatalog.ReadAsync(settings.CatalogPath, settings.ReleasePublicKey,
            provider.Enrollment.ProviderId, settings.ProviderVersion, settings.RuntimeDirectory, settings.RuntimeFiles,
            clock, protection, token);

        void CheckPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) ||
                Within(value, provider.JournalDirectory) || Within(value, provider.WorkloadDirectory))
                throw new InvalidDataException("Provisioning settings and payload metadata must be outside mutable runtime roots.");
        }
    }

    private static bool Within(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
