using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Office.Contracts.Workloads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.Setup;

/// <summary>
/// Installs a tagged GitHub release <c>.csab</c> bundle as the immutable agent
/// package without queueing a fleet source build. The download is bounded, the
/// SHA-256 digest is checked, and the bundle passes through the same
/// <see cref="IAgentArtifactStore.ImportAsync"/> validation gate as
/// builder-produced artifacts before the version is marked Built.
/// </summary>
public sealed class PrebuiltBundleInstallService(
    CSweetDbContext db,
    HttpClient httpClient,
    IAgentArtifactStore artifactStore,
    IGitHubAgentRepositoryClient repositoryClient,
    IAuditEventWriter auditWriter,
    ILogger<PrebuiltBundleInstallService>? logger = null)
{
    private const long MaximumBundleBytes = 2L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<Guid> InstallAsync(
        Guid packageVersionId,
        CancellationToken cancellationToken = default)
    {
        var package = await db.AgentPackageVersions
            .Include(x => x.PackageSource)
            .SingleOrDefaultAsync(x => x.Id == packageVersionId, cancellationToken)
            ?? throw new AgentBuildException("The agent package version was not found.");
        if (package.ReleaseTag is null || package.ReleaseAssetUrl is null || package.ReleaseAssetName is null)
            throw new AgentBuildException("The package version has no prebuilt release bundle.");
        if (package.Status == AgentPackageVersionStatus.Built &&
            !string.IsNullOrWhiteSpace(package.PackageDigest))
        {
            var existing = await db.AgentBuildJobs
                .Where(x => x.PackageVersionId == packageVersionId)
                .OrderByDescending(x => x.Attempt)
                .Select(x => x.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (existing != Guid.Empty) return existing;
        }

        var now = DateTimeOffset.UtcNow;
        var job = new AgentBuildJob
        {
            Id = Guid.NewGuid(),
            PackageVersionId = package.Id,
            Attempt = await db.AgentBuildJobs
                .Where(x => x.PackageVersionId == package.Id)
                .Select(x => (int?)x.Attempt)
                .MaxAsync(cancellationToken) is { } max
                ? max + 1
                : 1,
            QueuedAt = now,
            StepsJson = AgentBuildStepStore.CreatePrebuiltInitialJson(now)
        };
        // Claim the inline install before the source-build worker can see it.
        job.TransitionTo(AgentBuildStatus.Cloning, now);
        db.AgentBuildJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);

        var progress = new PersistedAgentBuildProgressReporter(db, job);
        try
        {
            await progress.ReportAsync(
                new AgentBuildProgressUpdate(AgentBuildStepKeys.Queued, AgentBuildStepStatuses.Succeeded),
                cancellationToken);
            await progress.ReportAsync(
                new AgentBuildProgressUpdate(
                    AgentBuildStepKeys.Download,
                    AgentBuildStepStatuses.InProgress,
                    $"Downloading {package.ReleaseAssetName} from release {package.ReleaseTag}."),
                cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            var expectedDigest = await ResolveExpectedDigestAsync(package, cancellationToken);
            await using var bundle = await DownloadBoundedAsync(
                package.ReleaseAssetUrl, job, progress, cancellationToken);

            await progress.ReportAsync(
                new AgentBuildProgressUpdate(
                    AgentBuildStepKeys.Download,
                    AgentBuildStepStatuses.Succeeded,
                    "Release bundle downloaded."),
                cancellationToken);
            await progress.ReportAsync(
                new AgentBuildProgressUpdate(
                    AgentBuildStepKeys.Verify,
                    AgentBuildStepStatuses.InProgress,
                    "Checking the bundle digest and validating the package."),
                cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            bundle.Position = 0;
            job.TransitionTo(AgentBuildStatus.Building, DateTimeOffset.UtcNow);
            await progress.ReportAsync(
                new AgentBuildProgressUpdate(
                    AgentBuildStepKeys.Install,
                    AgentBuildStepStatuses.InProgress,
                    "Sealing the immutable agent package."),
                cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            AgentArtifactReference reference;
            try
            {
                reference = await artifactStore.ImportAsync(
                    bundle,
                    new ArtifactImportDescriptor(
                        expectedDigest,
                        MaximumBundleBytes,
                        package.ArtifactFormatVersion,
                        package.ArtifactOperatingSystem,
                        package.ArtifactArchitecture,
                        ProvenanceJson(package)),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new AgentBuildException(
                    $"The release bundle could not be validated and sealed into the artifact store: {Sanitize(exception.Message)}",
                    AgentBuildStepKeys.Install,
                    exception);
            }

            // Package rows use bare SHA-256 hex, as in FleetAgentBuildExecutor.
            // Artifact references and release provenance retain the sha256: prefix.
            var packageDigest = reference.Digest.StartsWith("sha256:", StringComparison.Ordinal)
                ? reference.Digest[7..]
                : reference.Digest;
            job.PackageDigest = packageDigest;
            job.TransitionTo(AgentBuildStatus.Succeeded, DateTimeOffset.UtcNow);
            package.PackageDigest = packageDigest;
            package.ArtifactSignature = reference.Signature;
            package.ArtifactFormatVersion = reference.FormatVersion;
            package.ArtifactOperatingSystem = reference.OperatingSystem;
            package.ArtifactArchitecture = reference.Architecture;
            package.ReleaseBundleDigest = reference.Digest;
            package.BuiltAt = job.CompletedAt;
            package.Status = AgentPackageVersionStatus.Built;
            await AgentBuildStepStore.CompleteRemainingAsync(db, job, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var message = Sanitize(exception.Message);
            var stepKey = (exception as AgentBuildException)?.StepKey;
            // A failed completion save leaves tracked entities Built/Succeeded. Reload
            // before recording failure so invalid pending values are not saved again.
            await db.Entry(job).ReloadAsync(CancellationToken.None);
            await db.Entry(package).ReloadAsync(CancellationToken.None);
            await AgentBuildStepStore.FailCurrentAsync(db, job, message, stepKey);
            job.FailureMessage = message;
            job.TransitionTo(AgentBuildStatus.Failed, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(CancellationToken.None);
            logger?.LogWarning(exception,
                "Prebuilt release install failed for package version {PackageVersionId}.",
                package.Id);
            throw new AgentBuildException($"The prebuilt release bundle could not be installed: {message}", exception);
        }

        // Audit errors must not turn an installed package into a source-build fallback.
        try
        {
            await auditWriter.WriteAsync(
                "agent-build.prebuilt-installed",
                nameof(AgentPackageVersion),
                package.Id,
                $"Installed prebuilt release {package.ReleaseTag} ({package.ReleaseAssetName}) as {package.ReleaseBundleDigest}.",
                cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger?.LogWarning(exception, "Could not audit prebuilt install for package {PackageVersionId}.", package.Id);
        }
        return job.Id;
    }

    private async Task<string> ResolveExpectedDigestAsync(
        AgentPackageVersion package,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(package.ReleaseBundleDigest))
            return package.ReleaseBundleDigest!;
        var source = package.PackageSource
            ?? throw new AgentBuildException("The build job package source was not loaded.");
        GitHubReleaseInfo? release;
        try
        {
            release = await repositoryClient.GetLatestReleaseAsync(
                source.RepositoryOwner, source.RepositoryName, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new AgentBuildException(
                "The release checksum could not be resolved; refusing to install an unverified bundle.",
                exception);
        }

        var sidecarUrl = release?.Assets.FirstOrDefault(x =>
            string.Equals(x.Name, package.ReleaseAssetName + ".sha256", StringComparison.OrdinalIgnoreCase))
            ?.BrowserDownloadUrl;
        if (sidecarUrl is null)
            throw new AgentBuildException(
                "The release has no .sha256 checksum sidecar; refusing to install an unverified bundle.");
        var text = await DownloadTextBoundedAsync(sidecarUrl, 4096, cancellationToken);
        var digest = ParseChecksumSidecar(text);
        package.ReleaseBundleDigest = digest;
        await db.SaveChangesAsync(cancellationToken);
        return digest;
    }

    private async Task<MemoryStream> DownloadBoundedAsync(
        string url,
        AgentBuildJob job,
        PersistedAgentBuildProgressReporter progress,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new AgentBuildException($"The release bundle download failed with {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength > MaximumBundleBytes)
            throw new AgentBuildException("The release bundle exceeds the 2 GiB limit.");

        var output = new MemoryStream();
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[81920];
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                if (output.Length + read > MaximumBundleBytes)
                    throw new AgentBuildException("The release bundle exceeds the 2 GiB limit.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        catch
        {
            await output.DisposeAsync();
            throw;
        }

        job.LogPath = null;
        await progress.ReportAsync(
            new AgentBuildProgressUpdate(
                AgentBuildStepKeys.Download,
                AgentBuildStepStatuses.InProgress,
                $"Downloaded {output.Length / 1024 / 1024} MiB."),
            cancellationToken);
        return output;
    }

    private async Task<string> DownloadTextBoundedAsync(
        string url,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new AgentBuildException("The release checksum sidecar could not be downloaded.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > maximumBytes)
                throw new AgentBuildException("The release checksum sidecar is oversized.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return Encoding.ASCII.GetString(output.ToArray());
    }

    private static string ParseChecksumSidecar(string text)
    {
        // Accepts "<hex>  <filename>" (as written by pack-csab.py) or a bare hex digest.
        var token = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (token is not null && token.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            token = token["sha256:".Length..];
        if (token is not { Length: 64 } || !token.All(Uri.IsHexDigit))
            throw new AgentBuildException("The release checksum sidecar is not a valid SHA-256 digest.");
        return "sha256:" + token.ToLowerInvariant();
    }

    private static string ProvenanceJson(AgentPackageVersion package) =>
        JsonSerializer.Serialize(new
        {
            source = "github-release",
            tag = package.ReleaseTag,
            asset = package.ReleaseAssetName,
            url = package.ReleaseAssetUrl,
            repositoryUrl = package.PackageSource?.RepositoryUrl
        }, JsonOptions);

    private static string Sanitize(string value)
    {
        value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length > 1200 ? value[..1200] : value;
    }
}
