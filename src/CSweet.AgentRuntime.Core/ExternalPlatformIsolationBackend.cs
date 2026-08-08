using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CSweet.AgentRuntime.Abstractions;

namespace CSweet.AgentRuntime.Core;

public class PlatformIsolationBackendOptions
{
    public string HelperExecutablePath { get; set; } = string.Empty;
    public string GuestImagePath { get; set; } = string.Empty;
    public string GuestImageDigest { get; set; } = string.Empty;
    public string GuestImageSignaturePath { get; set; } = string.Empty;
    public string GuestImageSigningCertificatePath { get; set; } = string.Empty;
    public string GuestImageSigningCertificateThumbprint { get; set; } = string.Empty;
    public string ArtifactImageRoot { get; set; } = string.Empty;
    public string BrokerProtocolVersion { get; set; } = "1.0";
    public string CertificationSuiteVersion { get; set; } = string.Empty;
    public string CertificationEvidenceDigest { get; set; } = string.Empty;
    public string CertificationEvidencePath { get; set; } = string.Empty;
    public DateTimeOffset? CertifiedAt { get; set; }
    public DateTimeOffset? CertificationExpiresAt { get; set; }
    public int HelperTimeoutSeconds { get; set; } = 120;
}

/// <summary>
/// Privileged backend adapter for small, platform-native VM helpers. The helper
/// protocol is typed JSON over private standard streams; no shell, raw command,
/// host path, network-device, or mount option is accepted from the control plane.
/// </summary>
public abstract class ExternalPlatformIsolationBackend : IPlatformIsolationBackend
{
    private const int MaximumHelperResponseBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly PlatformIsolationBackendOptions _options;
    private readonly TimeProvider _timeProvider;

    protected ExternalPlatformIsolationBackend(
        IsolationProviderDescriptor descriptor,
        PlatformIsolationBackendOptions options,
        TimeProvider timeProvider)
    {
        Descriptor = descriptor;
        _options = options;
        _timeProvider = timeProvider;
    }

    public IsolationProviderDescriptor Descriptor { get; }

    protected abstract bool IsHostPlatform(out string unavailableReason);

    public async Task<IsolationProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (!IsHostPlatform(out var platformReason)) return Unavailable(platformReason);
        if (!TryResolveFile(_options.HelperExecutablePath, out _))
            return Unavailable("The privileged platform helper is not installed at its configured absolute path.");
        if (!TryResolveFile(_options.GuestImagePath, out var guestImage))
            return Unavailable("The immutable guest image is not installed at its configured absolute path.");
        if (!TryResolveFile(_options.GuestImageSignaturePath, out var guestSignature) ||
            !TryResolveFile(_options.GuestImageSigningCertificatePath, out var signingCertificate))
            return Unavailable("The guest image detached signature and pinned signing certificate are not installed.");
        if (!TryResolveFile(_options.CertificationEvidencePath, out var certificationEvidence))
            return Unavailable("The provider certification evidence file is not installed.");
        if (!IsSha256(_options.GuestImageDigest) || !IsSha256(_options.CertificationEvidenceDigest))
            return Unavailable("Guest image and certification evidence must use immutable SHA-256 digests.");
        if (_options.CertifiedAt is null || string.IsNullOrWhiteSpace(_options.CertificationSuiteVersion))
            return Unavailable("No certification evidence is configured for this provider build.");
        if (!await VerifyFileDigestAsync(guestImage, _options.GuestImageDigest, cancellationToken))
            return Unavailable("The installed guest image does not match its configured digest.");
        if (!await VerifyFileDigestAsync(certificationEvidence, _options.CertificationEvidenceDigest, cancellationToken))
            return Unavailable("The installed certification evidence does not match its configured digest.");
        if (!await VerifyCertificationEvidenceAsync(certificationEvidence, cancellationToken))
            return Unavailable("The certification evidence is malformed or is not bound to this exact provider, image, protocol, and certification window.");
        if (!await VerifyGuestImageSignatureAsync(
                guestImage, guestSignature, signingCertificate,
                _options.GuestImageSigningCertificateThumbprint, cancellationToken))
            return Unavailable("The guest image signature or pinned signing certificate is invalid.");

        PlatformHelperResponse response;
        try { response = await InvokeAsync("probe", null, cancellationToken); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException)
        {
            return Unavailable($"Platform helper probe failed: {Sanitize(exception.Message)}");
        }
        if (!response.Success) return Unavailable(response.SanitizedError ?? response.ErrorCode ?? "Platform helper reported unavailable.");

        var certification = new IsolationProviderCertification(
            Descriptor.ProviderId,
            Descriptor.ProviderVersion,
            Descriptor.HostOperatingSystem,
            Descriptor.HostArchitecture,
            _options.GuestImageDigest,
            _options.BrokerProtocolVersion,
            _options.CertificationSuiteVersion,
            _options.CertificationEvidenceDigest,
            _options.CertifiedAt.Value,
            _options.CertificationExpiresAt);
        if (!certification.IsActiveAt(_timeProvider.GetUtcNow()))
            return Unavailable("The configured provider certification is expired.");
        return new IsolationProviderProbeResult(Descriptor, true, null, certification);
    }

    public async Task<IsolationWorkloadHandle> CreateAsync(IsolationWorkloadSpec workload, CancellationToken cancellationToken = default)
    {
        ValidateWorkload(workload);
        if (!TryResolveFile(_options.GuestImagePath, out var guestImage) ||
            !await VerifyFileDigestAsync(guestImage, _options.GuestImageDigest, cancellationToken))
            throw new InvalidDataException("The configured guest image failed its integrity check.");
        if (workload is not BuilderWorkloadSpec and not RuntimeWorkloadSpec)
            throw new InvalidDataException("The workload type is not supported by the platform helper protocol.");
        string? artifactImage = null;
        if (workload is RuntimeWorkloadSpec runtime)
        {
            if (string.IsNullOrWhiteSpace(_options.ArtifactImageRoot) ||
                !Path.IsPathFullyQualified(_options.ArtifactImageRoot))
                throw new InvalidDataException("The artifact media root is not configured as an absolute path.");
            artifactImage = Path.Combine(
                Path.GetFullPath(_options.ArtifactImageRoot),
                $"{runtime.Artifact.Digest[7..]}.iso");
            if (!await SingleFileIso9660.VerifyArtifactDigestAsync(
                    artifactImage, runtime.Artifact.Digest, cancellationToken))
                throw new InvalidDataException("The workload artifact media failed its integrity check.");
        }
        var response = await InvokeAsync("create", new PlatformHelperRequest
        {
            BuilderWorkload = workload as BuilderWorkloadSpec,
            RuntimeWorkload = workload as RuntimeWorkloadSpec,
            GuestImagePath = Path.GetFullPath(_options.GuestImagePath),
            ArtifactImagePath = artifactImage
        }, cancellationToken);
        EnsureSuccess(response);
        if (string.IsNullOrWhiteSpace(response.ProviderInstanceId) || response.ProviderInstanceId.Length > 256)
            throw new InvalidDataException("The platform helper returned an invalid instance identifier.");
        return new IsolationWorkloadHandle(Descriptor.ProviderId, workload.WorkloadId, response.ProviderInstanceId, workload.Kind);
    }

    public Task StartAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) =>
        InvokeHandleAsync("start", handle, null, cancellationToken);

    public async Task<IsolationWorkloadStatus?> InspectAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default)
    {
        ValidateHandle(handle);
        var response = await InvokeAsync("inspect", new PlatformHelperRequest { Handle = handle }, cancellationToken);
        if (string.Equals(response.ErrorCode, "not-found", StringComparison.Ordinal)) return null;
        EnsureSuccess(response);
        var status = response.Status ?? throw new InvalidDataException("The platform helper omitted workload status.");
        if (status.Handle != handle) throw new InvalidDataException("The platform helper returned status for another workload.");
        return status;
    }

    public Task StopAsync(IsolationWorkloadHandle handle, TimeSpan gracePeriod, CancellationToken cancellationToken = default)
    {
        if (gracePeriod < TimeSpan.Zero || gracePeriod > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(gracePeriod));
        return InvokeHandleAsync("stop", handle, (int)gracePeriod.TotalSeconds, cancellationToken);
    }

    public Task DestroyAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) =>
        InvokeHandleAsync("destroy", handle, null, cancellationToken);

    protected async Task<int> ReapAbandonedWorkloadsAsync(CancellationToken cancellationToken)
    {
        var response = await InvokeAsync("reap", null, cancellationToken);
        EnsureSuccess(response);
        if (response.WorkloadsRemoved < 0)
            throw new InvalidDataException("The platform helper returned an invalid cleanup count.");
        return response.WorkloadsRemoved;
    }

    public async IAsyncEnumerable<IsolationLogChunk> StreamLogsAsync(
        IsolationWorkloadHandle handle,
        int maximumBytes,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateHandle(handle);
        if (maximumBytes is < 1 or > MaximumHelperResponseBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var response = await InvokeAsync("logs", new PlatformHelperRequest { Handle = handle, MaximumBytes = maximumBytes }, cancellationToken);
        EnsureSuccess(response);
        foreach (var chunk in response.Logs ?? [])
        {
            if (chunk.Content.Length > maximumBytes) throw new InvalidDataException("The platform helper exceeded the log limit.");
            yield return chunk;
        }
    }

    private async Task InvokeHandleAsync(string operation, IsolationWorkloadHandle handle, int? gracePeriodSeconds, CancellationToken cancellationToken)
    {
        ValidateHandle(handle);
        EnsureSuccess(await InvokeAsync(operation, new PlatformHelperRequest
        {
            Handle = handle,
            GracePeriodSeconds = gracePeriodSeconds
        }, cancellationToken));
    }

    private async Task<PlatformHelperResponse> InvokeAsync(string operation, PlatformHelperRequest? request, CancellationToken cancellationToken)
    {
        if (!TryResolveFile(_options.HelperExecutablePath, out var helper))
            throw new IOException("The configured platform helper is unavailable.");
        var start = new ProcessStartInfo
        {
            FileName = helper,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--protocol");
        start.ArgumentList.Add("1.0");
        start.ArgumentList.Add("--operation");
        start.ArgumentList.Add(operation);
        using var process = Process.Start(start) ?? throw new IOException("The platform helper could not be started.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.HelperTimeoutSeconds, 5, 600)));
        var token = timeout.Token;
        var payload = JsonSerializer.Serialize(request ?? new PlatformHelperRequest(), JsonOptions);
        await process.StandardInput.WriteAsync(payload.AsMemory(), token);
        process.StandardInput.Close();
        var stdoutTask = ReadBoundedAsync(process.StandardOutput.BaseStream, MaximumHelperResponseBytes, token);
        var stderrTask = ReadBoundedAsync(process.StandardError.BaseStream, 16 * 1024, token);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException("The platform helper timed out.");
        }
        var output = await stdoutTask;
        var error = await stderrTask;
        if (process.ExitCode != 0)
            throw new IOException($"The platform helper failed: {Sanitize(Encoding.UTF8.GetString(error))}");
        return JsonSerializer.Deserialize<PlatformHelperResponse>(output, JsonOptions)
            ?? throw new InvalidDataException("The platform helper response was empty.");
    }

    private void ValidateWorkload(IsolationWorkloadSpec workload)
    {
        ArgumentNullException.ThrowIfNull(workload);
        workload.ResourceLimits.Validate();
        if (!string.Equals(workload.GuestImage.Digest, _options.GuestImageDigest, StringComparison.Ordinal) ||
            !string.Equals(workload.BrokerLease.ExpectedGuestImageDigest, _options.GuestImageDigest, StringComparison.Ordinal))
            throw new InvalidDataException("The workload is not bound to this provider's certified guest image.");
        if (!string.Equals(workload.BrokerLease.ProtocolVersion, _options.BrokerProtocolVersion, StringComparison.Ordinal) ||
            workload.BrokerLease.ExpiresAt <= _timeProvider.GetUtcNow())
            throw new InvalidDataException("The workload broker lease is invalid.");
    }

    private void ValidateHandle(IsolationWorkloadHandle handle)
    {
        if (!string.Equals(handle.ProviderId, Descriptor.ProviderId, StringComparison.Ordinal) ||
            handle.WorkloadId == Guid.Empty || string.IsNullOrWhiteSpace(handle.ProviderInstanceId))
            throw new InvalidDataException("The workload handle is invalid for this provider.");
    }

    private IsolationProviderProbeResult Unavailable(string reason) => new(Descriptor, false, reason, null);
    private static bool TryResolveFile(string path, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        resolved = Path.GetFullPath(path);
        return File.Exists(resolved);
    }
    private static bool IsSha256(string value) => value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) && value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;
    private static async Task<bool> VerifyFileDigestAsync(
        string path,
        string expectedDigest,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var digest = await SHA256.HashDataAsync(stream, cancellationToken);
            var actual = $"sha256:{Convert.ToHexStringLower(digest)}";
            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expectedDigest));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<bool> VerifyGuestImageSignatureAsync(
        string imagePath,
        string signaturePath,
        string certificatePath,
        string expectedThumbprint,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedThumbprint)) return false;
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
            var normalizedExpected = NormalizeThumbprint(expectedThumbprint);
            var normalizedActual = NormalizeThumbprint(certificate.Thumbprint);
            var now = DateTimeOffset.UtcNow;
            if (normalizedExpected.Length == 0 || normalizedActual.Length != normalizedExpected.Length ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(normalizedActual), Encoding.ASCII.GetBytes(normalizedExpected)) ||
                now < certificate.NotBefore || now > certificate.NotAfter)
                return false;
            await using var image = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var imageHash = await SHA256.HashDataAsync(image, cancellationToken);
            var signature = await File.ReadAllBytesAsync(signaturePath, cancellationToken);
            using var rsa = certificate.GetRSAPublicKey();
            if (rsa is not null)
                return rsa.VerifyHash(imageHash, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var ecdsa = certificate.GetECDsaPublicKey();
            return ecdsa?.VerifyHash(imageHash, signature) == true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return false;
        }
    }

    private async Task<bool> VerifyCertificationEvidenceAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length is < 2 or > 1024 * 1024) return false;
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var evidence = await JsonSerializer.DeserializeAsync<ProviderCertificationEvidence>(
                stream, JsonOptions, cancellationToken);
            return evidence is not null &&
                string.Equals(evidence.ProviderId, Descriptor.ProviderId, StringComparison.Ordinal) &&
                string.Equals(evidence.ProviderVersion, Descriptor.ProviderVersion, StringComparison.Ordinal) &&
                string.Equals(evidence.HostOperatingSystem, Descriptor.HostOperatingSystem, StringComparison.Ordinal) &&
                string.Equals(evidence.HostArchitecture, Descriptor.HostArchitecture, StringComparison.Ordinal) &&
                string.Equals(evidence.GuestImageDigest, _options.GuestImageDigest, StringComparison.Ordinal) &&
                string.Equals(evidence.BrokerProtocolVersion, _options.BrokerProtocolVersion, StringComparison.Ordinal) &&
                string.Equals(evidence.CertificationSuiteVersion, _options.CertificationSuiteVersion, StringComparison.Ordinal) &&
                evidence.CertifiedAt == _options.CertifiedAt &&
                evidence.CertificationExpiresAt == _options.CertificationExpiresAt;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static string NormalizeThumbprint(string value) =>
        new(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
    private static void EnsureSuccess(PlatformHelperResponse response)
    {
        if (!response.Success) throw new InvalidOperationException($"Platform helper rejected the operation ({Sanitize(response.ErrorCode)}).");
    }
    private static string Sanitize(string? value) => string.IsNullOrWhiteSpace(value) ? "unspecified" : new string(value.Where(character => !char.IsControl(character)).Take(256).ToArray());
    private static void TryKill(Process process) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximumBytes + 1 - (int)output.Length)), cancellationToken);
            if (read == 0) return output.ToArray();
            output.Write(buffer, 0, read);
            if (output.Length > maximumBytes) throw new InvalidDataException("The platform helper response exceeded its limit.");
        }
    }

}

public sealed record ProviderCertificationEvidence(
    string ProviderId,
    string ProviderVersion,
    string HostOperatingSystem,
    string HostArchitecture,
    string GuestImageDigest,
    string BrokerProtocolVersion,
    string CertificationSuiteVersion,
    DateTimeOffset CertifiedAt,
    DateTimeOffset? CertificationExpiresAt);
