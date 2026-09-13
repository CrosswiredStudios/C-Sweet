using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using CSweet.Domain.Compute;

namespace CSweet.UnitTests;

public sealed class ComputeImageCertificationTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("control")]
    [InlineData("image")]
    [InlineData("payload-path")]
    [InlineData("legacy-purpose")]
    [InlineData("expired")]
    public void Independent_release_certification_binds_template_controls_payload_and_expiry(string scenario)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        var template = new ComputeTemplate("windows-clean", "windows", "x64", "sha256:" + new string('a', 64), []);
        var certificate = new ComputeImageCertification(ComputeImageCertificationVerifier.Purpose, "hyper-v", "0.1.0", template,
            "1", ComputeImageCertificationVerifier.RequiredControls.ToHashSet(), new() { ["runtime.dll"] = "sha256:" + new string('b', 64) }, now.AddMinutes(-1), now.AddHours(1));
        if (scenario == "control") certificate.Controls.Remove("generation-fencing");
        if (scenario == "image") certificate = certificate with { Template = template with { ImageDigest = "sha256:" + new string('c', 64) } };
        if (scenario == "payload-path") certificate.RuntimeFiles["../runtime.dll"] = "sha256:" + new string('b', 64);
        if (scenario == "legacy-purpose") certificate = certificate with { Purpose = "CSweet.WebHost.ProductVmCertification.v1" };
        if (scenario == "expired") certificate = certificate with { ExpiresAt = now };
        var json = JsonSerializer.Serialize(certificate, ComputeProtocol.Json);
        var signed = new SignedComputeImageCertification(json, Convert.ToBase64String(key.SignData(ComputeImageCertificationVerifier.Payload(json), HashAlgorithmName.SHA256)));
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        if (scenario == "valid") Assert.Equal(template.Id, ComputeImageCertificationVerifier.Verify(signed, publicKey, "hyper-v", "0.1.0", template, now).Template.Id);
        else Assert.Throws<UnauthorizedAccessException>(() => ComputeImageCertificationVerifier.Verify(signed, publicKey, "hyper-v", "0.1.0", template, now));
    }
}
