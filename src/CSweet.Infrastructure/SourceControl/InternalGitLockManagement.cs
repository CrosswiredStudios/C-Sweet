using CSweet.Contracts.SourceControl;
using CSweet.Domain.Setup;
using CSweet.Domain.Core;

namespace CSweet.Infrastructure.SourceControl;

public sealed partial class InternalGitAccessService
{
    public async Task<(Guid Actor, InternalGitLockResult Result)> LocksWithTokenAsync(Guid business, Guid repository, string token,
        ManageInternalGitLockRequest request, int limit, CancellationToken ct)
    {
        var (_, access, _) = await ValidateAsync(business, repository, token, request.Operation == "list" ? "git-upload-pack" : "git-receive-pack", ct);
        var actor = await MemberAsync(business, access.UserId, ct);
        return (access.UserId, await ExecuteLocksAsync(business, repository, actor, request, limit, ct));
    }
    public async Task<InternalGitLockResult> ManageLocksAsync(Guid business, Guid repository, Guid user, ManageInternalGitLockRequest request, CancellationToken ct)
    {
        var actor = await MemberAsync(business, user, ct); var repo = await RepositoryAsync(business, repository, ct);
        if (request.Operation is not ("list" or "create" or "unlock")) throw new ArgumentException("Unsupported lock operation.");
        if (request.Operation != "list" && (actor.PermissionLevel < OrganizationPermissionLevel.Manager || repo.Status != SourceControlRepositoryStatus.Ready || repo.ArchivedAt is not null))
            throw new UnauthorizedAccessException("Management access to an active repository is required.");
        return await ExecuteLocksAsync(business, repository, actor, request, 100, ct);
    }
    private async Task<InternalGitLockResult> ExecuteLocksAsync(Guid business, Guid repository, OrganizationUser actor, ManageInternalGitLockRequest request, int limit, CancellationToken ct)
    {
        if (request.Operation is "create" or "unlock")
            await RecordAsync(business, repository, actor.ApplicationUserId!.Value, "LfsLockChangeStarted", new { request.Operation, request.Path, request.Id, request.Force }, ct);
        var result = await host.InternalLocksAsync(new(business, repository, actor.ApplicationUserId!.Value,
            string.IsNullOrWhiteSpace(actor.DisplayName) ? "Business member" : actor.DisplayName,
            request.Operation, request.Path, request.Id, request.Force, actor.PermissionLevel >= OrganizationPermissionLevel.Manager, request.Cursor, limit), ct);
        if (request.Operation is "create" or "unlock" && result.StatusCode is >= 200 and < 300)
            await RecordAsync(business, repository, actor.ApplicationUserId.Value, request.Operation == "create" ? "LfsLocked" : "LfsUnlocked",
                new { request.Force, Locks = result.Locks.Select(l => new { l.Id, l.Path, l.OwnerId }) }, ct);
        return result;
    }
}
