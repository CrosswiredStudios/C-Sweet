using CSweet.Contracts.PersonalAssistant;
using CSweet.Domain.PersonalAssistant;

namespace CSweet.Application.PersonalAssistant;

public sealed record StagedRepositorySource(string Name, string ArchiveKey, string Snapshot, RepositoryAnalysisInput Input);
public interface IRepositorySourceStore
{
    Task<StagedRepositorySource> StageUploadAsync(Guid user, Guid draft, Guid sourceId, string name, Stream archive, CancellationToken token);
    Task<StagedRepositorySource> PrepareGitHubAsync(Guid user, Guid draft, RepositoryOnboardingSource source, CancellationToken token);
    Task<Stream> OpenAsync(Guid user, Guid draft, string key, CancellationToken token);
    Task DiscardAsync(Guid user, Guid draft, CancellationToken token);
}
