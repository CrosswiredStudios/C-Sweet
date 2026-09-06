using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CSweet.TrustedServices;

public sealed class GitHubAppOptions
{
    public const string SectionName = "GitHubApp";
    public long AppId { get; set; }
    public string PrivateKeyBase64 { get; set; } = string.Empty;
}

public static class GitHubAppServiceExtensions
{
    public static IServiceCollection AddGitHubAppClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<GitHubAppOptions>()
            .Bind(configuration.GetSection(GitHubAppOptions.SectionName));
        services.AddSingleton<GitHubAppCredentialProvider>();
        services.AddHttpClient<GitHubAppClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.github.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CSweet-TrustedHost/2.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        return services;
    }

    internal static bool TryDecodePrivateKey(string value, out string pem)
    {
        try
        {
            pem = Encoding.UTF8.GetString(Convert.FromBase64String(value));
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return rsa.KeySize >= 2048;
        }
        catch (Exception exception) when (
            exception is FormatException or CryptographicException or ArgumentException)
        {
            pem = string.Empty;
            return false;
        }
    }

    internal static string DecodePrivateKey(string value)
    {
        TryDecodePrivateKey(value, out var pem);
        return pem;
    }
}

public sealed partial class GitHubAppClient(
    HttpClient http,
    GitHubAppCredentialProvider credentials,
    TimeProvider timeProvider)
{
    public async Task<GitHubAppIdentity> ValidateCredentialAsync(
        long appId,
        string privateKeyBase64,
        CancellationToken cancellationToken)
    {
        var credential = GitHubAppCredentialProvider.Validate(appId, privateKeyBase64, 0);
        using var request = CreateAppRequest(HttpMethod.Get, "app", credential);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<AppPayload>(cancellationToken)
            ?? throw new InvalidOperationException("GitHub returned an empty App response.");
        if (payload.Id != appId)
            throw new InvalidOperationException("GitHub returned a different App identity.");
        return new GitHubAppIdentity(payload.Id, payload.Slug, payload.Name);
    }

    public async Task<GitHubInstallationDescriptor> DescribeInstallationAsync(
        long installationId,
        CancellationToken cancellationToken)
    {
        using var request = CreateAppRequest(HttpMethod.Get, $"app/installations/{installationId}");
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<InstallationPayload>(cancellationToken)
            ?? throw new InvalidOperationException("GitHub returned an empty installation response.");
        return new GitHubInstallationDescriptor(
            payload.Id,
            payload.Account.Id,
            payload.Account.Login,
            payload.Account.Type,
            payload.SuspendedAt is not null,
            payload.SuspendedBy is null ? null : $"Suspended by {payload.SuspendedBy.Login}.");
    }

    public async Task<IReadOnlyList<GitHubRepositoryDescriptor>> ListInstallationRepositoriesAsync(
        long installationId,
        CancellationToken cancellationToken)
    {
        var token = await CreateInstallationTokenAsync(installationId, cancellationToken);
        var result = new List<GitHubRepositoryDescriptor>();
        for (var page = 1; page <= 10; page++)
        {
            using var request = CreateInstallationRequest(
                HttpMethod.Get,
                $"installation/repositories?per_page=100&page={page}",
                token);
            using var response = await http.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            var payload = await response.Content.ReadFromJsonAsync<RepositoriesPayload>(cancellationToken)
                ?? throw new InvalidOperationException("GitHub returned an empty repository-list response.");
            result.AddRange(payload.Repositories.Select(repository => new GitHubRepositoryDescriptor(
                repository.Id,
                repository.Owner.Login,
                repository.Name,
                repository.FullName,
                repository.CloneUrl,
                repository.DefaultBranch,
                repository.Private,
                repository.Archived,
                repository.IsTemplate)));
            if (payload.Repositories.Count < 100 || result.Count >= payload.TotalCount)
                break;
        }
        return result;
    }

    public async Task<GitHubMergeResult> MergePullRequestAsync(
        GitHubMergeRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRepositoryCoordinates(request.Owner, request.Repository);
        if (request.PullRequestNumber <= 0 || !IsSha(request.ExpectedHeadSha))
            throw new ArgumentException("A valid pull request number and full commit SHA are required.");
        var token = await CreateInstallationTokenAsync(request.InstallationId, cancellationToken);
        using var headRequest = CreateInstallationRequest(
            HttpMethod.Get,
            $"repos/{Escape(request.Owner)}/{Escape(request.Repository)}/pulls/{request.PullRequestNumber}",
            token);
        using var headResponse = await http.SendAsync(headRequest, cancellationToken);
        await EnsureSuccessAsync(headResponse, cancellationToken);
        var pullRequest = await headResponse.Content.ReadFromJsonAsync<PullRequestPayload>(cancellationToken)
            ?? throw new InvalidOperationException("GitHub returned an empty pull request response.");
        if (!string.Equals(pullRequest.Head.Sha, request.ExpectedHeadSha, StringComparison.OrdinalIgnoreCase))
            return new GitHubMergeResult(false, false, null, "head_changed", "The proposed-change head no longer matches the authorized SHA.");

        using var mergeRequest = CreateInstallationRequest(
            HttpMethod.Put,
            $"repos/{Escape(request.Owner)}/{Escape(request.Repository)}/pulls/{request.PullRequestNumber}/merge",
            token);
        mergeRequest.Content = JsonContent.Create(new
        {
            sha = request.ExpectedHeadSha,
            merge_method = "squash"
        });
        using var mergeResponse = await http.SendAsync(mergeRequest, cancellationToken);
        var result = await mergeResponse.Content.ReadFromJsonAsync<MergePayload>(cancellationToken);
        if (mergeResponse.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
            return new GitHubMergeResult(false, false, null, "head_changed", result?.Message ?? "GitHub rejected the exact-SHA merge.");
        await EnsureSuccessAsync(mergeResponse, cancellationToken);
        return result?.Merged == true
            ? new GitHubMergeResult(true, true, result.Sha)
            : new GitHubMergeResult(false, true, null, "merge_rejected", result?.Message ?? "GitHub did not merge the proposed change.");
    }

    public async Task<GitHubProvisionRepositoryResult> ProvisionPrivateRepositoryAsync(
        GitHubProvisionRepositoryRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRepositoryCoordinates(request.OrganizationLogin, request.RepositoryName);
        ValidateRepositoryCoordinates(request.TemplateOwner, request.TemplateRepository);
        if (request.Description.Length > 350 || !IsBranch(request.RequiredDefaultBranch))
            throw new ArgumentException("The description or required default branch is invalid.");
        var installation = await DescribeInstallationAsync(request.InstallationId, cancellationToken);
        if (installation.Suspended ||
            (!string.Equals(installation.AccountType, "Organization", StringComparison.OrdinalIgnoreCase) && !string.Equals(installation.AccountType, "User", StringComparison.OrdinalIgnoreCase)) ||
            !string.Equals(installation.AccountLogin, request.OrganizationLogin, StringComparison.OrdinalIgnoreCase))
        {
            return new GitHubProvisionRepositoryResult(false, false, null, null, null, null,
                "installation_not_eligible", "Managed repositories require an active matching organization or personal-account installation.");
        }

        var token = await CreateInstallationTokenAsync(request.InstallationId, cancellationToken);
        using var createRequest = CreateInstallationRequest(
            HttpMethod.Post,
            $"repos/{Escape(request.TemplateOwner)}/{Escape(request.TemplateRepository)}/generate",
            token);
        createRequest.Content = JsonContent.Create(new
        {
            owner = request.OrganizationLogin,
            name = request.RepositoryName,
            description = request.Description,
            @private = true,
            include_all_branches = false
        });
        using var createResponse = await http.SendAsync(createRequest, cancellationToken);
        if (createResponse.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
        {
            var failure = await ReadErrorAsync(createResponse, cancellationToken);
            return new GitHubProvisionRepositoryResult(false, false, null, null, null, null,
                "repository_creation_rejected", failure);
        }
        await EnsureSuccessAsync(createResponse, cancellationToken);
        var created = await createResponse.Content.ReadFromJsonAsync<RepositoryPayload>(cancellationToken)
            ?? throw new InvalidOperationException("GitHub returned an empty repository response.");
        if (created.Id <= 0 || created.Owner.Id != installation.AccountId ||
            !string.Equals(created.Owner.Login, request.OrganizationLogin, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(created.Name, request.RepositoryName, StringComparison.OrdinalIgnoreCase) || created.Archived)
            return Quarantined(created, "repository_identity_not_confirmed", "The provider did not confirm the requested active repository identity.");
        if (!created.Private)
            return Quarantined(created, "private_visibility_not_confirmed", "The provider did not confirm private visibility.");

        if (!string.Equals(created.DefaultBranch, request.RequiredDefaultBranch, StringComparison.Ordinal))
        {
            using var branchRequest = CreateInstallationRequest(
                HttpMethod.Patch,
                $"repos/{Escape(created.Owner.Login)}/{Escape(created.Name)}",
                token);
            branchRequest.Content = JsonContent.Create(new { default_branch = request.RequiredDefaultBranch });
            using var branchResponse = await http.SendAsync(branchRequest, cancellationToken);
            if (!branchResponse.IsSuccessStatusCode)
                return Quarantined(created, "default_branch_configuration_failed", await ReadErrorAsync(branchResponse, cancellationToken));
            created = created with { DefaultBranch = request.RequiredDefaultBranch };
        }

        using var protectionRequest = CreateInstallationRequest(
            HttpMethod.Put,
            $"repos/{Escape(created.Owner.Login)}/{Escape(created.Name)}/branches/{Escape(created.DefaultBranch)}/protection",
            token);
        protectionRequest.Content = JsonContent.Create(new
        {
            required_status_checks = (object?)null,
            enforce_admins = true,
            required_pull_request_reviews = new
            {
                dismiss_stale_reviews = true,
                require_code_owner_reviews = false,
                required_approving_review_count = 1,
                require_last_push_approval = true
            },
            restrictions = (object?)null,
            allow_force_pushes = false,
            allow_deletions = false,
            block_creations = false,
            required_conversation_resolution = true,
            lock_branch = false,
            allow_fork_syncing = false
        });
        using var protectionResponse = await http.SendAsync(protectionRequest, cancellationToken);
        if (!protectionResponse.IsSuccessStatusCode)
            return Quarantined(created, "branch_protection_failed", await ReadErrorAsync(protectionResponse, cancellationToken));
        return new GitHubProvisionRepositoryResult(
            true, false, created.Id, created.Owner.Login, created.Name, created.DefaultBranch);
    }

    private GitHubProvisionRepositoryResult Quarantined(
        RepositoryPayload repository,
        string code,
        string message) => new(
        true, true, repository.Id, repository.Owner.Login, repository.Name,
        repository.DefaultBranch, code, message);

    internal async Task<string> CreateInstallationTokenAsync(
        long installationId,
        CancellationToken cancellationToken)
    {
        using var request = CreateAppRequest(HttpMethod.Post, $"app/installations/{installationId}/access_tokens");
        request.Content = JsonContent.Create(new { });
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<TokenPayload>(cancellationToken)
            ?? throw new InvalidOperationException("GitHub returned an empty installation-token response.");
        return payload.Token;
    }

    private HttpRequestMessage CreateAppRequest(
        HttpMethod method,
        string uri,
        GitHubAppCredentialSnapshot? credential = null)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", CreateAppJwt(credential ?? credentials.GetRequired()));
        return request;
    }

    private static HttpRequestMessage CreateInstallationRequest(
        HttpMethod method,
        string uri,
        string token)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private string CreateAppJwt(GitHubAppCredentialSnapshot credential)
    {
        var now = timeProvider.GetUtcNow();
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iat = now.AddSeconds(-60).ToUnixTimeSeconds(),
            exp = now.AddMinutes(9).ToUnixTimeSeconds(),
            iss = credential.AppId.ToString()
        }));
        var unsigned = $"{header}.{payload}";
        using var rsa = RSA.Create();
        rsa.ImportFromPem(GitHubAppServiceExtensions.DecodePrivateKey(credential.PrivateKeyBase64));
        var signature = rsa.SignData(
            Encoding.ASCII.GetBytes(unsigned),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return $"{unsigned}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        var message = await ReadErrorAsync(response, cancellationToken);
        throw new InvalidOperationException($"GitHub request failed ({(int)response.StatusCode}): {message}");
    }

    private static async Task<string> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<ErrorPayload>(cancellationToken);
            return payload?.Message ?? "The provider rejected the request.";
        }
        catch (JsonException)
        {
            return "The provider rejected the request.";
        }
    }

    private static void ValidateRepositoryCoordinates(string owner, string repository)
    {
        if (!IsCoordinate(owner) || !IsCoordinate(repository))
            throw new ArgumentException("Repository owner and name must use bounded GitHub coordinates.");
    }

    private static bool IsCoordinate(string value) =>
        value.Length is >= 1 and <= 100 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsBranch(string value) =>
        value.Length is >= 1 and <= 200 &&
        !value.Contains("..", StringComparison.Ordinal) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '/');

    private static bool IsSha(string value) =>
        value.Length == 40 && value.All(Uri.IsHexDigit);

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private sealed record InstallationPayload(
        long Id,
        AccountPayload Account,
        [property: System.Text.Json.Serialization.JsonPropertyName("suspended_at")]
        DateTimeOffset? SuspendedAt,
        [property: System.Text.Json.Serialization.JsonPropertyName("suspended_by")]
        AccountPayload? SuspendedBy);
    private sealed record AccountPayload(long Id, string Login, string Type);
    private sealed record TokenPayload(string Token);
    private sealed record PullRequestPayload(HeadPayload Head);
    private sealed record HeadPayload(string Sha);
    private sealed record MergePayload(string? Sha, bool Merged, string? Message);
    private sealed record RepositoryPayload(
        long Id,
        string Name,
        bool Private,
        [property: System.Text.Json.Serialization.JsonPropertyName("default_branch")]
        string DefaultBranch,
        AccountPayload Owner,
        bool Archived = false);
    private sealed record RepositoriesPayload(
        [property: System.Text.Json.Serialization.JsonPropertyName("total_count")]
        int TotalCount,
        IReadOnlyList<RepositoryListItemPayload> Repositories);
    private sealed record RepositoryListItemPayload(
        long Id,
        string Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("full_name")]
        string FullName,
        [property: System.Text.Json.Serialization.JsonPropertyName("clone_url")]
        string CloneUrl,
        [property: System.Text.Json.Serialization.JsonPropertyName("default_branch")]
        string DefaultBranch,
        bool Private,
        bool Archived,
        [property: System.Text.Json.Serialization.JsonPropertyName("is_template")]
        bool IsTemplate,
        AccountPayload Owner);
    private sealed record ErrorPayload(string? Message);
    private sealed record AppPayload(long Id, string Slug, string Name);
}

public sealed record GitHubAppIdentity(long AppId, string AppSlug, string AppName);

public sealed record GitHubAppCredentialSnapshot(
    long AppId,
    string PrivateKeyBase64,
    long Revision,
    string? AppSlug = null,
    string? AppName = null);

public sealed class GitHubAppCredentialProvider
{
    private readonly object _gate = new();
    private GitHubAppCredentialSnapshot? _current;

    public GitHubAppCredentialProvider(IOptions<GitHubAppOptions> options)
    {
        var configured = options.Value;
        if (configured.AppId > 0 && !string.IsNullOrWhiteSpace(configured.PrivateKeyBase64))
            _current = Validate(configured.AppId, configured.PrivateKeyBase64, 0);
    }

    public GitHubAppCredentialSnapshot? Current
    {
        get { lock (_gate) return _current; }
    }

    public GitHubAppCredentialSnapshot GetRequired() => Current ?? throw new InvalidOperationException(
        "The GitHub App has not been configured by an enterprise administrator.");

    public GitHubAppCredentialSnapshot Activate(
        long appId,
        string privateKeyBase64,
        long revision,
        string appSlug,
        string appName)
    {
        var next = Validate(appId, privateKeyBase64, revision) with
        {
            AppSlug = appSlug,
            AppName = appName
        };
        lock (_gate)
        {
            if (_current is not null && revision < _current.Revision)
                throw new InvalidOperationException("An older GitHub App credential revision cannot replace the active revision.");
            _current = next;
            return next;
        }
    }

    internal static GitHubAppCredentialSnapshot Validate(long appId, string privateKeyBase64, long revision)
    {
        if (appId <= 0 || revision < 0 ||
            !GitHubAppServiceExtensions.TryDecodePrivateKey(privateKeyBase64, out _))
            throw new ArgumentException("A valid GitHub App ID and RSA private key are required.");
        return new GitHubAppCredentialSnapshot(appId, privateKeyBase64, revision);
    }
}
