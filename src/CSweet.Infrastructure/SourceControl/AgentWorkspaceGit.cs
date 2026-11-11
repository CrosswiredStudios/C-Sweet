using CSweet.Contracts.SourceControl;
using CSweet.Domain.Setup;
using CSweet.TrustedServices;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.SourceControl;

public sealed partial class AgentWorkspaceBroker
{
    /// <summary>
    /// Runs one Git smart-HTTP exchange for an agent's assigned workspace. A read needs the authority of a
    /// snapshot pull and a push the authority of a publication, so this grants nothing the workspace did not
    /// already have. GitHost confines every push to the workspace branch and its assigned base commit.
    /// </summary>
    public async Task<AgentBrokerWorkspaceGitResult> GitAsync(AgentBrokerWorkspaceGitRequest request, CancellationToken ct = default)
    {
        var push = request.Service == "git-receive-pack";
        if (request.Service is not ("git-upload-pack" or "git-receive-pack") ||
            request.Body is not { Length: <= InternalGitRepositoryStore.MaximumGitRequestBytes } ||
            request.Advertise && request.Body.Length != 0 ||
            request.Workspace.Operation != (push ? "publish" : "snapshot-pull") ||
            string.IsNullOrWhiteSpace(request.Workspace.IdempotencyKey) || request.Workspace.IdempotencyKey.Length > 160)
            throw new ArgumentException("Invalid workspace Git exchange.");
        var workspace = await AuthorizeWorkspaceOperationAsync(request.Workspace, ct);
        if (workspace.Repository!.Connection!.Provider != SourceControlProvider.InternalGit)
            throw new InvalidOperationException("Agent Git access currently requires an internal repository.");
        var actor = await db.CoreOrganizationUsers.AsNoTracking().Where(u => u.OrganizationId == workspace.OrganizationId &&
            u.AgentInstallationId == workspace.AgentInstallationId && u.IsActive).Select(u => u.Id).SingleAsync(ct);
        var result = await gitHost.ExchangeInternalGitAsync(new InternalGitHttpRequest(workspace.OrganizationId, workspace.RepositoryId,
            request.Service, request.Advertise, request.Body, [], actor)
        {
            AgentBranch = workspace.BranchName,
            AgentBaseCommitSha = workspace.BaseCommitSha,
            AgentMaximumBlobBytes = _transferLimits.MaximumGitBlobBytes
        }, ct);
        return new(result.ContentType, result.Body);
    }
}
