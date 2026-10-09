using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace CSweet.Infrastructure.Core;

/// <summary>The shared comment is the source. Private chat and consent details never enter recall.</summary>
internal static class WorkInstructionMemorySource
{
    internal const string Type = "work-instruction";
    internal static string Checksum(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    internal static Guid EpisodeId(Guid comment, long revision) => new(SHA256.HashData(
        Encoding.UTF8.GetBytes($"work-instruction-v1:{comment:D}:{revision.ToString(CultureInfo.InvariantCulture)}")).AsSpan(0, 16));

    internal static async Task CaptureAsync(CSweetDbContext db, Guid commentId, CancellationToken token)
    {
        var transaction = db.Database.CurrentTransaction ?? throw new InvalidOperationException("Instruction memory must commit with its comment.");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"WorkItemComments\" WHERE \"Id\"={commentId} FOR SHARE NOWAIT", token);
        var publication = await db.WorkInstructionPublications.AsNoTracking().SingleAsync(x => x.CommentId == commentId, token);
        var comment = await db.WorkItemComments.AsNoTracking().SingleAsync(x => x.Id == commentId, token);
        if (comment.DeletedAt is not null) return;
        var item = await db.CoreWorkTasks.AsNoTracking().SingleAsync(x => x.Id == publication.WorkItemId && x.OrganizationId == publication.OrganizationId, token);
        var installation = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.Id == publication.SourceEmployeeId &&
            x.OrganizationId == publication.OrganizationId).Select(x => x.AgentInstallationId).SingleAsync(token)
            ?? throw new UnauthorizedAccessException();
        var ns = EmployeeMemoryNamespaces.Case(publication.OrganizationId.ToString("D"), item.Id.ToString("D"), "csweet");
        var episode = new MemoryEpisode(EpisodeId(comment.Id, comment.Revision), ns.Partition, ns.Scope, comment.Body, "text/plain",
            new(Type, EpisodeId(comment.Id, comment.Revision).ToString("D"), publication.ActorOrganizationUserId.ToString("D")), Checksum(comment.Body),
            comment.EditedAt ?? comment.CreatedAt, DateTimeOffset.UtcNow,
            IdempotencyKey: $"work-instruction:{comment.Id:D}:{comment.Revision}", Sensitivity: MemorySensitivity.Internal,
            Metadata: new Dictionary<string, string> { ["publicationId"] = publication.Id.ToString("D"),
                ["workItemId"] = item.Id.ToString("D"), ["commentId"] = comment.Id.ToString("D"), ["commentRevision"] = comment.Revision.ToString(CultureInfo.InvariantCulture),
                ["installationId"] = installation.ToString("D") });
        if (!await MatchesAsync(db, episode, token)) throw new UnauthorizedAccessException();
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE csweet_memory_episodes, csweet_memory_transfers, csweet_memory_revisions, csweet_memory_claims, csweet_memory_entities, csweet_memory_edges, csweet_memory_procedures IN SHARE ROW EXCLUSIVE MODE", token);
        await using var memory = new PostgreSqlMemoryStore((NpgsqlTransaction)transaction.GetDbTransaction());
        var result = await memory.AppendEpisodeAsync(episode, token);
        var retained = await memory.GetEpisodeAsync(ns.Partition, result.Id, token) ?? throw new InvalidOperationException("Instruction source was not retained.");
        // A replay cannot revive suppression or rewrite accepted enrichment evidence.
        if (!MemoryProvenance.IsCurrent(retained, ns.Partition, result.Id, DateTimeOffset.UtcNow)) return;
        await AgentMemoryService.StageEpisodeJobAsync(db, retained, publication.OrganizationId, publication.SourceEmployeeId,
            installation, publication.ActorApplicationUserId, token);
    }

    internal static async Task<bool> MatchesAsync(CSweetDbContext db, MemoryEpisode episode, CancellationToken token, bool retained = false)
    {
        if (episode.Source.Type != Type || episode.Source.Id != episode.Id.ToString("D") ||
            episode.Metadata is not { Count: 5 } metadata || !metadata.TryGetValue("commentId", out var rawComment) ||
            !Guid.TryParseExact(rawComment, "D", out var commentId) || !metadata.TryGetValue("publicationId", out var raw) ||
            !Guid.TryParseExact(raw, "D", out var receiptId) || !metadata.TryGetValue("commentRevision", out var rawRevision) ||
            !long.TryParse(rawRevision, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 1 ||
            !metadata.TryGetValue("workItemId", out var rawItem) || !Guid.TryParseExact(rawItem, "D", out var itemId) ||
            !metadata.TryGetValue("installationId", out var rawInstallation) || !Guid.TryParseExact(rawInstallation, "D", out _) ||
            episode.Id != EpisodeId(commentId, revision) || episode.TransferEvidence is not null || episode.CorrectionEvidence is not null ||
            episode.OperationalReferences is not null || episode.Sensitivity != MemorySensitivity.Internal ||
            episode.ContentType != "text/plain" || episode.Content.Length is < 1 or > 8192 ||
            episode.Checksum != Checksum(episode.Content) || episode.IdempotencyKey != $"work-instruction:{commentId:D}:{revision}") return false;
        var receipt = await db.WorkInstructionPublications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == receiptId && x.CommentId == commentId, token);
        if (receipt is null || receipt.WorkItemId != itemId || episode.Source.Author != receipt.ActorOrganizationUserId.ToString("D")) return false;
        var ns = EmployeeMemoryNamespaces.Case(receipt.OrganizationId.ToString("D"), itemId.ToString("D"), "csweet");
        if (episode.Partition != ns.Partition || episode.Scope != ns.Scope) return false;
        // Retained job evidence supplies the historical checksum; consent supplies identity/ownership.
        // This mode is only for erasure, never a recall or enrichment authorization.
        if (retained) return true;
        if (db.Database.CurrentTransaction is not null)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"WorkItemComments\" WHERE \"Id\"={commentId} FOR SHARE NOWAIT", token);
        var comment = await db.WorkItemComments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == commentId, token);
        return comment is not null && comment.OrganizationId == receipt.OrganizationId && comment.WorkItemId == itemId &&
            comment.Kind == "human.instruction" && comment.AuthorKind == GrantSubjectKind.OrganizationUser &&
            comment.AuthorSubjectId == receipt.ActorOrganizationUserId && comment.CausationId == receipt.Id.ToString("D") &&
            comment.ArtifactDigest == receipt.InstructionChecksum && comment.DeletedAt is null && comment.Revision == revision &&
            comment.Body == episode.Content && (comment.EditedAt ?? comment.CreatedAt) == episode.OccurredAt &&
            (revision != 1 || episode.Checksum == receipt.InstructionChecksum);
    }
}
