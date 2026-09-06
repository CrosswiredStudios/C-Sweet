using System.Net.Http.Json;
using CSweet.TrustedServices;

namespace CSweet.AgentHost.Broker;

/// <summary>
/// AgentHost can ask Core to materialize an already-authorized workspace, but receives no provider
/// coordinates, installation identifiers, Git credentials, archive bytes, or Docker authority.
/// </summary>
public sealed class CoreWorkspaceBrokerClient(HttpClient http) : ITrustedGitHostClient
{
    public async Task<TrustedWorkspaceMaterialization> PrepareAsync(
        TrustedWorkspacePrepareRequest request,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(
                "agent-broker/v2/workspaces/prepare",
                new AgentBrokerWorkspacePrepareRequest(
                    request.OrganizationId,
                    request.AgentInstallationId,
                    request.RepositoryId,
                    request.WorkspaceId,
                    request.WorkItemId,
                    request.AssignmentRevision,
                    request.DeterministicBranch,
                    request.ExpectedCommitSha,
                    request.IdempotencyKey),
                cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException(
                "The trusted Core workspace broker is unavailable; source-control access remains blocked.",
                exception);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    "Core rejected or could not materialize the authorized workspace.");
        var result = await response.Content.ReadFromJsonAsync<AgentBrokerWorkspacePrepareResult>(cancellationToken)
            ?? throw new InvalidOperationException("Core returned an empty workspace response.");
        return new TrustedWorkspaceMaterialization(
            result.WorkspaceKey,
            result.AgentWorkspacePath,
            result.BaseCommitSha,
            result.Resumed);
        }
    }

    public async Task<TrustedWorkspaceRefresh> RefreshAsync(TrustedWorkspaceOperationRequest request, CancellationToken ct)
    {
        var result = await ExecuteAsync(request, "refresh", ct);
        return new(result.Status, result.BaseSha, result.Status == "Conflict"
            ? result.ChangedFiles.Select(path => new CSweet.Agent.SDK.GitWorkspaceConflict(path, "RemoteChanged", result.DiffSummary)).ToList() : []);
    }

    public async Task<CSweet.Agent.SDK.GitWorkspaceInspection> InspectAsync(TrustedWorkspaceOperationRequest request, CancellationToken ct)
    {
        var result = await ExecuteAsync(request, "inspect", ct);
        return new(request.WorkspaceId, result.Status, result.ChangedFiles.Count > 0, result.ChangedFiles, [])
        { HasTrackedChanges = result.ChangedFiles.Count > 0, TrackedChangedFiles = result.ChangedFiles, DiffSummary = result.DiffSummary };
    }

    public async Task<TrustedWorkspacePublication> PublishAsync(TrustedWorkspacePublishRequest request, CancellationToken ct)
    {
        var result = await ExecuteAsync(request.Workspace, "publish", ct, request.CommitMessage, title: request.ProposedChangeTitle, body: request.ProposedChangeBody);
        if (result.Status == "Locked") throw new InvalidOperationException(result.DiffSummary);
        return new(result.Provider, CSweet.Agent.SDK.GitDeliveryKinds.PullRequest, result.Branch!, result.CommitSha!,
            new Uri(result.ReviewUrl!, UriKind.Absolute), result.ChangedFiles, result.DiffSummary);
    }

    public async Task<CSweet.Agent.SDK.GitWorkspaceCleanupResult> CleanupAsync(TrustedWorkspaceCleanupRequest request, CancellationToken ct)
    {
        var result = await ExecuteAsync(request.Workspace, "cleanup", ct, retain: request.RetainOnFailure);
        return new(request.Workspace.WorkspaceId, result.Removed, result.RetainUntil);
    }

    private async Task<AgentBrokerWorkspaceOperationResult> ExecuteAsync(TrustedWorkspaceOperationRequest request,
        string operation, CancellationToken ct, string? message = null, bool retain = true, string? title = null, string? body = null)
    {
        using var response = await http.PostAsJsonAsync("agent-broker/v2/workspaces/operate",
            new AgentBrokerWorkspaceOperationRequest(request.OrganizationId, request.RepositoryId, request.WorkspaceId,
                request.WorkItemId, request.AssignmentRevision, request.WorkspaceKey, request.IdempotencyKey, operation, message, retain, title, body), ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Core rejected the workspace operation. Check current assignment and repository state.");
        return await response.Content.ReadFromJsonAsync<AgentBrokerWorkspaceOperationResult>(ct)
            ?? throw new InvalidOperationException("Core returned an empty workspace operation response.");
    }
}
