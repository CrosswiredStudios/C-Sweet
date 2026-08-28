using CSweet.Contracts.Core;

namespace CSweet.Application.Core;

public sealed record ArtifactHumanActor(Guid ApplicationUserId);
public sealed record ArtifactAgentActor(Guid OrganizationUserId, Guid InstallationId, string AgentId, string? AgentVersion);

public interface IArtifactDocumentService
{
    Task<IReadOnlyList<ArtifactDocumentSummary>> BrowseAsync(Guid organizationId, ArtifactHumanActor actor, ArtifactDocumentQuery query, CancellationToken cancellationToken = default);
    Task<ArtifactDocumentDetail?> GetAsync(Guid organizationId, ArtifactHumanActor actor, Guid artifactId, CancellationToken cancellationToken = default);
    Task<ArtifactDocumentDetail> CreateAsync(Guid organizationId, ArtifactHumanActor actor, CreateArtifactDocumentRequest request, CancellationToken cancellationToken = default);
    Task<ArtifactRevisionResponse> ReviseAsync(Guid organizationId, ArtifactHumanActor actor, Guid artifactId, CreateArtifactRevisionRequest request, CancellationToken cancellationToken = default);
    Task<ArtifactDocumentDetail> SubmitAsync(Guid organizationId, ArtifactHumanActor actor, Guid artifactId, SubmitArtifactRevisionRequest request, CancellationToken cancellationToken = default);
    Task<ArtifactDocumentDetail> DecideAsync(Guid organizationId, ArtifactHumanActor actor, Guid artifactId, DecideArtifactRevisionRequest request, CancellationToken cancellationToken = default);
    Task<ArtifactDocumentDetail> MoveAsync(Guid organizationId, ArtifactHumanActor actor, Guid artifactId, MoveArtifactRequest request, CancellationToken cancellationToken = default);
    Task<ArtifactDocumentDetail> ReassignStewardAsync(Guid organizationId, ArtifactHumanActor actor, Guid artifactId, ReassignArtifactStewardRequest request, CancellationToken cancellationToken = default);
    Task<ArtifactDocumentDetail> SetArchivedAsync(Guid organizationId, ArtifactHumanActor actor, Guid artifactId, bool archived, ArtifactArchiveRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ArtifactFolderResponse>> ListFoldersAsync(Guid organizationId, ArtifactHumanActor actor, bool includeArchived, CancellationToken cancellationToken = default);
    Task<ArtifactFolderResponse> CreateFolderAsync(Guid organizationId, ArtifactHumanActor actor, CreateArtifactFolderRequest request, CancellationToken cancellationToken = default);
    Task<ArtifactFolderResponse> UpdateFolderAsync(Guid organizationId, ArtifactHumanActor actor, Guid folderId, UpdateArtifactFolderRequest request, CancellationToken cancellationToken = default);
    Task<ArtifactFolderResponse> SetFolderArchivedAsync(Guid organizationId, ArtifactHumanActor actor, Guid folderId, bool archived, ArtifactArchiveRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ArtifactGrantResponse>> SetGrantsAsync(Guid organizationId, ArtifactHumanActor actor, Guid artifactId, UpsertArtifactGrantRequest request, CancellationToken cancellationToken = default);
    Task<ArtifactAccessRequestResponse> RequestAccessAsync(Guid organizationId, ArtifactAgentActor actor, Guid artifactId, RequestArtifactAccessRequest request, CancellationToken cancellationToken = default);
    Task<ArtifactAccessRequestResponse> RequestAccessAsync(Guid organizationId, ArtifactHumanActor actor, Guid artifactId, RequestArtifactAccessRequest request, CancellationToken cancellationToken = default);
    Task<ArtifactAccessRequestResponse> DecideAccessAsync(Guid organizationId, ArtifactHumanActor actor, Guid requestId, DecideArtifactAccessRequest request, CancellationToken cancellationToken = default);
    Task<ArtifactPackageResponse> CreatePackageAsync(Guid organizationId, ArtifactHumanActor actor, CreateArtifactPackageRequest request, CancellationToken cancellationToken = default);
    Task<ArtifactPackageResponse?> GetPackageAsync(Guid organizationId, ArtifactHumanActor actor, Guid packageId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ArtifactPackageResponse>> ListPackagesAsync(Guid organizationId, ArtifactHumanActor actor, bool includeArchived, CancellationToken cancellationToken = default);
    Task<ArtifactPackageResponse> SubmitPackageAsync(Guid organizationId, ArtifactHumanActor actor, Guid packageId, SubmitArtifactPackageRequest request, CancellationToken cancellationToken = default);
    Task<ArtifactPackageResponse> DecidePackageAsync(Guid organizationId, ArtifactHumanActor actor, Guid packageId, DecideArtifactPackageRequest request, CancellationToken cancellationToken = default);
    Task<ArtifactPackageResponse> SetPackageArchivedAsync(Guid organizationId, ArtifactHumanActor actor, Guid packageId, bool archived, ArtifactArchiveRequest request, CancellationToken cancellationToken = default);
}
