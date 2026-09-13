using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using CSweet.Domain.Compute;

namespace CSweet.UnitTests;

public sealed class ComputeCertifiedPayloadTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("image-changed")]
    [InlineData("runtime-changed")]
    [InlineData("omitted-dependency")]
    [InlineData("unprotected")]
    [InlineData("cancelled")]
    public async Task Materialized_payload_requires_complete_unchanged_protected_files(string scenario)
    {
        var directory = Path.Combine(Path.GetTempPath(), "compute-payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var image = Path.Combine(directory, "clean.vhdx");
            var runtime = Path.Combine(directory, "runtime.dll");
            await File.WriteAllTextAsync(image, "test image bytes");
            await File.WriteAllTextAsync(runtime, "test runtime bytes");
            static string Hash(string path) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            var template = new ComputeTemplate("clean", "windows", "x64", Hash(image), []);
            var now = DateTimeOffset.UtcNow;
            var evidence = new ComputeImageCertification(ComputeImageCertificationVerifier.Purpose, "hyper-v", "0.1.0",
                template, "1", ComputeImageCertificationVerifier.RequiredControls.ToHashSet(),
                new() { ["runtime.dll"] = Hash(runtime) }, now.AddMinutes(-1), now.AddHours(1));
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var json = JsonSerializer.Serialize(evidence, ComputeProtocol.Json);
            var signed = new SignedComputeImageCertification(json,
                Convert.ToBase64String(key.SignData(ComputeImageCertificationVerifier.Payload(json), HashAlgorithmName.SHA256)));
            if (scenario == "image-changed") await File.AppendAllTextAsync(image, "changed");
            if (scenario == "runtime-changed") await File.AppendAllTextAsync(runtime, "changed");
            var required = new HashSet<string>(StringComparer.Ordinal) { "runtime.dll" };
            if (scenario == "omitted-dependency") required.Add("dependency.dll");
            var checkedPaths = new List<string>();
            void Guard(string path)
            {
                checkedPaths.Add(path);
                if (scenario == "unprotected") throw new UnauthorizedAccessException();
            }
            using var cancellation = new CancellationTokenSource();
            if (scenario == "cancelled") cancellation.Cancel();
            Task<ComputeCertifiedPayload> Open() => ComputeCertifiedPayload.OpenAsync(signed,
                Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), "hyper-v", "0.1.0", template, now,
                image, directory, required, Guard, cancellation.Token);
            if (scenario == "cancelled") await Assert.ThrowsAnyAsync<OperationCanceledException>(Open);
            else if (scenario != "valid") await Assert.ThrowsAsync<UnauthorizedAccessException>(Open);
            else
            {
                using (var payload = await Open())
                {
                    Assert.Equal(image, payload.ImagePath);
                    Assert.Equal(new[] { directory, image, runtime }, checkedPaths);
                    if (OperatingSystem.IsWindows())
                    {
                        Assert.Throws<IOException>(() => File.WriteAllText(image, "tampered"));
                        Assert.Throws<IOException>(() => File.Delete(runtime));
                    }
                }
            }
            // Success, rejection and cancellation all release handles.
            using var imageWrite = new FileStream(image, FileMode.Open, FileAccess.Write, FileShare.None);
            using var runtimeWrite = new FileStream(runtime, FileMode.Open, FileAccess.Write, FileShare.None);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
