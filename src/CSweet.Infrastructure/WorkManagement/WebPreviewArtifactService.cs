using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Isolation.Security;
using CSweet.WorkManagement.Contracts;
using CSweet.WebHost.Contracts;
using Microsoft.EntityFrameworkCore;
namespace CSweet.Infrastructure.WorkManagement;

public sealed record PreparedWebPreviewArtifact(Guid OrganizationId, Guid ProjectId, Guid RepositoryId,
    Guid BuildId, string SourceRevision, string SourceArtifactDigest, string Digest, Stream Content, long BuildRevision = 0, string OutputsJson = "", Guid AssignmentId = default) : IDisposable
{
    public long Length => Content.Length;
    public void Dispose() => Content.Dispose();
}

/// <summary>Prepares data only. The dispatcher must separately authorize the employee, grant and destination.
/// No build commands or product processes run in Headquarters.</summary>
public sealed class WebPreviewArtifactService(CSweetDbContext db, IAgentArtifactStore store)
{
    // Bound transient in-memory copies independently of how many dispatch requests arrive.
    private static readonly SemaphoreSlim PreparationSlot = new(1, 1);
    private const long MaximumSourceArchiveBytes = 512L * 1024 * 1024;
    private static readonly DateTimeOffset ZipTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public Task<PreparedWebPreviewArtifact> PrepareStaticAsync(Guid organizationId, Guid projectId, Guid repositoryId,
        Guid buildId, string sourceRevision, CancellationToken token) => PrepareAsync(organizationId, projectId, repositoryId, buildId, sourceRevision, PreviewMode.Static, token);
    public async Task<PreparedWebPreviewArtifact> PrepareAsync(Guid organizationId, Guid projectId,
        Guid repositoryId, Guid buildId, string sourceRevision, PreviewMode mode, CancellationToken token)
    {
        if (!Enum.IsDefined(mode) || organizationId == Guid.Empty || projectId == Guid.Empty || repositoryId == Guid.Empty || buildId == Guid.Empty ||
            (sourceRevision is not { Length: 40 or 64 } || !sourceRevision.All(char.IsAsciiHexDigit)))
            throw new ArgumentException("An exact organization, project, repository, build and source revision are required.");
        await PreparationSlot.WaitAsync(token);
        try
        {
            var build = await db.DeliveryBuilds.AsNoTracking().SingleOrDefaultAsync(x => x.Id == buildId &&
                x.OrganizationId == organizationId && x.WorkstreamId == projectId && x.RepositoryId == repositoryId, token)
                ?? throw new UnauthorizedAccessException("The preview build is outside the requested scope.");
            if (build.Status != DeliveryBuildStatuses.Succeeded || build.SourceRevision != sourceRevision ||
                build.OutputsJson.Length > 2 * 1024 * 1024)
                throw new InvalidDataException("The preview requires a successful build at the exact source revision.");
            await RequireRepositoryAsync(organizationId, repositoryId, token);
            var assignment = await db.ExecutionWorkloadAssignments.AsNoTracking().SingleOrDefaultAsync(x =>
                x.DeliveryBuildId == buildId && x.WorkloadKind == ExecutionWorkloadKind.ToolchainBuild &&
                x.ResultArtifactDigest != null && x.Status == ExecutionAssignmentStatus.Completed, token) ?? throw new InvalidDataException("The build has no ingested output artifact.");
            if (!Guid.TryParse(assignment.BusinessId, out var businessId) || businessId != organizationId ||
                !WorkloadAuthorizationEnvelope.IsDigest(assignment.ResultArtifactDigest))
                throw new InvalidDataException("The ingested build artifact does not have the expected business and content identity.");
            var manifest = JsonSerializer.Deserialize<IReadOnlyList<BuildOutputManifestEntry>>(build.OutputsJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
            if (manifest.Any(x => x is null || x.RelativePath is null || x.Sha256 is null || x.ContentType is null))
                throw new InvalidDataException("The stored build output manifest is incomplete.");
            await using var source = await store.OpenReadAsync(assignment.ResultArtifactDigest!, token);
            using var verified = new BoundedDigestReadStream(source, MaximumSourceArchiveBytes);
            var bundle = await WebPreviewBundle.ReadAsync(verified, manifest, token, requireStaticIndex: mode == PreviewMode.Static);
            await verified.VerifyCompleteAsync(assignment.ResultArtifactDigest!, token);
            var result = new MemoryStream();
            try
            {
                using (var zip = new ZipArchive(result, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var file in bundle.Files.OrderBy(x => x.Key, StringComparer.Ordinal))
                    {
                        token.ThrowIfCancellationRequested();
                        if (!names.Add(file.Key) || file.Key.Split('/').Any(x => x.EndsWith('.') || x.EndsWith(' ')))
                            throw new InvalidDataException("The output paths are not safe for a product artifact.");
                                                if (mode == PreviewMode.Containers && !(file.Key.StartsWith("source/", StringComparison.Ordinal) ||
                            file.Key.StartsWith("images/", StringComparison.Ordinal) && file.Key.EndsWith(".tar", StringComparison.Ordinal) && file.Key.Count(c => c == '/') == 1))
                            throw new InvalidDataException("Container build outputs may contain only source/ files and images/*.tar inputs.");
                        var entry = zip.CreateEntry(mode == PreviewMode.Static ? "site/" + file.Key : file.Key, CompressionLevel.NoCompression);
                        entry.LastWriteTime = ZipTimestamp;
                        entry.ExternalAttributes = unchecked((int)0x81a40000); // Regular file, 0644; never a symlink or executable hook.
                        await using var output = entry.Open();
                        await output.WriteAsync(file.Value.Content, token);
                    }
                }
                // ZIP headers are bounded by the already-validated file count and path lengths.
                if (result.Length > WebPreviewBundle.MaximumTotalBytes + 16L * 1024 * 1024)
                    throw new InvalidDataException("The product archive exceeds its transfer budget.");
                result.Position = 0;
                var digest = "sha256:" + Convert.ToHexStringLower(await SHA256.HashDataAsync(result, token));
                result.Position = 0;
                // A build cancellation, repository archive or changed output record during preparation invalidates this candidate.
                if (!await db.DeliveryBuilds.AsNoTracking().AnyAsync(x => x.Id == buildId && x.OrganizationId == organizationId &&
                    x.WorkstreamId == projectId && x.RepositoryId == repositoryId && x.SourceRevision == sourceRevision &&
                    x.Status == DeliveryBuildStatuses.Succeeded && x.Revision == build.Revision && x.OutputsJson == build.OutputsJson, token) ||
                    !await db.ExecutionWorkloadAssignments.AsNoTracking().AnyAsync(x => x.Id == assignment.Id &&
                        x.DeliveryBuildId == buildId && x.ResultArtifactDigest == assignment.ResultArtifactDigest &&
                        x.BusinessId == assignment.BusinessId && x.WorkloadKind == ExecutionWorkloadKind.ToolchainBuild &&
                        x.Status == ExecutionAssignmentStatus.Completed, token))
                    throw new InvalidOperationException("The build evidence changed during product preparation.");
                await RequireRepositoryAsync(organizationId, repositoryId, token);
                return new(organizationId, projectId, repositoryId, buildId, sourceRevision, assignment.ResultArtifactDigest!, digest, result, build.Revision, build.OutputsJson, assignment.Id);
            }
            catch { result.Dispose(); throw; }
        }
        finally { PreparationSlot.Release(); }
    }

    private async Task RequireRepositoryAsync(Guid organizationId, Guid repositoryId, CancellationToken token)
    {
        if (!await db.SourceControlRepositories.AsNoTracking().AnyAsync(x => x.Id == repositoryId &&
            x.OrganizationId == organizationId && x.ArchivedAt == null, token))
            throw new UnauthorizedAccessException("The source repository is no longer available.");
    }
}
