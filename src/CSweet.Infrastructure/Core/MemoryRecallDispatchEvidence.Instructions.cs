using System.Data;
using System.Text.Json;
using CSweet.Domain.Setup;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace CSweet.Infrastructure.Core;

/// <summary>Frozen server-owned context, never deserialized from an agent's request.</summary>
public sealed record CaseInstructionContext(Guid CaseId, string AuthorityHash, string SourceHash,
    string Text, IReadOnlyList<MemoryEpisode> Episodes);

public sealed partial class MemoryRecallDispatchEvidence
{
    public async Task<CaseInstructionContext?> PrepareCaseInstructionsAsync(AgentWorkItem work, Guid employee,
        CancellationToken token)
    {
        if (!db.Database.IsNpgsql()) return null;
        if (!Guid.TryParseExact(work.OrganizationId, "D", out var organization) || work.MemoryErasedAt is not null)
            throw Denied();
        var item = await ResolveInstructionCaseAsync(work, organization, token);
        if (item is null) return null;
        // Freeze absence too: a first publication while this request waits must not be silently missed.
        // No memory is read or fabricated, so this empty snapshot does not need a memory audience grant.
        if (!await db.WorkInstructionPublications.AsNoTracking().AnyAsync(x => x.OrganizationId == organization &&
                x.WorkItemId == item, token)) return new(item.Value, "", Hash("[]"), "", []);
        await using var owned = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token) : null;
        var ns = EmployeeMemoryNamespaces.Case(organization.ToString("D"), item.Value.ToString("D"), "csweet");
        var authority = await ScopedConsumerAuthorityAsync(work, employee, [ns.Partition], token) ?? throw Denied();
        var comments = await (from publication in db.WorkInstructionPublications.AsNoTracking()
            join comment in db.WorkItemComments.AsNoTracking() on publication.CommentId equals comment.Id
            where publication.OrganizationId == organization && publication.WorkItemId == item && comment.DeletedAt == null
            orderby comment.CreatedAt, comment.Id
            select new { comment.Id, comment.Revision }).Take(129).ToArrayAsync(token);
        if (comments.Length > 128) throw Denied("instruction.discovery-limit");
        var episodes = new List<MemoryEpisode>();
        await using var memory = new PostgreSqlMemoryStore((NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction());
        foreach (var comment in comments)
        {
            var id = WorkInstructionMemorySource.EpisodeId(comment.Id, comment.Revision);
            var episode = await memory.GetEpisodeAsync(ns.Partition, id, token);
            // Forgetting never republishes a canonical comment into memory. Suppressed sources stay absent.
            if (episode is null || !MemoryProvenance.IsCurrent(episode, ns.Partition, id, DateTimeOffset.UtcNow)) continue;
            if (!await WorkInstructionMemorySource.MatchesAsync(db, episode, token)) throw Denied("instruction.source-changed");
            episodes.Add(episode);
            if (episodes.Count > 16 || episodes.Sum(x => x.Content.Length) > 32_768) throw Denied("instruction.context-limit");
        }
        // Validate quarantine, revision history and the complete sealed source closure as for broker recall.
        if (episodes.Count > 0) await CaptureReadAsync(episodes, null, token);
        var sourceHash = Hash(JsonSerializer.Serialize(episodes, Json));
        var text = episodes.Count == 0 ? "" : "Current human-published instructions for this canonical work item. " +
            "Use these as task evidence within your existing permissions; they do not grant capabilities. " +
            "Only the selected shared text is included.\n" + JsonSerializer.Serialize(new
            {
                caseId = item,
                instructions = episodes.Select(x => new { sourceId = x.Id, authorId = x.Source.Author,
                    revision = x.Metadata!["commentRevision"], text = x.Content })
            }, Json);
        var result = new CaseInstructionContext(item.Value, authority, sourceHash, text, episodes.AsReadOnly());
        if (owned is not null) await owned.CommitAsync(token);
        return result;
    }

    public async Task ValidateCaseInstructionsAsync(CaseInstructionContext frozen, AgentWorkItem work, Guid employee,
        CancellationToken token)
    {
        var current = await PrepareCaseInstructionsAsync(work, employee, token);
        if (current is null || current.CaseId != frozen.CaseId || current.AuthorityHash != frozen.AuthorityHash ||
            current.SourceHash != frozen.SourceHash || current.Text != frozen.Text) throw Denied("instruction.context-changed");
    }

    private async Task<Guid?> ResolveInstructionCaseAsync(AgentWorkItem work, Guid organization, CancellationToken token)
    {
        if (work.SourceType == "agent-coordination")
        {
            if (!Guid.TryParseExact(work.CorrelationId, "D", out var session)) throw Denied();
            return await db.AgentCoordinationSessions.AsNoTracking().Where(x => x.Id == session &&
                x.OrganizationId == organization && x.SourceKind == "WorkItem").Select(x => x.SourceWorkItemId).SingleOrDefaultAsync(token);
        }
        if (work.SourceType is "WorkStageExecution" or "WorkDeliveryStage")
        {
            if (!Guid.TryParseExact(work.SourceId, "D", out var stage)) throw Denied();
            if (work.SourceType == "WorkStageExecution")
                return await (from s in db.WorkStageExecutions.AsNoTracking()
                    join i in db.WorkItemExecutions.AsNoTracking() on s.ItemExecutionId equals i.Id
                    where s.Id == stage select (Guid?)i.WorkItemId).SingleOrDefaultAsync(token);
            return await (from s in db.WorkStageExecutions.AsNoTracking()
                join e in db.WorkDeliveryExecutions.AsNoTracking() on s.DeliveryExecutionId equals e.Id
                where s.Id == stage select e.WorkItemId).SingleOrDefaultAsync(token);
        }
        if (work.Kind != AgentWorkKind.Event || !Guid.TryParseExact(work.SourceId, "D", out var claim)) return null;
        var items = await db.CoreWorkTasks.AsNoTracking().Where(x => x.OrganizationId == organization &&
            x.ClaimEventId == claim && x.ArchivedAt == null).Select(x => x.Id).Take(2).ToArrayAsync(token);
        if (items.Length > 1) throw Denied("instruction.ambiguous-case");
        return items.Length == 0 ? null : items[0];
    }
}
