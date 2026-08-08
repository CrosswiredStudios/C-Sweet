using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;

return await BuilderProgram.RunAsync(args);

internal static class BuilderProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = BuilderOptions.Parse(args);
            await using var broker = new BuilderBrokerClient(options.BrokerSocketPath);
            await broker.ReportAsync("isolate", "started", "Preparing the disposable builder workspace.");
            var root = "/run/csweet/workload/build";
            ResetDirectory(root);
            var archive = Path.Combine(root, "source.zip");
            await broker.DownloadAsync(SourceArchiveUri(options.RepositoryUrl, options.CommitSha), archive, options.MaximumRepositoryBytes);
            var source = Path.Combine(root, "source");
            ExtractSourceArchive(archive, source, options.MaximumRepositoryBytes);
            var project = ResolveProject(source, options.ProjectPath);
            await broker.ReportAsync("isolate", "succeeded", "The isolated source workspace is ready.");

            var nugetTrust = await NuGetTrustedRepository.LoadAsync(
                broker,
                Path.Combine(root, "nuget-trust"));
            await using var nuget = await NuGetLoopbackProxy.StartAsync(broker, Path.Combine(root, "nuget-proxy"));
            var nugetConfig = Path.Combine(root, "NuGet.Config");
            await File.WriteAllTextAsync(nugetConfig, NuGetConfiguration(nuget.ServiceIndexUrl, nugetTrust));
            var environment = new Dictionary<string, string>
            {
                ["DOTNET_CLI_HOME"] = Path.Combine(root, "dotnet-home"),
                ["NUGET_PACKAGES"] = Path.Combine(root, "nuget-packages"),
                ["DOTNET_NOLOGO"] = "1",
                ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_TieredPGO"] = "0",
                ["MSBUILDDISABLENODEREUSE"] = "1",
                ["SSL_CERT_FILE"] = nuget.TrustCertificatePath,
                ["NUGET_CERT_REVOCATION_MODE"] = "offline"
            };

            await broker.ReportAsync("restore", "started", "Restoring dependencies through the approved package broker.");
            await RunRequiredAsync("dotnet", ["restore", project, "--nologo", "--disable-build-servers", "--configfile", nugetConfig], source, environment);
            await broker.ReportAsync("restore", "succeeded", "Approved dependencies were restored.");

            var output = Path.Combine(root, "output");
            Directory.CreateDirectory(output);
            await broker.ReportAsync("publish", "started", "Compiling and publishing the agent inside the builder VM.");
            var publishArguments = new List<string> { "publish", project, "--configuration", "Release", "--no-restore", "--nologo", "--disable-build-servers", "--output", output };
            if (!string.IsNullOrWhiteSpace(options.TargetFramework))
            {
                publishArguments.Add("--framework");
                publishArguments.Add(options.TargetFramework);
            }
            await RunRequiredAsync("dotnet", publishArguments, source, environment);
            File.Copy(Path.Combine(source, "csweet-plugin.json"), Path.Combine(output, "csweet-plugin.json"), overwrite: true);
            await broker.ReportAsync("publish", "succeeded", "The agent publish output is ready for verification.");

            await broker.ReportAsync("package", "started", "Creating the immutable agent bundle.");
            var bundle = Path.Combine(root, "agent.csab");
            await CreateBundleAsync(output, Path.GetFileNameWithoutExtension(project), bundle, options.MaximumArtifactBytes);
            await broker.UploadArtifactAsync(bundle);
            await broker.ReportAsync("package", "succeeded", "The immutable agent bundle was transferred for host validation.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(Sanitize(exception.Message));
            return 1;
        }
    }

    private static Uri SourceArchiveUri(Uri repository, string commit)
    {
        if (!repository.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !repository.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The dotnet-publish-v1 profile currently supports GitHub HTTPS repositories only.");
        var segments = repository.AbsolutePath.Trim('/').Split('/');
        if (segments.Length != 2) throw new InvalidDataException("The GitHub repository URL is invalid.");
        var name = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        return new Uri($"https://codeload.github.com/{Uri.EscapeDataString(segments[0])}/{Uri.EscapeDataString(name)}/zip/{commit}");
    }

    private static void ExtractSourceArchive(string archivePath, string destination, long maximumBytes)
    {
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count is < 1 or > 20_000) throw new InvalidDataException("The source archive entry count is invalid.");
        var firstSegments = archive.Entries.Select(entry => entry.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray();
        if (firstSegments.Length != 1) throw new InvalidDataException("The source archive must have one repository root.");
        var prefix = firstSegments[0]! + "/";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length == prefix.Length) continue;
            var relative = name[prefix.Length..].TrimEnd('/');
            var segments = relative.Split('/', StringSplitOptions.None);
            if (segments.Any(segment => segment is "" or "." or "..") || relative.StartsWith('/') || relative.Any(char.IsControl))
                throw new InvalidDataException("The source archive contains an invalid path.");
            if (!seen.Add(relative)) throw new InvalidDataException("The source archive contains duplicate paths.");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(ResolveBeneath(destination, relative));
                continue;
            }
            total = checked(total + entry.Length);
            if (total > maximumBytes) throw new InvalidDataException("The expanded source exceeds its approved limit.");
            var target = ResolveBeneath(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }

    private static string ResolveProject(string source, string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The approved project path is invalid.");
        var project = ResolveBeneath(source, projectPath.Replace('\\', '/'));
        if (!File.Exists(project)) throw new FileNotFoundException("The approved project was not present at the immutable commit.");
        if (!File.Exists(Path.Combine(source, "csweet-plugin.json"))) throw new FileNotFoundException("The source manifest is missing.");
        return project;
    }

    private static async Task RunRequiredAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string> environment)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var item in environment) start.Environment[item.Key] = item.Value;
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {executable}.");
        var stdout = CopyOutputAsync(process.StandardOutput, Console.Out);
        var stderr = CopyOutputAsync(process.StandardError, Console.Error);
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0) throw new InvalidOperationException($"{executable} exited with code {process.ExitCode}.");
    }

    private static async Task CopyOutputAsync(StreamReader input, TextWriter output)
    {
        while (await input.ReadLineAsync() is { } line) await output.WriteLineAsync(line);
    }

    private static async Task CreateBundleAsync(string outputRoot, string entrypoint, string bundlePath, long maximumBytes)
    {
        var files = Directory.EnumerateFiles(outputRoot, "*", SearchOption.AllDirectories).ToArray();
        if (files.Length is < 1 or > 10_000) throw new InvalidDataException("The publish output file count is invalid.");
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = "1.0", operatingSystem = "linux", architecture = "x64", entrypoint = new[] { entrypoint } });
        await using var output = new FileStream(bundlePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (var writer = new TarWriter(output, leaveOpen: true))
        {
            var manifestEntry = new PaxTarEntry(TarEntryType.RegularFile, "artifact.json") { DataStream = new MemoryStream(manifest), Uid = 0, Gid = 0, Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead };
            await writer.WriteEntryAsync(manifestEntry);
            foreach (var file in files.OrderBy(path => Path.GetRelativePath(outputRoot, path), StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(outputRoot, file).Replace('\\', '/');
                var info = new FileInfo(file);
                if (info.LinkTarget is not null) throw new InvalidDataException("The publish output contains a symbolic link.");
                if (output.Length + info.Length > maximumBytes) throw new InvalidDataException("The agent bundle exceeds its approved limit.");
                await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                var executable = relative.Equals(entrypoint, StringComparison.Ordinal) ||
                    relative.EndsWith(".sh", StringComparison.Ordinal);
                var entry = new PaxTarEntry(TarEntryType.RegularFile, "payload/" + relative)
                {
                    DataStream = input, Uid = 0, Gid = 0,
                    Mode = executable
                        ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                        : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
                };
                await writer.WriteEntryAsync(entry);
            }
        }
        if (new FileInfo(bundlePath).Length > maximumBytes) throw new InvalidDataException("The agent bundle exceeds its approved limit.");
    }

    internal static string NuGetConfiguration(string source, NuGetTrustedRepository trustedRepository)
    {
        var certificates = string.Join(
            Environment.NewLine,
            trustedRepository.CertificateFingerprints.Select(fingerprint =>
                $"      <certificate fingerprint=\"{fingerprint}\" hashAlgorithm=\"SHA256\" allowUntrustedRoot=\"false\" />"));
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources><clear /><add key="csweet-broker" value="{source}" /></packageSources>
              <config><add key="signatureValidationMode" value="require" /></config>
              <trustedSigners>
                <repository name="nuget.org" serviceIndex="{trustedRepository.ServiceIndexUrl}">
            {certificates}
                </repository>
              </trustedSigners>
            </configuration>
            """;
    }

    private static string ResolveBeneath(string root, string relative)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var value = Path.GetFullPath(Path.Combine(normalizedRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!value.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("A path escaped the disposable build workspace.");
        return value;
    }

    private static void ResetDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);
    }

    private static string Sanitize(string value) => new(value.Where(character => !char.IsControl(character) || character == ' ').Take(1000).ToArray());
}

internal sealed record NuGetTrustedRepository(
    string ServiceIndexUrl,
    IReadOnlyList<string> CertificateFingerprints)
{
    private static readonly Uri NuGetServiceIndex = new("https://api.nuget.org/v3/index.json");
    private static readonly Uri NuGetRepositorySignatures = new(
        "https://api.nuget.org/v3-index/repository-signatures/5.0.0/index.json");

    public static async Task<NuGetTrustedRepository> LoadAsync(
        BuilderBrokerClient broker,
        string cacheRoot)
    {
        Directory.CreateDirectory(cacheRoot);
        var signatureMetadataPath = Path.Combine(cacheRoot, "repository-signatures.json");
        await broker.DownloadAsync(NuGetRepositorySignatures, signatureMetadataPath, 4 * 1024 * 1024);
        return Parse(
            NuGetServiceIndex.AbsoluteUri,
            await File.ReadAllTextAsync(signatureMetadataPath));
    }

    internal static NuGetTrustedRepository Parse(string serviceIndexUrl, string metadataJson)
    {
        if (!Uri.TryCreate(serviceIndexUrl, UriKind.Absolute, out var serviceIndex) ||
            serviceIndex.Scheme != Uri.UriSchemeHttps ||
            !serviceIndex.Host.Equals("api.nuget.org", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The trusted NuGet service index is invalid.");

        using var metadata = JsonDocument.Parse(metadataJson);
        if (!metadata.RootElement.TryGetProperty("allRepositorySigned", out var allSigned) ||
            allSigned.ValueKind != JsonValueKind.True ||
            !metadata.RootElement.TryGetProperty("signingCertificates", out var certificates) ||
            certificates.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("NuGet.org did not assert that all packages are repository signed.");

        const string sha256Oid = "2.16.840.1.101.3.4.2.1";
        var fingerprints = certificates.EnumerateArray()
            .Select(certificate => certificate.TryGetProperty("fingerprints", out var values) &&
                values.TryGetProperty(sha256Oid, out var fingerprint)
                    ? fingerprint.GetString()?.ToLowerInvariant()
                    : null)
            .Where(fingerprint => fingerprint is { Length: 64 } &&
                fingerprint.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            .Select(fingerprint => fingerprint!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (fingerprints.Length == 0)
            throw new InvalidDataException("NuGet.org did not publish a valid SHA-256 repository signer.");
        return new NuGetTrustedRepository(serviceIndex.AbsoluteUri, fingerprints);
    }
}

internal sealed record BuilderOptions(
    Uri RepositoryUrl,
    string CommitSha,
    string ProjectPath,
    long MaximumRepositoryBytes,
    long MaximumArtifactBytes,
    string BrokerSocketPath,
    string? TargetFramework)
{
    public static BuilderOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(args[index][2..], args[index + 1]))
                throw new InvalidDataException("The builder arguments are invalid.");
        }
        string Required(string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidDataException($"The builder argument {key} is required.");
        var repository = new Uri(Required("repository"), UriKind.Absolute);
        var commit = Required("commit");
        if (commit.Length != 40 || commit.Any(character => !Uri.IsHexDigit(character))) throw new InvalidDataException("The immutable commit is invalid.");
        var repositoryBytes = long.Parse(Required("maximum-repository-bytes"), System.Globalization.CultureInfo.InvariantCulture);
        var artifactBytes = long.Parse(Required("maximum-artifact-bytes"), System.Globalization.CultureInfo.InvariantCulture);
        if (repositoryBytes is < 1 or > 2L * 1024 * 1024 * 1024 || artifactBytes is < 1 or > 10L * 1024 * 1024 * 1024) throw new InvalidDataException("The builder byte limits are invalid.");
        values.TryGetValue("target-framework", out var targetFramework);
        if (targetFramework is not null && targetFramework is not ("net8.0" or "net9.0" or "net10.0"))
            throw new InvalidDataException("The approved target framework is invalid.");
        return new BuilderOptions(
            repository,
            commit.ToLowerInvariant(),
            Required("project"),
            repositoryBytes,
            artifactBytes,
            Required("broker-socket"),
            targetFramework);
    }
}

internal sealed class BuilderBrokerClient : IAsyncDisposable
{
    private const int ChunkBytes = 768 * 1024;
    private readonly HttpClient _http;

    public BuilderBrokerClient(string socketPath)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            }
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost"), Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<(string? ContentType, long Length)> DownloadAsync(Uri uri, string destination, long maximumBytes, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                long offset = 0;
                string? contentType = null;
                while (true)
                {
                    var payload = JsonSerializer.SerializeToUtf8Bytes(new { url = uri.AbsoluteUri, offset, maximumBytes = ChunkBytes });
                    using var response = await _http.PostAsync("/build/fetch", new ByteArrayContent(payload), cancellationToken);
                    if (!response.IsSuccessStatusCode)
                        throw new BuilderDownloadException((int)response.StatusCode);
                    contentType ??= Header(response, "X-CSweet-Content-Type");
                    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    if (bytes.Length == 0 && !IsTrue(response, "X-CSweet-Complete")) throw new IOException("The build broker returned an empty incomplete chunk.");
                    offset = checked(offset + bytes.Length);
                    if (offset > maximumBytes) throw new InvalidDataException("The broker download exceeds its approved limit.");
                    await output.WriteAsync(bytes, cancellationToken);
                    if (IsTrue(response, "X-CSweet-Complete"))
                    {
                        await output.FlushAsync(cancellationToken);
                        File.Move(partial, destination);
                        return (contentType, offset);
                    }
                }
            }
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    public async Task UploadArtifactAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = "sha256:" + Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken));
        input.Position = 0;
        var buffer = new byte[ChunkBytes];
        long sequence = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            using var content = new ByteArrayContent(buffer, 0, read);
            content.Headers.Add("X-CSweet-Sequence", sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
            content.Headers.Add("X-CSweet-Completed", "false");
            using var response = await _http.PostAsync("/build/artifact", content, cancellationToken);
            response.EnsureSuccessStatusCode();
            sequence++;
        }
        using var final = new ByteArrayContent([]);
        final.Headers.Add("X-CSweet-Sequence", sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        final.Headers.Add("X-CSweet-Completed", "true");
        final.Headers.Add("X-CSweet-Digest", digest);
        using var completed = await _http.PostAsync("/build/artifact", final, cancellationToken);
        completed.EnsureSuccessStatusCode();
    }

    public async Task ReportAsync(string step, string status, string detail, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { step, status, detail });
        using var response = await _http.PostAsync("/build/progress", new ByteArrayContent(payload), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static bool IsTrue(HttpResponseMessage response, string name) => string.Equals(Header(response, name), "true", StringComparison.OrdinalIgnoreCase);
    private static string? Header(HttpResponseMessage response, string name) => response.Headers.TryGetValues(name, out var values) ? values.SingleOrDefault() : null;
    public ValueTask DisposeAsync() { _http.Dispose(); return ValueTask.CompletedTask; }
}

internal sealed class BuilderDownloadException(int statusCode) : IOException($"The build broker download failed with HTTP status {statusCode}.")
{
    public int StatusCode { get; } = statusCode;
}

internal sealed class NuGetLoopbackProxy : IAsyncDisposable
{
    private readonly BuilderBrokerClient _broker;
    private readonly string _cacheRoot;
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _certificate;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _cacheLocks = new(StringComparer.Ordinal);
    private readonly Task _loop;
    public string ServiceIndexUrl { get; }
    public string TrustCertificatePath { get; }

    private NuGetLoopbackProxy(
        BuilderBrokerClient broker,
        string cacheRoot,
        TcpListener listener,
        X509Certificate2 certificate,
        string trustCertificatePath)
    {
        _broker = broker;
        _cacheRoot = cacheRoot;
        _listener = listener;
        _certificate = certificate;
        TrustCertificatePath = trustCertificatePath;
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        ServiceIndexUrl = $"https://localhost:{port}/v3/index.json";
        _loop = AcceptLoopAsync();
    }

    public static async Task<NuGetLoopbackProxy> StartAsync(BuilderBrokerClient broker, string cacheRoot)
    {
        Directory.CreateDirectory(cacheRoot);
        var certificate = CreateCertificate();
        var trustCertificatePath = Path.Combine(cacheRoot, "loopback-ca.pem");
        await File.WriteAllTextAsync(trustCertificatePath, certificate.ExportCertificatePem());
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(32);
        return new NuGetLoopbackProxy(broker, cacheRoot, listener, certificate, trustCertificatePath);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try { _ = HandleAsync(await _listener.AcceptTcpClientAsync(_lifetime.Token)); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        await using (var network = client.GetStream())
        await using (var stream = new SslStream(network, leaveInnerStreamOpen: false))
        {
            try
            {
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ClientCertificateRequired = false
                }, _lifetime.Token);
                var path = await ReadGetPathAsync(stream, _lifetime.Token);
                var upstream = path == "/v3/index.json"
                    ? new Uri("https://api.nuget.org/v3/index.json")
                    : DecodeUpstream(path);
                var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(upstream.AbsoluteUri)));
                var cached = Path.Combine(_cacheRoot, key);
                string? contentType;
                var gate = _cacheLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(_lifetime.Token);
                try
                {
                    if (!File.Exists(cached))
                    {
                        (contentType, _) = await _broker.DownloadAsync(upstream, cached, 512L * 1024 * 1024, _lifetime.Token);
                        if (IsJson(contentType, upstream)) await RewriteJsonAsync(cached);
                    }
                    else contentType = null;
                }
                finally { gate.Release(); }
                await WriteFileAsync(stream, cached, contentType ?? ContentType(upstream), _lifetime.Token);
            }
            catch (BuilderDownloadException exception) when (exception.StatusCode is >= 400 and <= 599)
            {
                await WriteStatusAsync(stream, exception.StatusCode);
            }
            catch
            {
                await WriteStatusAsync(stream, 502);
            }
        }
    }

    private async Task RewriteJsonAsync(string path)
    {
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path, _lifetime.Token)) ?? throw new InvalidDataException("NuGet returned an empty service index.");
        Rewrite(node);
        await File.WriteAllTextAsync(path, node.ToJsonString(), _lifetime.Token);
        void Rewrite(JsonNode current)
        {
            if (current is JsonObject obj)
            {
                foreach (var item in obj.ToArray())
                {
                    if (item.Value is JsonValue value && value.TryGetValue<string>(out var text) && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                        obj[item.Key] = LocalUpstream(uri);
                    else if (item.Value is not null) Rewrite(item.Value);
                }
            }
            else if (current is JsonArray array)
                foreach (var item in array) if (item is not null) Rewrite(item);
        }
    }

    private string LocalUpstream(Uri uri)
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(uri.AbsoluteUri)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return new Uri(new Uri(ServiceIndexUrl), "/upstream/" + token + "/").AbsoluteUri;
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") },
            true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddMinutes(-5), now.AddHours(2));
    }

    internal static Uri DecodeUpstream(string path)
    {
        const string prefix = "/upstream/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("The NuGet proxy path is invalid.");
        var target = path[prefix.Length..];
        var queryIndex = target.IndexOf('?');
        var query = queryIndex >= 0 ? target[queryIndex..] : string.Empty;
        if (queryIndex >= 0) target = target[..queryIndex];
        var separator = target.IndexOf('/');
        var suffix = separator >= 0 ? target[(separator + 1)..] : string.Empty;
        var token = (separator >= 0 ? target[..separator] : target).Replace('-', '+').Replace('_', '/');
        token = token.PadRight((token.Length + 3) / 4 * 4, '=');
        var value = Encoding.UTF8.GetString(Convert.FromBase64String(token));
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("The NuGet upstream URL is invalid.");
        if (suffix.Length == 0) return uri;
        var baseUri = uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? uri : new Uri(uri.AbsoluteUri + "/");
        return new Uri(baseUri, suffix + query);
    }

    private static async Task<string> ReadGetPathAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var header = new MemoryStream();
        var state = 0;
        while (header.Length < 32 * 1024 && state != 4)
        {
            var value = new byte[1];
            if (await stream.ReadAsync(value, cancellationToken) == 0) throw new EndOfStreamException();
            header.WriteByte(value[0]);
            state = (state, value[0]) switch { (0, 13) => 1, (1, 10) => 2, (2, 13) => 3, (3, 10) => 4, (_, 13) => 1, _ => 0 };
        }
        var first = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", 2)[0].Split(' ');
        if (first.Length != 3 || first[0] != "GET" || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) throw new InvalidDataException("Only NuGet GET requests are supported.");
        return first[1];
    }

    private static async Task WriteFileAsync(Stream stream, string path, string contentType, CancellationToken cancellationToken)
    {
        var length = new FileInfo(path).Length;
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        await input.CopyToAsync(stream, cancellationToken);
    }

    private static async Task WriteStatusAsync(Stream stream, int statusCode)
    {
        var reason = statusCode switch { 404 => "Not Found", 401 => "Unauthorized", 403 => "Forbidden", 429 => "Too Many Requests", _ => "Bad Gateway" };
        var message = Encoding.ASCII.GetBytes($"HTTP/1.1 {statusCode} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        try { await stream.WriteAsync(message); } catch { }
    }

    private static string ContentType(Uri uri) => uri.AbsolutePath.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) ? "application/octet-stream" : "application/json";
    private static bool IsJson(string? contentType, Uri uri) =>
        contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true ||
        uri.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
        uri.AbsolutePath.EndsWith("/index.json", StringComparison.OrdinalIgnoreCase);
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        _listener.Stop();
        try { await _loop; } catch (SocketException) { }
        _certificate.Dispose();
        _lifetime.Dispose();
    }
}
