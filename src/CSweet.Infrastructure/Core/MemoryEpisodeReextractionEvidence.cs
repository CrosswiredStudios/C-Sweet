using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

internal static class MemoryEpisodeReextractionEvidence
{
    internal static string Hash(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value))).ToLowerInvariant();
    internal static string? AcceptedHash(MemoryEpisodeEnrichmentJob job) => job.AcceptedExtractionJson is null ? null :
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(job.AcceptedExtractionJson))).ToLowerInvariant();
    // Superseding changes only SupersededAt. Every other predecessor field is frozen.
    internal static string JobHash(MemoryEpisodeEnrichmentJob job) => Hash(new { job.Id, job.OrganizationId, job.EmployeeId,
        job.InstallationId, job.ReviewerApplicationUserId, job.EpisodeId, job.InputGeneration, job.PreviousJobId, job.SourceHash,
        job.Status, job.Attempts, job.RetryGeneration, job.CreatedAt, job.NextAttemptAt, job.LastAttemptAt, job.CompletedAt,
        job.LastError, job.LeaseToken, job.LeaseExpiresAt, job.ExtractionAcceptedAt, AcceptedHash = AcceptedHash(job) });

    internal static async Task<bool> VerifyArchivedAsync(CSweetDbContext db, MemoryEpisodeEnrichmentJob original, CancellationToken token)
    {
        if (original.SupersededAt is null || original.InputGeneration is < 0 or >= 8 ||
            original.Status is not (MemoryCaptureStatus.Failed or MemoryCaptureStatus.Completed) || original.LeaseToken is not null)
            return false;
        var receipt = await db.MemoryEpisodeReextractionReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.PreviousJobId == original.Id, token);
        if (receipt is null || receipt.OrganizationId != original.OrganizationId || receipt.EmployeeId != original.EmployeeId ||
            receipt.EpisodeId != original.EpisodeId || receipt.InputGeneration != original.InputGeneration + 1 ||
            receipt.PreviousJobHash != JobHash(original) || receipt.PreviousAcceptedHash != AcceptedHash(original) ||
            receipt.CreatedAt != original.SupersededAt) return false;
        return await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().AnyAsync(x => x.Id == receipt.JobId &&
            x.PreviousJobId == original.Id && x.InputGeneration == receipt.InputGeneration && x.OrganizationId == original.OrganizationId &&
            x.EmployeeId == original.EmployeeId && x.InstallationId == original.InstallationId &&
            x.ReviewerApplicationUserId == original.ReviewerApplicationUserId && x.EpisodeId == original.EpisodeId && x.SourceHash == receipt.SourceHash, token);
    }
}
