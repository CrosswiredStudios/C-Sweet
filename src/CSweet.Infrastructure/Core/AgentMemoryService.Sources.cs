using System.Security.Cryptography;
using System.Text;
using CSweet.Domain.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryService
{
    private sealed record EnrichmentProvider(Guid Id, string Model, string ConfigurationHash);
    private sealed record SourceFingerprint(Guid Id, ConversationRole Role, string Checksum,
        DateTimeOffset CreatedAt, Guid? ChatTurnId);
    private sealed record EnrichmentSources(Guid ConversationId, Guid InstallationId, SourceFingerprint[] Messages);
    private sealed record EnrichmentInput(MemoryEpisode Episode, EnrichmentSources Sources);
    private sealed class MemorySourceInvalidatedException(string code = "memory_enrichment_source_invalidated") : Exception
    {
        public string Code { get; } = code;
    }

    private static string SourceChecksum(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static SourceFingerprint Fingerprint(ConversationMessage message) => new(message.Id, message.Role,
        SourceChecksum(message.Content), message.CreatedAt, message.ChatTurnId);

    private async Task ValidateSourcesAsync(MemoryEpisode episode, EnrichmentSources? sources,
        IMemoryStore target, CancellationToken token)
    {
        if (sources is null || sources.Messages is null || sources.Messages.Length is < 1 or > 2 ||
            sources.Messages[0].Id != episode.Id || sources.Messages[0].Role != ConversationRole.User ||
            sources.Messages[0].Checksum != episode.Checksum ||
            (sources.Messages.Length == 2 && (sources.Messages[1].Role != ConversationRole.Assistant ||
                sources.Messages[1].Id == episode.Id)))
            throw new MemorySourceInvalidatedException("memory_enrichment_unverifiable_output");
        var context = await LoadConversationContextAsync(sources.ConversationId, token);
        if (context is null || EmployeeMemoryNamespaces.UserRelationship(context.OrganizationId, context.EmployeeId,
                context.UserId, ApplicationId).Partition != episode.Partition ||
            !await db.CoreConversations.AnyAsync(x => x.Id == sources.ConversationId &&
                x.AgentOrganizationUser!.AgentInstallationId == sources.InstallationId, token))
            throw new MemorySourceInvalidatedException();
        var messages = new List<ConversationMessage>();
        foreach (var fingerprint in sources.Messages)
        {
            if (await db.MemorySourceInvalidations.AnyAsync(x => x.SourceMessageId == fingerprint.Id, token))
                throw new MemorySourceInvalidatedException("memory_source_changed");
            if (await db.MemoryCaptureExclusions.AnyAsync(x => x.SourceMessageId == fingerprint.Id, token))
                throw new MemorySourceInvalidatedException("memory_capture_excluded");
            var message = await db.CoreConversationMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == fingerprint.Id, token);
            if (message is null || message.ConversationId != sources.ConversationId || Fingerprint(message) != fingerprint ||
                await db.MemoryCaptureOutbox.AnyAsync(x => x.ConversationMessageId == fingerprint.Id &&
                    x.Status == MemoryCaptureStatus.Completed && x.EpisodeCapturedAt == null && x.LastError != null, token))
                throw new MemorySourceInvalidatedException();
            if (target is not IMemorySourceReader reader ||
                await reader.GetEpisodeAsync(episode.Partition, fingerprint.Id, token) is not { } current ||
                !MemoryProvenance.IsCurrent(current, episode.Partition, fingerprint.Id, DateTimeOffset.UtcNow) ||
                current.Checksum != fingerprint.Checksum || current.Content != message.Content ||
                current.OccurredAt != fingerprint.CreatedAt ||
                current.Source.Type != (message.Role == ConversationRole.User ? "user" : "assistant") ||
                current.Source.Id != message.Id.ToString("D") ||
                ReadGuid(current.Metadata, "conversationId") != sources.ConversationId ||
                ReadGuid(current.Metadata, "installationId") != sources.InstallationId ||
                current.Sensitivity != (message.Role == ConversationRole.User ? MemorySensitivity.Personal : MemorySensitivity.Internal))
                throw new MemorySourceInvalidatedException();
            messages.Add(message);
        }
        var expectedContent = messages.Count == 1 ? messages[0].Content :
            $"<user_turn>\n{messages[0].Content}\n</user_turn>\n<assistant_turn>\n{messages[1].Content}\n</assistant_turn>";
        if (episode.Content != expectedContent || episode.Sensitivity != MemorySensitivity.Personal)
            throw new MemorySourceInvalidatedException();
    }

    private async Task LockSourcesAsync(MemoryEpisode episode, EnrichmentSources sources, CancellationToken token)
    {
        // Called only inside the job's PostgreSQL apply transaction. Hold source and authority
        // rows through completion so an edit, deletion, or revocation cannot slip past validation.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"CoreConversations\" WHERE \"Id\" = {sources.ConversationId} FOR SHARE", token);
        foreach (var source in sources.Messages.OrderBy(x => x.Id))
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"CoreConversationMessages\" WHERE \"Id\" = {source.Id} FOR SHARE", token);
        var employeeId = Guid.Parse(episode.Partition.AgentId!);
        var humanId = Guid.Parse(episode.Partition.UserId!);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"CoreOrganizationUsers\" WHERE \"Id\" IN ({employeeId}, {humanId}) ORDER BY \"Id\" FOR SHARE", token);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"AgentInstallations\" WHERE \"Id\" = {sources.InstallationId} FOR SHARE", token);
        foreach (var source in sources.Messages.OrderBy(x => x.Id))
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM csweet_memory_episodes WHERE id = {source.Id} AND partition_key = {episode.Partition.StorageKey} FOR SHARE", token);
    }
}
