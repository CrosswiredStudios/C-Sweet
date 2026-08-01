using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Application.WorkManagement;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Shared = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

public sealed class GovernedMergeWorkActionExecutor(
    CSweetDbContext db,
    IPluginSecretStore secrets,
    IHttpClientFactory clients,
    TimeProvider timeProvider) : ITrustedWorkActionExecutor
{
    public const string ActionName = "git.merge.qa-approved.v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public string Action => ActionName;

    public async Task<TrustedWorkActionResult> ExecuteAsync(
        TrustedWorkActionContext context,
        CancellationToken cancellationToken = default)
    {
        var itemExecution = await db.WorkItemExecutions
            .Include(x => x.WorkItem)
            .Include(x => x.Stages).ThenInclude(x => x.Attempts)
            .SingleAsync(x => x.Id == context.ItemExecutionId, cancellationToken);
        var outcomes = itemExecution.Stages.SelectMany(x => x.Attempts)
            .Where(x => !string.IsNullOrWhiteSpace(x.ResultJson))
            .OrderBy(x => x.CompletedAt)
            .Select(x => JsonSerializer.Deserialize<Shared.WorkExecutionOutcomeV1>(x.ResultJson!, JsonOptions))
            .Where(x => x is not null).Cast<Shared.WorkExecutionOutcomeV1>().ToList();
        var development = outcomes.LastOrDefault(x =>
            x.Disposition == Shared.WorkExecutionDispositions.Completed &&
            x.Output.ValueKind == JsonValueKind.Object &&
            x.Output.TryGetProperty("pullRequestUrl", out _));
        var quality = outcomes.LastOrDefault(x =>
            x.Disposition == Shared.WorkExecutionDispositions.Completed && x.OutcomeCode == "passed");
        if (development is null || quality is null)
            return Blocked("Governed merge requires a completed development outcome and QA pass.");

        var output = development.Output;
        var connectionId = output.GetProperty("repositoryConnectionId").GetGuid();
        var commitSha = output.GetProperty("commitSha").GetString();
        var pullRequestUrl = output.GetProperty("pullRequestUrl").GetString();
        if (string.IsNullOrWhiteSpace(commitSha) || string.IsNullOrWhiteSpace(pullRequestUrl) ||
            !quality.Evidence.Any(x => x.Kind == "commit" &&
                string.Equals(x.Value, commitSha, StringComparison.OrdinalIgnoreCase)))
            return Blocked("The QA pass does not prove the exact development commit.");

        var developerInstallationId = itemExecution.Stages
            .Where(x => x.AgentInstallationId.HasValue && x.StageKey.Contains("develop", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.AgentInstallationId).FirstOrDefault();
        if (!developerInstallationId.HasValue)
            return Blocked("The development installation snapshot is unavailable.");
        var connection = await db.GitRepositoryConnections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == connectionId && x.OrganizationId == context.OrganizationId, cancellationToken);
        if (connection is null || connection.Provider != GitRepositoryProvider.GitHub ||
            !connection.AllowedOperations.HasFlag(GitAllowedOperation.MergeQaApprovedPullRequest))
            return Blocked("The repository connection does not authorize governed GitHub merge.");
        var grant = await db.GitRepositoryConnectionGrants.AsNoTracking().SingleOrDefaultAsync(x =>
            x.RepositoryConnectionId == connectionId && x.AgentInstallationId == developerInstallationId &&
            x.RevokedAt == null && x.CanMergeQaApprovedPullRequest, cancellationToken);
        if (grant is null) return Blocked("The governed merge grant is unavailable.");
        var token = await secrets.GetAsync(developerInstallationId.Value,
            SoftwareDevelopmentWorkService.CredentialKey(connection.Id, "github-api-token"), cancellationToken);
        if (string.IsNullOrWhiteSpace(token)) return Blocked("The GitHub merge credential is unavailable.");

        var uri = new Uri(pullRequestUrl);
        var (owner, repository, number) = ParsePullRequest(uri);
        if (!string.Equals($"{owner}/{repository}", connection.PermittedRepositoryPath, StringComparison.OrdinalIgnoreCase))
            return Blocked("The pull request is outside the repository grant.");
        var client = clients.CreateClient();
        using var inspect = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{owner}/{repository}/pulls/{number}");
        AddHeaders(inspect, token);
        using var inspected = await client.SendAsync(inspect, cancellationToken);
        if (!inspected.IsSuccessStatusCode)
            return Blocked($"GitHub pull-request inspection failed with HTTP {(int)inspected.StatusCode}.");
        using var pr = JsonDocument.Parse(await inspected.Content.ReadAsStringAsync(cancellationToken));
        var remoteHead = pr.RootElement.GetProperty("head").GetProperty("sha").GetString();
        if (!string.Equals(remoteHead, commitSha, StringComparison.OrdinalIgnoreCase))
            return Blocked("The pull-request head changed after QA approval.");

        var mergeSha = pr.RootElement.TryGetProperty("merged", out var alreadyMerged) && alreadyMerged.GetBoolean()
            ? pr.RootElement.GetProperty("merge_commit_sha").GetString() : null;
        if (mergeSha is null)
        {
            using var merge = new HttpRequestMessage(HttpMethod.Put,
                $"https://api.github.com/repos/{owner}/{repository}/pulls/{number}/merge")
            {
                Content = JsonContent.Create(new { sha = commitSha, merge_method = "squash" })
            };
            AddHeaders(merge, token);
            using var merged = await client.SendAsync(merge, cancellationToken);
            if (merged.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.MethodNotAllowed)
                return Blocked("Branch protection or mergeability currently prevents governed merge.");
            if (!merged.IsSuccessStatusCode)
                return Blocked($"GitHub rejected governed merge with HTTP {(int)merged.StatusCode}.");
            using var response = JsonDocument.Parse(await merged.Content.ReadAsStringAsync(cancellationToken));
            if (!response.RootElement.TryGetProperty("merged", out var didMerge) || !didMerge.GetBoolean())
                return Blocked("GitHub did not confirm the governed merge.");
            mergeSha = response.RootElement.GetProperty("sha").GetString();
        }
        if (string.IsNullOrWhiteSpace(mergeSha)) return Blocked("GitHub returned no merge commit SHA.");

        var now = timeProvider.GetUtcNow();
        itemExecution.WorkItem!.MergeStatus = "Merged";
        itemExecution.WorkItem.MergeCommitSha = mergeSha;
        itemExecution.WorkItem.MergedAt = now;
        itemExecution.WorkItem.MergeAuthorizationGrantId = grant.Id;
        itemExecution.WorkItem.MergeAuthorizationGrantRevision = grant.Revision;
        var workspace = await db.GitTicketWorkspaces
            .Where(x => x.WorkItemId == itemExecution.WorkItemId && x.CommitSha == commitSha)
            .OrderByDescending(x => x.UpdatedAt).FirstOrDefaultAsync(cancellationToken);
        if (workspace is not null)
        {
            workspace.MergeStatus = "Merged"; workspace.MergeCommitSha = mergeSha;
            workspace.MergedAt = now; workspace.UpdatedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
        return new(Shared.WorkExecutionDispositions.Completed, "merged", "QA-approved commit merged.",
            JsonSerializer.SerializeToElement(new { sourceCommitSha = commitSha, mergeCommitSha = mergeSha, pullRequestUrl }, JsonOptions),
            []);
    }

    private static TrustedWorkActionResult Blocked(string summary) => new(
        Shared.WorkExecutionDispositions.Blocked, "blocked", summary,
        JsonSerializer.SerializeToElement(new { }), [summary]);

    private static (string Owner, string Repository, int Number) ParsePullRequest(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The approved pull request is not a GitHub HTTPS URL.");
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length != 4 || segments[2] != "pull" || !int.TryParse(segments[3], out var number))
            throw new InvalidOperationException("The approved pull request URL is malformed.");
        return (segments[0], segments[1], number);
    }

    private static void AddHeaders(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("CSweet-Board-Orchestrator/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
    }
}
