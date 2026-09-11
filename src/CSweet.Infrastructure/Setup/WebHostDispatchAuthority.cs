using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Domain.Setup;
using CSweet.WebHost.Contracts;
using CSweet.WebHost.Core;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Setup;

public sealed class WebHostExecutionOptions
{
    public const string SectionName = "CSweet:WebHost:Execution";
    public bool Enabled { get; set; }
    // Operator-managed secret configuration; never supplied by a host, plugin, agent, or API request.
    public string AuthorizationSigningKeyPem { get; set; } = "";
    public string ReleaseVerificationPublicKeyBase64 { get; set; } = "";
    public List<SignedProductReleaseCertification> ApprovedReleases { get; set; } = [];
}

public interface IWebHostAuthorizationSigner
{
    bool IsConfigured { get; }
    string Sign(byte[] payload);
}

public sealed class WebHostAuthorizationSigner(IOptions<WebHostExecutionOptions> execution,
    IOptions<WebHostRegistryOptions> registry) : IWebHostAuthorizationSigner
{
    public bool IsConfigured
    {
        get
        {
            try { using var key = Load(); return true; }
            catch (Exception error) when (error is CryptographicException or ArgumentException or InvalidOperationException)
            { return false; }
        }
    }
    public string Sign(byte[] payload)
    {
        using var key = Load();
        return Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256));
    }
    private ECDsa Load()
    {
        var config = execution.Value;
        if (!config.Enabled || config.AuthorizationSigningKeyPem is not { Length: > 0 and <= 16384 })
            throw new InvalidOperationException("WebHost workload signing is not configured.");
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(config.AuthorizationSigningKeyPem);
            var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            WebHostIdentity.ValidatePublicKey(publicKey);
            if (publicKey != registry.Value.AuthorizationVerificationPublicKeyBase64)
                throw new InvalidOperationException("WebHost signing does not match the enrolled verification key.");
            key.ExportParameters(true); // A public-key-only configuration must never advertise dispatch readiness.
            return key;
        }
        catch { key.Dispose(); throw; }
    }
}

public sealed record WebHostApprovedRuntime(ProductProviderStatus Provider, ProductReleaseCertification Certificate);

public sealed class WebHostReleaseCatalog(IOptions<WebHostExecutionOptions> options,
    IWebHostAuthorizationSigner signer, TimeProvider clock)
{
    public WebHostApprovedRuntime? Select(WebHostRegistration host, DateTimeOffset requiredExpiry, bool requireConnected = true)
    {
        var now = clock.GetUtcNow();
        if (!options.Value.Enabled || !signer.IsConfigured || host.RevokedAt is not null || host.ExpiresAt < requiredExpiry ||
            (requireConnected && (host.LastHeartbeatAt is null || host.LastHeartbeatAt < now.AddMinutes(-2))) || host.ReportedHeartbeatJson is null)
            return null;
        var heartbeat = JsonSerializer.Deserialize<WebHostHeartbeat>(host.ReportedHeartbeatJson, PreviewJson.Options);
        foreach (var provider in heartbeat?.Providers ?? [])
        {
            if (!provider.Available || !provider.Certified || provider.CertificationExpiresAt is null || provider.CertificationExpiresAt < requiredExpiry) continue;
            foreach (var envelope in options.Value.ApprovedReleases.Take(128))
            {
                try
                {
                    var release = ProductReleaseCertificationVerifier.Verify(envelope,
                        options.Value.ReleaseVerificationPublicKeyBase64, provider.Id, provider.Version, provider.GuestImageDigest, now);
                    if (release.ExpiresAt >= requiredExpiry) return new(provider, release);
                }
                catch (Exception error) when (error is UnauthorizedAccessException or CryptographicException or
                    ArgumentException or FormatException or JsonException or IOException) { }
            }
        }
        return null;
    }
}
