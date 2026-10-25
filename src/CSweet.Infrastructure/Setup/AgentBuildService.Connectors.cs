using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;

namespace CSweet.Infrastructure.Setup;

public sealed partial class AgentBuildService
{
    internal const string ConnectorArtifactFormat = "connector-declarations.v1";

    private async Task VerifyConnectorAsync(AgentBuildJob job, CancellationToken ct)
    {
        var package = job.PackageVersion!;
        job.ExecutionPoolId = null;
        job.StepsJson = AgentBuildStepStore.CreateConnectorInitialJson(job.QueuedAt);
        if (package.Status != AgentPackageVersionStatus.Approved)
        {
            job.FailureMessage = $"Verification cancelled because the package version is {package.Status}.";
            job.TransitionTo(AgentBuildStatus.Cancelled, DateTimeOffset.UtcNow);
            await _dbContext.SaveChangesAsync(ct);
            await WriteAuditAsync(job, "connector-package.cancelled", job.FailureMessage, ct);
            return;
        }

        try
        {
            job.TransitionTo(AgentBuildStatus.Cloning, DateTimeOffset.UtcNow);
            await _dbContext.SaveChangesAsync(ct);
            await AgentBuildStepStore.ApplyAsync(_dbContext, job,
                new(AgentBuildStepKeys.Queued, AgentBuildStepStatuses.Succeeded), ct);
            await AgentBuildStepStore.ApplyAsync(_dbContext, job,
                new(AgentBuildStepKeys.Verify, AgentBuildStepStatuses.InProgress), ct);

            var digest = VerifyConnectorDeclarations(package);
            job.TransitionTo(AgentBuildStatus.Building, DateTimeOffset.UtcNow);
            await AgentBuildStepStore.CompleteRemainingAsync(_dbContext, job, ct);
            package.PackageDigest = digest;
            package.PackagePath = null;
            package.ArtifactSignature = null; // No executable artifact was built or signed.
            package.ArtifactFormatVersion = ConnectorArtifactFormat;
            package.ArtifactOperatingSystem = "none";
            package.ArtifactArchitecture = "none";
            package.BuiltAt = DateTimeOffset.UtcNow;
            package.Status = AgentPackageVersionStatus.Built;
            job.PackageDigest = digest;
            job.PackagePath = null;
            job.FailureMessage = null;
            job.TransitionTo(AgentBuildStatus.Succeeded, package.BuiltAt.Value);
            await _dbContext.SaveChangesAsync(ct);
            await WriteAuditAsync(job, "connector-package.verified",
                $"Verified reviewed connector declarations {digest}; no plugin code was built or executed.", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await CancelAsync(job, package, "Connector verification was interrupted.");
            throw;
        }
        catch (Exception exception)
        {
            await AgentBuildStepStore.FailCurrentAsync(_dbContext, job, exception.Message, AgentBuildStepKeys.Verify);
            await FailAsync(job, package, exception.Message);
        }
    }

    // Freeze the exact approved manifest AND its immutable source identity. This is not a
    // shortcut for arbitrary plugin code: the validator permits only closed broker mappings.
    internal static string VerifyConnectorDeclarations(AgentPackageVersion package)
    {
        if (package.PluginKind != PluginKind.Connector || package.PackageSource is not { } source ||
            !Regex.IsMatch(package.CommitSha, "\\A[0-9a-fA-F]{40}(?:[0-9a-fA-F]{24})?\\z"))
            throw new AgentBuildException("Connector verification requires an immutable reviewed source commit.");
        var bytes = Encoding.UTF8.GetBytes(package.ManifestJson);
        var manifestDigest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(manifestDigest, package.ManifestDigest, StringComparison.Ordinal))
            throw new AgentBuildException("The connector manifest changed after review. Preview and approve the package again.");
        var envelope = new PluginManifestReader().Read(bytes, package.ManifestFileName);
        var manifest = JsonSerializer.Deserialize<PluginManifest>(envelope.ManifestJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        AgentImportPreviewService.ValidateManifest(manifest);
        if (envelope.Kind != "connector" || manifest.Id != package.AgentId || manifest.Version != package.Version ||
            manifest.Name != package.AgentName || manifest.Publisher.Id != package.PublisherId ||
            manifest.Publisher.Name != package.PublisherName ||
            AgentImportPreviewService.CapabilityDescriptorsDigest(manifest) != package.CapabilityDescriptorsDigest)
            throw new AgentBuildException("The connector identity or capabilities do not match the reviewed package.");
        var repository = GitHubRepositoryUrlNormalizer.Normalize(source.RepositoryUrl);
        var identity = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = ConnectorArtifactFormat, repository = repository.RepositoryUrl,
            commit = package.CommitSha.ToLowerInvariant(), manifest = manifestDigest
        });
        return Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant();
    }
}
