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

    public Task<TrustedWorkspaceRefresh> RefreshAsync(
        TrustedWorkspaceOperationRequest request,
        CancellationToken cancellationToken) => throw NotEnabled("refresh");

    public Task<CSweet.Agent.SDK.GitWorkspaceInspection> InspectAsync(
        TrustedWorkspaceOperationRequest request,
        CancellationToken cancellationToken) => throw NotEnabled("inspection");

    public Task<TrustedWorkspacePublication> PublishAsync(
        TrustedWorkspacePublishRequest request,
        CancellationToken cancellationToken) => throw NotEnabled("publication");

    public Task<CSweet.Agent.SDK.GitWorkspaceCleanupResult> CleanupAsync(
        TrustedWorkspaceCleanupRequest request,
        CancellationToken cancellationToken) => throw NotEnabled("cleanup");

    private static InvalidOperationException NotEnabled(string operation) => new(
        $"Trusted workspace {operation} remains blocked until its sanitized Core broker operation is configured.");
}
