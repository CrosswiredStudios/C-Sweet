using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace CSweet.ExecutionNode;

public sealed record ExecutionNodeState(
    Guid NodeId,
    string EnrollmentReceipt,
    long SessionEpoch,
    string CertificatePath);

public sealed class ExecutionNodeStateStore(ExecutionNodeOptions options)
{
    private readonly string _directory = options.ResolveStateDirectory();
    private string StatePath => Path.Combine(_directory, "node-state.json");
    private string CertificatePath => Path.Combine(_directory, "node-identity.pfx");
    private string MaintenanceDirectory => Path.Combine(_directory, "maintenance");
    private string DrainStatePath => Path.Combine(MaintenanceDirectory, "drain-state");
    private string ActiveAssignmentsDirectory => Path.Combine(MaintenanceDirectory, "active-assignments");

    public void InitializeMaintenanceSession()
    {
        Directory.CreateDirectory(ActiveAssignmentsDirectory);
        foreach (var marker in Directory.EnumerateFiles(
                     ActiveAssignmentsDirectory, "*.active", SearchOption.TopDirectoryOnly))
            File.Delete(marker);
    }

    public void SetDraining(bool draining)
    {
        Directory.CreateDirectory(MaintenanceDirectory);
        WriteAtomic(DrainStatePath, draining ? "draining" : "ready");
    }

    public void MarkAssignmentActive(Guid assignmentId)
    {
        Directory.CreateDirectory(ActiveAssignmentsDirectory);
        WriteAtomic(Path.Combine(ActiveAssignmentsDirectory, $"{assignmentId:N}.active"),
            DateTimeOffset.UtcNow.ToString("O"));
    }

    public void MarkAssignmentInactive(Guid assignmentId)
    {
        var path = Path.Combine(ActiveAssignmentsDirectory, $"{assignmentId:N}.active");
        if (File.Exists(path)) File.Delete(path);
    }

    public async Task<ExecutionNodeState?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            await using var stream = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<ExecutionNodeState>(stream, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public async Task SaveAsync(ExecutionNodeState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var temporary = StatePath + ".new";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                         4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            await JsonSerializer.SerializeAsync(stream, state, cancellationToken: cancellationToken);
        File.Move(temporary, StatePath, true);
    }

    public X509Certificate2 GetOrCreateCertificate()
    {
        Directory.CreateDirectory(_directory);
        if (File.Exists(CertificatePath))
            return X509CertificateLoader.LoadPkcs12FromFile(CertificatePath, null,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            $"CN=CSweet Execution Node {Environment.MachineName}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(7));
        File.WriteAllBytes(CertificatePath, generated.Export(X509ContentType.Pfx));
        return X509CertificateLoader.LoadPkcs12FromFile(CertificatePath, null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }

    public string GetCertificatePath() => CertificatePath;

    public static string CreateCertificateSigningRequestPem(X509Certificate2 certificate)
    {
        using var key = certificate.GetECDsaPrivateKey()
            ?? throw new CryptographicException("The execution-node identity key is unavailable.");
        var request = new CertificateRequest(
            certificate.SubjectName, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSigningRequestPem();
    }

    public X509Certificate2 InstallOperationalCertificate(
        X509Certificate2 current,
        string certificateBase64)
    {
        var publicCertificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(certificateBase64));
        using (publicCertificate)
        using (var privateKey = current.GetECDsaPrivateKey()
            ?? throw new CryptographicException("The execution-node private key is unavailable."))
        using (var combined = publicCertificate.CopyWithPrivateKey(privateKey))
        {
            var temporary = CertificatePath + ".new";
            File.WriteAllBytes(temporary, combined.Export(X509ContentType.Pfx));
            File.Move(temporary, CertificatePath, true);
        }
        return X509CertificateLoader.LoadPkcs12FromFile(CertificatePath, null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }

    private static void WriteAtomic(string path, string value)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.new";
        File.WriteAllText(temporary, value);
        File.Move(temporary, path, true);
    }
}
