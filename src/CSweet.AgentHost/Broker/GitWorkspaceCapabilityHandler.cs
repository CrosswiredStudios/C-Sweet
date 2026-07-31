using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

/// <summary>
/// Executes narrowly-scoped Git operations inside the authenticated agent runtime.
/// Credential values travel only over docker-exec stdin and are redacted from errors.
/// </summary>
public sealed class GitWorkspaceCapabilityHandler(
    CSweetDbContext db,
    IDockerCommandExecutor docker,
    IPluginSecretStore secrets,
    IHttpClientFactory httpClientFactory) : IPlatformCapabilityHandler
{
    private const long MaximumRepositoryBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumRepositoryFiles = 250_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Handled =
    [
        GitWorkspaceCapabilities.Prepare,
        GitWorkspaceCapabilities.Inspect,
        GitWorkspaceCapabilities.Publish,
        GitWorkspaceCapabilities.Cleanup
    ];

    public bool CanHandle(string capability) => Handled.Contains(capability);

    public async IAsyncEnumerable<CapabilityResult> HandleAsync(
        AgentSession session,
        RequestCapability request,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        if (!session.Grant.RequestedCapabilities.Contains(request.Capability))
        {
            yield return Failure(
                request.RequestId,
                PlatformCapabilityErrorCode.Denied,
                $"The installation capability grant does not include '{request.Capability}'.");
            yield break;
        }
        if (!Guid.TryParse(session.BusinessId, out var organizationId) ||
            !Guid.TryParse(session.InstallationId, out var installationId) ||
            !Guid.TryParse(session.RuntimeInstanceId, out var runtimeInstanceId))
        {
            yield return Failure(
                request.RequestId,
                PlatformCapabilityErrorCode.Denied,
                "The authenticated runtime identity is invalid.");
            yield break;
        }

        CapabilityResult response;
        try
        {
            object value = request.Capability switch
            {
                GitWorkspaceCapabilities.Prepare => await PrepareAsync(
                    organizationId,
                    installationId,
                    runtimeInstanceId,
                    Read<PrepareGitWorkspaceRequest>(request),
                    cancellationToken),
                GitWorkspaceCapabilities.Inspect => await InspectAsync(
                    organizationId,
                    installationId,
                    runtimeInstanceId,
                    Read<InspectGitWorkspaceRequest>(request),
                    cancellationToken),
                GitWorkspaceCapabilities.Publish => await PublishAsync(
                    organizationId,
                    installationId,
                    runtimeInstanceId,
                    Read<PublishGitWorkspaceRequest>(request),
                    cancellationToken),
                GitWorkspaceCapabilities.Cleanup => await CleanupAsync(
                    organizationId,
                    installationId,
                    runtimeInstanceId,
                    Read<CleanupGitWorkspaceRequest>(request),
                    cancellationToken),
                _ => throw new KeyNotFoundException("The Git workspace capability is not implemented.")
            };
            response = Success(request.RequestId, value);
        }
        catch (JsonException)
        {
            response = Failure(
                request.RequestId,
                PlatformCapabilityErrorCode.ValidationFailed,
                "The capability payload is not valid JSON.");
        }
        catch (UnauthorizedAccessException exception)
        {
            response = Failure(
                request.RequestId,
                PlatformCapabilityErrorCode.Denied,
                exception.Message);
        }
        catch (KeyNotFoundException exception)
        {
            response = Failure(
                request.RequestId,
                PlatformCapabilityErrorCode.NotFound,
                exception.Message);
        }
        catch (ArgumentException exception)
        {
            response = Failure(
                request.RequestId,
                PlatformCapabilityErrorCode.ValidationFailed,
                exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            response = Failure(
                request.RequestId,
                PlatformCapabilityErrorCode.Conflict,
                exception.Message);
        }
        yield return response;
    }

    private async Task<GitWorkspaceResult> PrepareAsync(
        Guid organizationId,
        Guid installationId,
        Guid runtimeInstanceId,
        PrepareGitWorkspaceRequest input,
        CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(input.IdempotencyKey);
        var context = await RequireContextAsync(
            organizationId,
            installationId,
            runtimeInstanceId,
            input.WorkItemId,
            input.AssignmentRevision,
            input.RepositoryConnectionId,
            requirePush: false,
            cancellationToken);
        var expectedBranch = DeterministicBranch(input.WorkItemId, context.Item.Title);
        if (!string.Equals(input.BranchName, expectedBranch, StringComparison.Ordinal))
            throw new ArgumentException($"The ticket branch must be '{expectedBranch}'.");
        var baseBranch = string.IsNullOrWhiteSpace(input.BaseBranch)
            ? context.Connection.DefaultBranch
            : ValidateGitReference(input.BaseBranch);
        var expectedCommitSha = string.IsNullOrWhiteSpace(input.ExpectedCommitSha)
            ? null
            : ValidateCommitSha(input.ExpectedCommitSha);
        if (input.ResumePublishedBranch && expectedCommitSha is null)
            throw new ArgumentException(
                "Resuming a published branch requires its expected commit SHA.");
        var expectedPath =
            $"/workspace/{input.WorkItemId:N}/{input.AssignmentRevision}";

        var workspace = await db.GitTicketWorkspaces.SingleOrDefaultAsync(x =>
            x.AgentInstallationId == installationId &&
            x.WorkItemId == input.WorkItemId &&
            x.AssignmentRevision == input.AssignmentRevision,
            cancellationToken);
        var resumed = workspace is not null;
        if (workspace is null)
        {
            var now = DateTimeOffset.UtcNow;
            workspace = new GitTicketWorkspace
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                AgentInstallationId = installationId,
                WorkItemId = input.WorkItemId,
                AssignmentRevision = input.AssignmentRevision,
                RepositoryConnectionId = input.RepositoryConnectionId,
                WorkspacePath = expectedPath,
                BaseBranch = baseBranch,
                BranchName = expectedBranch,
                Status = GitTicketWorkspaceStatus.Preparing,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.GitTicketWorkspaces.Add(workspace);
            await db.SaveChangesAsync(cancellationToken);
        }
        else if (workspace.RepositoryConnectionId != input.RepositoryConnectionId ||
                 workspace.WorkspacePath != expectedPath ||
                 workspace.BaseBranch != baseBranch ||
                 workspace.BranchName != expectedBranch)
        {
            throw new InvalidOperationException(
                "The assignment workspace is already bound to different repository parameters.");
        }
        workspace.Status = GitTicketWorkspaceStatus.Preparing;
        workspace.LastError = null;
        workspace.RetainUntil = null;
        workspace.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var authentication = await ResolveAuthenticationAsync(
            context.Connection,
            installationId,
            cancellationToken);
        var script = BuildPrepareScript(
            context.Connection,
            expectedPath,
            baseBranch,
            expectedBranch,
            expectedCommitSha,
            input.ResumePublishedBranch,
            authentication);
        var result = await ExecuteInRuntimeAsync(
            context.ContainerId,
            script,
            authentication.StandardInput,
            authentication.KnownSecrets,
            cancellationToken);
        if (result.ExitCode != 0)
        {
            workspace.Status = GitTicketWorkspaceStatus.Failed;
            workspace.LastError = Bounded(result.StandardError);
            workspace.RetainUntil = DateTimeOffset.UtcNow.AddHours(24);
            workspace.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Repository preparation failed: {workspace.LastError}");
        }

        var metadata = ParseCommandMetadata(result.StandardOutput);
        if (metadata.Bytes > MaximumRepositoryBytes ||
            metadata.Files > MaximumRepositoryFiles)
        {
            workspace.Status = GitTicketWorkspaceStatus.Failed;
            workspace.LastError = "The repository exceeds the approved workspace quota.";
            workspace.RetainUntil = DateTimeOffset.UtcNow.AddHours(24);
            workspace.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException("The repository exceeds the approved workspace quota.");
        }
        workspace.Status = GitTicketWorkspaceStatus.Ready;
        workspace.CommitSha = expectedCommitSha;
        workspace.LastError = null;
        workspace.RetainUntil = null;
        workspace.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return new GitWorkspaceResult(
            workspace.Id,
            workspace.WorkItemId,
            workspace.WorkspacePath,
            workspace.RepositoryConnectionId,
            workspace.BaseBranch,
            workspace.BranchName,
            workspace.Status.ToString(),
            resumed)
        {
            CheckoutCommitSha = metadata.CommitSha
        };
    }

    private async Task<GitWorkspaceInspection> InspectAsync(
        Guid organizationId,
        Guid installationId,
        Guid runtimeInstanceId,
        InspectGitWorkspaceRequest input,
        CancellationToken cancellationToken)
    {
        var (workspace, containerId) = await RequireWorkspaceAsync(
            organizationId, installationId, runtimeInstanceId, input.WorkspaceId, cancellationToken);
        var script = $"""
            set -euo pipefail
            cd {Quote(workspace.WorkspacePath)}
            printf '%s\n' 'CSWEET_CHANGED_BEGIN'
            git status --porcelain=v1
            printf '%s\n' 'CSWEET_CHANGED_END'
            printf '%s\n' 'CSWEET_COMMITS_BEGIN'
            git log --format='%H %s' {Quote($"origin/{workspace.BaseBranch}..HEAD")}
            printf '%s\n' 'CSWEET_COMMITS_END'
            printf '%s\n' 'CSWEET_TRACKED_BEGIN'
            (git diff --name-only; git diff --cached --name-only; {(
                string.IsNullOrWhiteSpace(workspace.CommitSha)
                    ? "true"
                    : $"git diff --name-only {Quote(workspace.CommitSha)} HEAD")}) | sort -u
            printf '%s\n' 'CSWEET_TRACKED_END'
            printf 'CSWEET_HEAD=%s\n' "$(git rev-parse HEAD)"
            """;
        var result = await ExecuteInRuntimeAsync(
            containerId, script, null, [], cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"Could not inspect the ticket workspace: {Bounded(result.StandardError)}");
        var changed = Between(result.StandardOutput, "CSWEET_CHANGED_BEGIN", "CSWEET_CHANGED_END")
            .Select(x => x.Length > 3 ? x[3..] : x)
            .Where(x => x.Length > 0)
            .ToList();
        var commits = Between(result.StandardOutput, "CSWEET_COMMITS_BEGIN", "CSWEET_COMMITS_END");
        var tracked = Between(
            result.StandardOutput, "CSWEET_TRACKED_BEGIN", "CSWEET_TRACKED_END");
        var headCommit = result.StandardOutput.Split(
                '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(x => x.StartsWith("CSWEET_HEAD=", StringComparison.Ordinal))
            ?["CSWEET_HEAD=".Length..];
        var headChanged = !string.IsNullOrWhiteSpace(workspace.CommitSha) &&
            !string.Equals(workspace.CommitSha, headCommit, StringComparison.OrdinalIgnoreCase);
        workspace.ChangedFilesJson = JsonSerializer.Serialize(changed, JsonOptions);
        workspace.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return new GitWorkspaceInspection(
            workspace.Id,
            workspace.Status.ToString(),
            changed.Count > 0 || commits.Count > 0,
            changed,
            commits,
            JsonSerializer.Deserialize<IReadOnlyList<GitValidationResult>>(
                workspace.ValidationsJson, JsonOptions) ?? [])
        {
            HasTrackedChanges = tracked.Count > 0 || headChanged,
            TrackedChangedFiles = tracked
        };
    }

    private async Task<GitWorkspacePublication> PublishAsync(
        Guid organizationId,
        Guid installationId,
        Guid runtimeInstanceId,
        PublishGitWorkspaceRequest input,
        CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(input.IdempotencyKey);
        if (string.IsNullOrWhiteSpace(input.CommitMessage) || input.CommitMessage.Length > 512)
            throw new ArgumentException("A commit message of at most 512 characters is required.");
        if (string.IsNullOrWhiteSpace(input.PullRequestTitle) ||
            input.PullRequestTitle.Length > 256)
            throw new ArgumentException("A pull-request title of at most 256 characters is required.");
        if (input.PullRequestBody is null || input.PullRequestBody.Length > 32_768)
            throw new ArgumentException("The pull-request body is too long.");
        if (input.Validations is null || input.Validations.Count == 0)
            throw new ArgumentException(
                "At least one successful validation result is required before publication.");
        if (input.Validations.Count > 100 ||
            input.Validations.Any(x =>
                string.IsNullOrWhiteSpace(x.Command) ||
                x.Command.Length > 2_000 ||
                !x.Succeeded ||
                x.ExitCode != 0 ||
                (x.DiagnosticExcerpt?.Length ?? 0) > 4_000))
            throw new ArgumentException(
                "Validation results must be bounded and every validation must have succeeded.");

        var (workspace, containerId) = await RequireWorkspaceAsync(
            organizationId, installationId, runtimeInstanceId, input.WorkspaceId, cancellationToken);
        if (workspace.Status == GitTicketWorkspaceStatus.Published &&
            !string.IsNullOrWhiteSpace(workspace.CommitSha))
            return ToPublication(workspace);
        var context = await RequireContextAsync(
            organizationId,
            installationId,
            runtimeInstanceId,
            workspace.WorkItemId,
            workspace.AssignmentRevision,
            workspace.RepositoryConnectionId,
            requirePush: true,
            cancellationToken);
        var authentication = await ResolveAuthenticationAsync(
            context.Connection, installationId, cancellationToken);
        workspace.ValidationsJson = JsonSerializer.Serialize(input.Validations, JsonOptions);
        var script = BuildPublishScript(
            context.Connection,
            workspace,
            input.CommitMessage,
            authentication);
        var result = await ExecuteInRuntimeAsync(
            containerId,
            script,
            authentication.StandardInput,
            authentication.KnownSecrets,
            cancellationToken);
        if (result.ExitCode != 0)
        {
            workspace.Status = GitTicketWorkspaceStatus.Failed;
            workspace.LastError = Bounded(result.StandardError);
            workspace.RetainUntil = DateTimeOffset.UtcNow.AddHours(24);
            workspace.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException($"Branch publication failed: {workspace.LastError}");
        }
        var commitSha = result.StandardOutput.Split(
                '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(x => x.StartsWith("CSWEET_COMMIT=", StringComparison.Ordinal))
            ?["CSWEET_COMMIT=".Length..];
        if (string.IsNullOrWhiteSpace(commitSha))
            throw new InvalidOperationException("Git did not return a published commit.");

        Uri? pullRequestUrl = null;
        try
        {
            if (context.Connection.PullRequestProvider == GitPullRequestProvider.GitHub)
            {
                var apiToken = authentication.ApiToken ??
                    throw new InvalidOperationException(
                        "The GitHub review provider requires an API credential.");
                pullRequestUrl = await CreateGitHubPullRequestAsync(
                    context.Connection,
                    workspace,
                    input,
                    apiToken,
                    cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            workspace.CommitSha = commitSha;
            workspace.Status = GitTicketWorkspaceStatus.Failed;
            workspace.LastError = Bounded(exception.Message);
            workspace.RetainUntil = DateTimeOffset.UtcNow.AddHours(24);
            workspace.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException(
                $"The branch was pushed, but pull-request creation failed: {workspace.LastError}");
        }
        workspace.CommitSha = commitSha;
        workspace.PullRequestUrl = pullRequestUrl?.AbsoluteUri;
        workspace.Status = GitTicketWorkspaceStatus.Published;
        workspace.LastError = null;
        workspace.RetainUntil = null;
        workspace.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return ToPublication(workspace);
    }

    private async Task<GitWorkspaceCleanupResult> CleanupAsync(
        Guid organizationId,
        Guid installationId,
        Guid runtimeInstanceId,
        CleanupGitWorkspaceRequest input,
        CancellationToken cancellationToken)
    {
        var (workspace, containerId) = await RequireWorkspaceAsync(
            organizationId, installationId, runtimeInstanceId, input.WorkspaceId, cancellationToken);
        if (workspace.Status != GitTicketWorkspaceStatus.Published && input.RetainOnFailure)
        {
            workspace.RetainUntil = DateTimeOffset.UtcNow.AddHours(24);
            workspace.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return new GitWorkspaceCleanupResult(
                workspace.Id, false, workspace.RetainUntil);
        }
        ValidateWorkspacePath(
            workspace.WorkspacePath, workspace.WorkItemId, workspace.AssignmentRevision);
        var result = await ExecuteInRuntimeAsync(
            containerId,
            $"set -euo pipefail\nfind {Quote(workspace.WorkspacePath)} -depth -mindepth 1 -delete\nrmdir {Quote(workspace.WorkspacePath)}",
            null,
            [],
            cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"Workspace cleanup failed: {Bounded(result.StandardError)}");
        workspace.Status = GitTicketWorkspaceStatus.Removed;
        workspace.RetainUntil = null;
        workspace.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return new GitWorkspaceCleanupResult(workspace.Id, true, null);
    }

    private async Task<WorkspaceContext> RequireContextAsync(
        Guid organizationId,
        Guid installationId,
        Guid runtimeInstanceId,
        Guid workItemId,
        long assignmentRevision,
        Guid connectionId,
        bool requirePush,
        CancellationToken cancellationToken)
    {
        var runtime = await db.AgentRuntimeInstances.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == runtimeInstanceId &&
            x.AgentInstallationId == installationId &&
            x.ContainerId != null &&
            x.Status == AgentRuntimeStatus.Running, cancellationToken)
            ?? throw new UnauthorizedAccessException("The agent runtime is not active.");
        var item = await db.CoreWorkTasks.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId &&
            x.Id == workItemId &&
            x.AssignedAgentInstallationId == installationId &&
            x.AssignmentRevision == assignmentRevision, cancellationToken)
            ?? throw new UnauthorizedAccessException(
                "The work item is not assigned to this installation revision.");
        var connection = await db.GitRepositoryConnections.AsNoTracking()
            .SingleOrDefaultAsync(x =>
                x.Id == connectionId && x.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new KeyNotFoundException("The repository connection was not found.");
        var assignedRepositoryId = DeserializeAssignedRepositoryId(item);
        if (assignedRepositoryId != connectionId)
            throw new UnauthorizedAccessException(
                "The repository is not the one pinned to this assignment.");
        var grant = await db.GitRepositoryConnectionGrants.AsNoTracking()
            .SingleOrDefaultAsync(x =>
                x.RepositoryConnectionId == connectionId &&
                x.AgentInstallationId == installationId &&
                x.RevokedAt == null, cancellationToken)
            ?? throw new UnauthorizedAccessException(
                "The repository connection is not granted to this installation.");
        if (!grant.CanReadFetch || (requirePush && !grant.CanPushTicketBranch))
            throw new UnauthorizedAccessException(
                requirePush
                    ? "The repository grant does not allow ticket-branch push."
                    : "The repository grant does not allow clone/fetch.");
        return new WorkspaceContext(item, connection, runtime.ContainerId!);
    }

    private static Guid? DeserializeAssignedRepositoryId(WorkTask item)
    {
        foreach (var json in new[] { item.QualityBriefJson, item.DevelopmentBriefJson })
        {
            if (string.IsNullOrWhiteSpace(json))
                continue;
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty(
                    "repositoryConnectionId", out var property) &&
                property.TryGetGuid(out var repositoryConnectionId))
                return repositoryConnectionId;
        }
        return null;
    }

    private async Task<(GitTicketWorkspace Workspace, string ContainerId)> RequireWorkspaceAsync(
        Guid organizationId,
        Guid installationId,
        Guid runtimeInstanceId,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        var workspace = await db.GitTicketWorkspaces.SingleOrDefaultAsync(x =>
            x.Id == workspaceId &&
            x.OrganizationId == organizationId &&
            x.AgentInstallationId == installationId, cancellationToken)
            ?? throw new KeyNotFoundException("The ticket workspace was not found.");
        ValidateWorkspacePath(
            workspace.WorkspacePath, workspace.WorkItemId, workspace.AssignmentRevision);
        var runtime = await db.AgentRuntimeInstances.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == runtimeInstanceId &&
            x.AgentInstallationId == installationId &&
            x.ContainerId != null &&
            x.Status == AgentRuntimeStatus.Running, cancellationToken)
            ?? throw new UnauthorizedAccessException("The agent runtime is not active.");
        return (workspace, runtime.ContainerId!);
    }

    private async Task<GitAuthentication> ResolveAuthenticationAsync(
        GitRepositoryConnection connection,
        Guid installationId,
        CancellationToken cancellationToken)
    {
        switch (connection.AuthenticationMode)
        {
            case GitAuthenticationMode.Anonymous:
                return new GitAuthentication("anonymous", null, null, []);
            case GitAuthenticationMode.HttpsCredential:
            {
                var token = await RequireSecretAsync(
                    installationId, connection.Id, "https-token", cancellationToken);
                return new GitAuthentication(
                    "https", token, token, [token]);
            }
            case GitAuthenticationMode.Ssh:
            {
                var key = await RequireSecretAsync(
                    installationId, connection.Id, "ssh-private-key", cancellationToken);
                var passphrase = await secrets.GetAsync(
                    installationId,
                    SoftwareDevelopmentWorkService.CredentialKey(
                        connection.Id, "ssh-key-passphrase"),
                    cancellationToken);
                string? apiToken = null;
                if (connection.PullRequestProvider == GitPullRequestProvider.GitHub)
                    apiToken = await RequireSecretAsync(
                        installationId, connection.Id, "github-api-token", cancellationToken);
                var passphraseLine = passphrase is null
                    ? string.Empty
                    : Convert.ToBase64String(Encoding.UTF8.GetBytes(passphrase));
                var standardInput = $"{passphraseLine}\n{key}";
                var knownSecrets = new List<string> { key };
                if (passphrase is not null) knownSecrets.Add(passphrase);
                if (apiToken is not null) knownSecrets.Add(apiToken);
                return new GitAuthentication(
                    "ssh", standardInput, apiToken, knownSecrets);
            }
            case GitAuthenticationMode.GitHubApp:
            {
                var appId = await RequireSecretAsync(
                    installationId, connection.Id, "github-app-id", cancellationToken);
                var githubInstallationId = await RequireSecretAsync(
                    installationId, connection.Id, "github-installation-id", cancellationToken);
                var privateKey = await RequireSecretAsync(
                    installationId, connection.Id, "github-private-key", cancellationToken);
                var token = await MintGitHubAppTokenAsync(
                    appId, githubInstallationId, privateKey, cancellationToken);
                return new GitAuthentication("https", token, token, [token, privateKey]);
            }
            default:
                throw new InvalidOperationException("The repository authentication mode is unsupported.");
        }
    }

    private async Task<string> RequireSecretAsync(
        Guid installationId,
        Guid connectionId,
        string component,
        CancellationToken cancellationToken) =>
        await secrets.GetAsync(
            installationId,
            SoftwareDevelopmentWorkService.CredentialKey(connectionId, component),
            cancellationToken)
        ?? throw new InvalidOperationException(
            $"The repository credential component '{component}' is unavailable.");

    private async Task<string> MintGitHubAppTokenAsync(
        string appId,
        string installationId,
        string privateKey,
        CancellationToken cancellationToken)
    {
        if (!long.TryParse(appId, out _) || !long.TryParse(installationId, out _))
            throw new InvalidOperationException("The GitHub App identity is invalid.");
        var now = DateTimeOffset.UtcNow;
        var header = Base64Url("""{"alg":"RS256","typ":"JWT"}""");
        var payload = Base64Url(JsonSerializer.Serialize(new
        {
            iat = now.AddSeconds(-30).ToUnixTimeSeconds(),
            exp = now.AddMinutes(9).ToUnixTimeSeconds(),
            iss = appId
        }));
        var unsigned = $"{header}.{payload}";
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKey);
        var signature = Base64Url(rsa.SignData(
            Encoding.UTF8.GetBytes(unsigned),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.github.com/app/installations/{installationId}/access_tokens");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", $"{unsigned}.{signature}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("CSweet-AgentHost/1.0");
        using var response = await client.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"GitHub App token exchange failed with HTTP {(int)response.StatusCode}.");
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("GitHub did not return an installation token.");
    }

    private async Task<Uri> CreateGitHubPullRequestAsync(
        GitRepositoryConnection connection,
        GitTicketWorkspace workspace,
        PublishGitWorkspaceRequest input,
        string token,
        CancellationToken cancellationToken)
    {
        var repository = connection.PermittedRepositoryPath.Trim('/');
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"https://api.github.com/repos/{repository}/pulls");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("CSweet-AgentHost/1.0");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                title = input.PullRequestTitle,
                head = workspace.BranchName,
                @base = workspace.BaseBranch,
                body = input.PullRequestBody
            }, JsonOptions),
            Encoding.UTF8,
            "application/json");
        using var response = await client.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
        {
            var owner = repository.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
            using var lookup = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.github.com/repos/{repository}/pulls?state=open&head={Uri.EscapeDataString($"{owner}:{workspace.BranchName}")}&base={Uri.EscapeDataString(workspace.BaseBranch)}");
            lookup.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            lookup.Headers.Accept.ParseAdd("application/vnd.github+json");
            lookup.Headers.UserAgent.ParseAdd("CSweet-AgentHost/1.0");
            using var lookupResponse = await client.SendAsync(lookup, cancellationToken);
            var lookupJson = await lookupResponse.Content.ReadAsStringAsync(cancellationToken);
            if (lookupResponse.IsSuccessStatusCode)
            {
                using var lookupDocument = JsonDocument.Parse(lookupJson);
                var existing = lookupDocument.RootElement.EnumerateArray().FirstOrDefault();
                if (existing.ValueKind != JsonValueKind.Undefined)
                {
                    var existingUrl = existing.GetProperty("html_url").GetString();
                    if (Uri.TryCreate(existingUrl, UriKind.Absolute, out var existingUri))
                        return existingUri;
                }
            }
        }
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"GitHub pull-request creation failed with HTTP {(int)response.StatusCode}.");
        using var document = JsonDocument.Parse(json);
        var url = document.RootElement.GetProperty("html_url").GetString();
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri
            : throw new InvalidOperationException("GitHub returned an invalid pull-request URL.");
    }

    private static string BuildPrepareScript(
        GitRepositoryConnection connection,
        string workspacePath,
        string baseBranch,
        string branchName,
        string? expectedCommitSha,
        bool resumePublishedBranch,
        GitAuthentication authentication)
    {
        var auth = AuthenticationPrefix(connection, authentication);
        return $$"""
            set -euo pipefail
            {{auth}}
            workspace={{Quote(workspacePath)}}
            parent=$(dirname "$workspace")
            mkdir -p "$parent"
            test "$(realpath -m "$workspace")" = {{Quote(workspacePath)}}
            if [ -d "$workspace/.git" ]; then
              cd "$workspace"
              git remote set-url origin {{Quote(connection.CloneUrl)}}
              git fetch --prune origin {{Quote(baseBranch)}}
            else
              if [ -e "$workspace" ]; then
                test "$(realpath -m "$workspace")" = {{Quote(workspacePath)}}
                find "$workspace" -depth -mindepth 1 -delete
              fi
              git clone --no-checkout {{Quote(connection.CloneUrl)}} "$workspace"
              cd "$workspace"
              git fetch --prune origin {{Quote(baseBranch)}}
            fi
            {{(resumePublishedBranch
                ? $"git fetch --prune origin {Quote($"refs/heads/{branchName}:refs/remotes/origin/{branchName}")}\ngit checkout -B {Quote(branchName)} {Quote($"origin/{branchName}")}"
                : $"""
                  if git show-ref --verify --quiet {Quote($"refs/heads/{branchName}")}; then
                    git checkout {Quote(branchName)}
                  else
                    git checkout -b {Quote(branchName)} {Quote($"origin/{baseBranch}")}
                  fi
                  """)}}
            {{(expectedCommitSha is null
                ? string.Empty
                : $"test \"$(git rev-parse HEAD)\" = {Quote(expectedCommitSha)}")}}
            commit=$(git rev-parse HEAD)
            bytes=$(du -sb . | cut -f1)
            files=$(find . -xdev -type f | wc -l)
            python3 - "$PWD" <<'PY'
            import os, pathlib, sys
            root = pathlib.Path(sys.argv[1]).resolve()
            for path in root.rglob("*"):
                if path.is_symlink():
                    target = (path.parent / os.readlink(path)).resolve()
                    if root not in target.parents and target != root:
                        raise SystemExit(f"symlink escapes workspace: {path.relative_to(root)}")
            PY
            printf 'CSWEET_BYTES=%s\nCSWEET_FILES=%s\nCSWEET_COMMIT=%s\n' "$bytes" "$files" "$commit"
            """;
    }

    private static string BuildPublishScript(
        GitRepositoryConnection connection,
        GitTicketWorkspace workspace,
        string commitMessage,
        GitAuthentication authentication)
    {
        var auth = authentication.Mode switch
        {
            "https" => HttpsAuthenticationPrefix(),
            "ssh" => SshAuthenticationPrefix(connection),
            _ => string.Empty
        };
        return $"""
            set -euo pipefail
            {auth}
            cd {Quote(workspace.WorkspacePath)}
            test "$(git branch --show-current)" = {Quote(workspace.BranchName)}
            git config user.name 'C-Sweet Software Developer'
            git config user.email 'software-developer@agents.csweet.local'
            git add -A
            if ! git diff --cached --quiet; then
              git commit -m {Quote(commitMessage)}
            fi
            commit=$(git rev-parse HEAD)
            git push origin {Quote($"HEAD:refs/heads/{workspace.BranchName}")}
            printf 'CSWEET_COMMIT=%s\n' "$commit"
            """;
    }

    private static string AuthenticationPrefix(
        GitRepositoryConnection connection,
        GitAuthentication authentication) =>
        authentication.Mode switch
        {
            "https" => HttpsAuthenticationPrefix(),
            "ssh" => SshAuthenticationPrefix(connection),
            _ => "export GIT_TERMINAL_PROMPT=0"
        };

    private static string HttpsAuthenticationPrefix() => """
        authdir=$(mktemp -d)
        trap 'rm -rf "$authdir"' EXIT
        chmod 700 "$authdir"
        cat > "$authdir/token"
        chmod 600 "$authdir/token"
        cat > "$authdir/askpass" <<'SH'
        #!/bin/sh
        case "$1" in
          *Username*) printf '%s\n' 'x-access-token' ;;
          *) cat "$CSWEET_GIT_TOKEN_FILE" ;;
        esac
        SH
        chmod 700 "$authdir/askpass"
        export CSWEET_GIT_TOKEN_FILE="$authdir/token"
        export GIT_ASKPASS="$authdir/askpass"
        export GIT_TERMINAL_PROMPT=0
        """;

    private static string SshAuthenticationPrefix(GitRepositoryConnection connection)
    {
        var uri = new Uri(connection.CloneUrl);
        var port = uri.IsDefaultPort ? 22 : uri.Port;
        var fingerprints = JsonSerializer.Deserialize<IReadOnlyList<string>>(
            connection.SshHostFingerprintsJson, JsonOptions) ?? [];
        if (fingerprints.Count == 0)
            throw new InvalidOperationException(
                "SSH repository connections require known-host fingerprints.");
        var comparisons = string.Join(
            "\n",
            fingerprints.Select(x =>
                $"  [ \"$actual\" = {Quote(x)} ] && matched=1"));
        return $$"""
        authdir=$(mktemp -d)
        trap 'rm -rf "$authdir"' EXIT
        chmod 700 "$authdir"
        IFS= read -r passphrase_b64
        cat > "$authdir/id"
        chmod 600 "$authdir/id"
        if [ -n "$passphrase_b64" ]; then
          printf '%s' "$passphrase_b64" | base64 -d > "$authdir/passphrase"
          chmod 600 "$authdir/passphrase"
          cat > "$authdir/askpass" <<'SH'
        #!/bin/sh
        cat "$CSWEET_SSH_PASSPHRASE_FILE"
        SH
          chmod 700 "$authdir/askpass"
          export CSWEET_SSH_PASSPHRASE_FILE="$authdir/passphrase"
          export SSH_ASKPASS="$authdir/askpass"
          export SSH_ASKPASS_REQUIRE=force
          export DISPLAY=csweet:0
        fi
        ssh-keyscan -p {{port}} {{Quote(uri.Host)}} > "$authdir/known_hosts" 2>/dev/null
        matched=0
        while read -r actual; do
        {{comparisons}}
        done < <(ssh-keygen -lf "$authdir/known_hosts" -E sha256 | awk '{print $2}')
        [ "$matched" = 1 ] || { echo 'SSH host fingerprint verification failed.' >&2; exit 71; }
        export GIT_SSH_COMMAND="ssh -i $authdir/id -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes -o UserKnownHostsFile=$authdir/known_hosts"
        """;
    }

    private async Task<DockerCommandResult> ExecuteInRuntimeAsync(
        string containerId,
        string script,
        string? standardInput,
        IReadOnlyList<string> knownSecrets,
        CancellationToken cancellationToken)
    {
        var result = await docker.ExecuteAsync(
            ["exec", "-i", "--workdir", "/workspace", containerId, "/bin/bash", "-c", script],
            cancellationToken,
            standardInput);
        return result with
        {
            StandardOutput = Redact(result.StandardOutput, knownSecrets),
            StandardError = Redact(result.StandardError, knownSecrets)
        };
    }

    private static CommandMetadata ParseCommandMetadata(string output)
    {
        long bytes = 0;
        var files = 0;
        string? commitSha = null;
        foreach (var line in output.Split(
                     '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("CSWEET_BYTES=", StringComparison.Ordinal))
                long.TryParse(line["CSWEET_BYTES=".Length..], out bytes);
            else if (line.StartsWith("CSWEET_FILES=", StringComparison.Ordinal))
                int.TryParse(line["CSWEET_FILES=".Length..], out files);
            else if (line.StartsWith("CSWEET_COMMIT=", StringComparison.Ordinal))
                commitSha = line["CSWEET_COMMIT=".Length..];
        }
        return new CommandMetadata(bytes, files, commitSha);
    }

    private static string Redact(string value, IReadOnlyList<string> secrets)
    {
        foreach (var secret in secrets.Where(x => !string.IsNullOrEmpty(x)))
            value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        return value;
    }

    private static IReadOnlyList<string> Between(string value, string start, string end)
    {
        var lines = value.Split(
            '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var collecting = false;
        var result = new List<string>();
        foreach (var line in lines)
        {
            if (line == start) { collecting = true; continue; }
            if (line == end) break;
            if (collecting) result.Add(line);
        }
        return result;
    }

    private static string DeterministicBranch(Guid workItemId, string title)
    {
        var slug = new string(title.ToLowerInvariant()
            .Select(x => char.IsAsciiLetterOrDigit(x) ? x : '-')
            .ToArray());
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        slug = slug.Trim('-');
        if (slug.Length > 48) slug = slug[..48].TrimEnd('-');
        if (slug.Length == 0) slug = "work";
        return $"csweet/{workItemId:N}-{slug}";
    }

    private static void ValidateWorkspacePath(
        string path,
        Guid workItemId,
        long assignmentRevision)
    {
        var expected = $"/workspace/{workItemId:N}/{assignmentRevision}";
        if (!string.Equals(path, expected, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The workspace path is outside the assignment root.");
    }

    private static string ValidateGitReference(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.StartsWith('-') ||
            value.Contains("..", StringComparison.Ordinal) ||
            value.Any(char.IsWhiteSpace) ||
            value.Any(x => x is '~' or '^' or ':' or '?' or '*' or '[' or '\\'))
            throw new ArgumentException("The Git reference is invalid.");
        return value;
    }

    private static string ValidateCommitSha(string value)
    {
        var result = value.Trim().ToLowerInvariant();
        if (result.Length != 40 || result.Any(x => !char.IsAsciiHexDigit(x)))
            throw new ArgumentException("The expected commit SHA must be a full 40-character SHA.");
        return result;
    }

    private static void ValidateIdempotencyKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 160)
            throw new ArgumentException("A bounded idempotency key is required.");
    }

    private static string Quote(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private static string Base64Url(string value) =>
        Base64Url(Encoding.UTF8.GetBytes(value));

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Bounded(string value) =>
        value.Length <= 2000 ? value.Trim() : value[..2000].Trim();

    private static T Read<T>(RequestCapability request) =>
        request.Payload.ToElement().Deserialize<T>(JsonOptions)
        ?? throw new JsonException("Capability payload was empty.");

    private static CapabilityResult Success(string requestId, object value) => new()
    {
        RequestId = requestId,
        Succeeded = true,
        Payload = JsonPayload.From(value, JsonOptions)
    };

    private static CapabilityResult Failure(
        string requestId,
        PlatformCapabilityErrorCode code,
        string error) => new()
    {
        RequestId = requestId,
        Succeeded = false,
        Error = error,
        Payload = JsonPayload.From(new { code = code.ToString(), error }, JsonOptions)
    };

    private static GitWorkspacePublication ToPublication(GitTicketWorkspace workspace) =>
        new(
            workspace.Id,
            workspace.BranchName,
            workspace.CommitSha!,
            true,
            Uri.TryCreate(workspace.PullRequestUrl, UriKind.Absolute, out var url) ? url : null,
            workspace.Status.ToString())
        {
            MergeStatus = workspace.MergeStatus,
            MergeCommitSha = workspace.MergeCommitSha,
            MergedAt = workspace.MergedAt
        };

    private sealed record WorkspaceContext(
        WorkTask Item,
        GitRepositoryConnection Connection,
        string ContainerId);

    private sealed record GitAuthentication(
        string Mode,
        string? StandardInput,
        string? ApiToken,
        IReadOnlyList<string> KnownSecrets);

    private sealed record CommandMetadata(long Bytes, int Files, string? CommitSha);
}
