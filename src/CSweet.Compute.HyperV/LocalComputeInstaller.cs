using System.Net.Http.Json;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using System.Xml.Linq;
using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using CSweet.Domain.Compute;

namespace CSweet.Compute.HyperV;

/// <summary>Application installer entry point. Never reachable from agent dispatch or guest commands.</summary>
internal static class LocalComputeInstaller
{
    internal sealed record Handoff(int Version, Guid SetupId, Guid OrganizationId, string Secret, string CoreOrigin,
        string CoreCertificateSha256, ComputeSigningIdentity ControlPlaneKey, string RepositoryRoot);

    public static async Task ConfigureAsync(string? handoffPath, string imagePath, string acceptancePath, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() || !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Administrator approval is required.");
        var handoff = handoffPath is null ? null : await ReadHandoffAsync(handoffPath, token);
        var installation = handoff is null ? new ComputeLocalInstallation(BusinessId: null)
            : await ComputeLocalInstallation.ResolveAsync(handoff.OrganizationId, token);
        var configurationPath = installation.ConfigurationPath;
        var root = installation.Root;
        WindowsComputeProtectedPaths.Verify(root);
        var runtime = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        WindowsComputeProtectedPaths.Verify(runtime); WindowsComputeProtectedPaths.Verify(imagePath);
        var acceptance = XDocument.Load(acceptancePath);
        var counters = acceptance.Descendants().Single(x => x.Name.LocalName == "Counters");
        if (!int.TryParse(counters.Attribute("passed")?.Value, out var passed) || passed < 1 ||
            counters.Attribute("failed")?.Value != "0" || counters.Attribute("error")?.Value != "0")
            throw new InvalidDataException("The compute component acceptance suite did not pass.");
        var now = DateTimeOffset.UtcNow;
        var digest = "sha256:" + await HashFileAsync(imagePath, token);
        var template = new ComputeTemplate("linux-local-" + digest[7..31], "linux", "x64", digest,
            handoff is null ? ["guest-execution", "python"] : ["guest-execution", "python", "docker", "node", "docker-apps-v1"]);
        ComputeProviderConfiguration configuration;
        if (File.Exists(configurationPath))
        {
            configuration = await ComputeProviderConfigurationLoader.ReadAsync(configurationPath, token);
            if (handoff is not null && (configuration.Enrollment.NodeId != handoff.SetupId || configuration.Enrollment.OrganizationId != handoff.OrganizationId))
                throw new InvalidOperationException("This machine already belongs to another compute installation.");
            using var existingCertificate = ComputeProviderConfigurationLoader.OpenSigningCertificate(configuration, TimeProvider.System);
            if (handoff is null)
            {
                var installedCatalog = Path.Combine(root, "catalog.json");
                WindowsComputeProtectedPaths.Verify(installedCatalog);
                var installed = JsonSerializer.Deserialize<ComputeTemplateCatalogDocument>(await File.ReadAllTextAsync(installedCatalog, token), ComputeProtocol.Json);
                if (installed?.Templates.Any(x => x.Template.Id == template.Id && x.Template.ImageDigest == digest &&
                        Path.GetFullPath(x.ImagePath).Equals(Path.GetFullPath(imagePath), StringComparison.OrdinalIgnoreCase)) != true)
                    throw new InvalidOperationException("Repair must preserve the enrolled image.");
                template = installed.Templates.Single(x => x.Template.Id == template.Id).Template;
            }
        }
        else
        {
            if (handoff is null) throw new InvalidOperationException("Repair requires an existing enrolled provider.");
            using var key = CngKey.Create(CngAlgorithm.ECDsaP256, "CSweet.Compute." + handoff.SetupId.ToString("N"), new()
            {
                KeyCreationOptions = CngKeyCreationOptions.MachineKey, ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.Signing, Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider
            });
            var keyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Crypto", "Keys", key.UniqueName!);
            ProtectFile(keyPath);
            using var signer = new ECDsaCng(key);
            using var certificate = new CertificateRequest("CN=C-Sweet Local Compute", signer, HashAlgorithmName.SHA256)
                .CreateSelfSigned(now.AddMinutes(-5), now.AddYears(2));
            using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine)) { store.Open(OpenFlags.ReadWrite); store.Add(certificate); }
            configuration = new(1, new(handoff.OrganizationId, handoff.SetupId, "hyperv", handoff.ControlPlaneKey),
                new("local-node", Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo())), handoff.CoreOrigin,
                Path.Combine(root, "journal"), Path.Combine(root, "workloads"), new(2, new(4, 4096, 40960)),
                certificate.Thumbprint, StoreLocation.LocalMachine, Path.Combine(root, "provisioning.json"), handoff.CoreCertificateSha256);
        }
        ProtectDirectory(configuration.JournalDirectory); ProtectDirectory(configuration.WorkloadDirectory);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(runtime)) files.Add(Path.GetFileName(file), "sha256:" + await HashFileAsync(file, token));
        if (Directory.GetDirectories(runtime).Length != 0) throw new InvalidDataException("A flat provider release is required.");
        // The local development release is attested only after its component acceptance suite passes.
        // This records package approval, not a claim that hardware acceptance has already run.
        using var releaseKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var evidence = new ComputeImageCertification(ComputeImageCertificationVerifier.Purpose, "hyperv", "1", template, "1",
            ComputeImageCertificationVerifier.RequiredControls.ToHashSet(StringComparer.Ordinal), files, now, now.AddDays(30));
        var evidenceJson = JsonSerializer.Serialize(evidence, ComputeProtocol.Json);
        var signed = new SignedComputeImageCertification(evidenceJson,
            Convert.ToBase64String(releaseKey.SignData(ComputeImageCertificationVerifier.Payload(evidenceJson), HashAlgorithmName.SHA256)));
        var catalogPath = Path.Combine(root, "catalog.json");
        await WriteAsync(catalogPath, new ComputeTemplateCatalogDocument(1, [new(template, imagePath, signed)]), token);
        await WriteAsync(configuration.ProvisioningSettingsPath!, new ComputeProvisioningSettings(1, catalogPath,
            Convert.ToBase64String(releaseKey.ExportSubjectPublicKeyInfo()), "1", runtime, files.Keys.ToHashSet()), token);
        await WriteAsync(configurationPath, configuration, token);
        var catalog = await ComputeProvisioningSettingsLoader.ReadCatalogAsync(configuration.ProvisioningSettingsPath!, configuration, TimeProvider.System, token);
        await catalog.ValidateInstallationAsync(runtime, token);
        await new ComputeReplayJournal(configuration.JournalDirectory, configuration.Enrollment, configuration.Capacity, TimeProvider.System).InitializeAsync(token);
        if (handoff is null) return; // Preserve enrollment, credentials and all existing journal history.
        using var client = CreateClient(handoff);
        using var response = await client.PostAsJsonAsync($"api/compute/local-setup/{handoff.SetupId:D}/enroll",
            new { secret = handoff.Secret, publicKey = configuration.NodeSigningIdentity.PublicKeyBase64, template }, ComputeProtocol.Json, token);
        response.EnsureSuccessStatusCode();
    }

    public static async Task CompleteAsync(string handoffPath, bool succeeded, CancellationToken token)
    {
        var handoff = await ReadHandoffAsync(handoffPath, token);
        var installation = await ComputeLocalInstallation.ResolveAsync(handoff.OrganizationId, token);
        var configuration = await ComputeProviderConfigurationLoader.ReadAsync(installation.ConfigurationPath, token);
        if (configuration.Enrollment.OrganizationId != handoff.OrganizationId)
            throw new UnauthorizedAccessException("The compute service belongs to another setup.");
        using var client = CreateClient(handoff);
        using var response = await client.PostAsJsonAsync($"api/compute/local-setup/{handoff.SetupId:D}/complete",
            new { secret = handoff.Secret, succeeded }, ComputeProtocol.Json, token);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Restores this business enrollment without modifying another business or retiring workloads.</summary>
    public static async Task ReenrollAsync(string handoffPath, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Local provider recovery requires Windows.");
        var handoff = await ReadHandoffAsync(handoffPath, token);
        var installation = await ComputeLocalInstallation.ResolveAsync(handoff.OrganizationId, token);
        var configuration = await ComputeProviderConfigurationLoader.ReadAsync(installation.ConfigurationPath, token);
        if (configuration.Enrollment.OrganizationId != handoff.OrganizationId)
            throw new UnauthorizedAccessException("The compute service belongs to another business.");
        using var certificate = ComputeProviderConfigurationLoader.OpenSigningCertificate(
            configuration, TimeProvider.System);
        var catalog = await ComputeProvisioningSettingsLoader.ReadCatalogAsync(
            configuration.ProvisioningSettingsPath!, configuration, TimeProvider.System, token);
        var template = catalog.Templates
            .Select(entry => entry.Value)
            .SingleOrDefault(candidate => candidate.Features.Contains("docker-apps-v1", StringComparer.Ordinal))
            ?? throw new InvalidOperationException("The installed provider has no approved Docker-capable Linux template.");

        using var client = CreateClient(handoff);
        using var response = await client.PostAsJsonAsync(
            $"api/compute/local-setup/{handoff.SetupId:D}/enroll",
            new { secret = handoff.Secret, publicKey = configuration.NodeSigningIdentity.PublicKeyBase64, template, nodeId = configuration.Enrollment.NodeId },
            ComputeProtocol.Json, token);
        response.EnsureSuccessStatusCode();

        await WriteAtomicAsync(installation.ConfigurationPath, configuration with
        {
            Enrollment = configuration.Enrollment with { ControlPlaneKey = handoff.ControlPlaneKey },
            CoreOrigin = handoff.CoreOrigin,
            CoreCertificateSha256 = handoff.CoreCertificateSha256
        }, token);
    }
    private static HttpClient CreateClient(Handoff handoff)
    {
        var expected = Convert.FromHexString(handoff.CoreCertificateSha256);
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false };
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) => certificate is not null && certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow && certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow &&
            (errors & System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) == 0 &&
            CryptographicOperations.FixedTimeEquals(expected, certificate.GetCertHash(HashAlgorithmName.SHA256));
        return new(handler) { BaseAddress = new Uri(handoff.CoreOrigin), Timeout = TimeSpan.FromSeconds(30) };
    }

    private static async Task<Handoff> ReadHandoffAsync(string path, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(path) || new FileInfo(path).Length > 16384) throw new InvalidDataException();
        var handoff = JsonSerializer.Deserialize<Handoff>(await File.ReadAllBytesAsync(path, token), ComputeProtocol.Json) ?? throw new InvalidDataException();
        if (handoff.Version != 1 || handoff.SetupId == Guid.Empty || handoff.OrganizationId == Guid.Empty ||
            handoff.Secret is not { Length: 64 } || !handoff.Secret.All(Uri.IsHexDigit) ||
            handoff.CoreCertificateSha256 is not { Length: 64 } || !handoff.CoreCertificateSha256.All(Uri.IsHexDigit) ||
            !Uri.TryCreate(handoff.CoreOrigin, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/") throw new InvalidDataException();
        return handoff;
    }
    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task WriteAsync<T>(string path, T value, CancellationToken token)
    {
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(value, ComputeProtocol.Json), token);
        ProtectFile(path);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task WriteAtomicAsync<T>(string path, T value, CancellationToken token)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(value, ComputeProtocol.Json), token);
            ProtectFile(temporary);
            File.Move(temporary, path, overwrite: true);
            ProtectFile(path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void ProtectDirectory(string path)
    {
        Directory.CreateDirectory(path);
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        acl.SetOwner(administrators);
        foreach (var sid in new[] { administrators, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void ProtectFile(string path)
    {
        var acl = new FileSecurity(); acl.SetAccessRuleProtection(true, false);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        acl.SetOwner(administrators);
        acl.AddAccessRule(new(administrators, FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
    }
}
