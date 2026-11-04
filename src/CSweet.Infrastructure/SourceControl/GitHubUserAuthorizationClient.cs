using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CSweet.Application.SourceControl;

namespace CSweet.Infrastructure.SourceControl;

public sealed class GitHubUserAuthorizationClient(HttpClient http) : IGitHubUserAuthorizationClient
{
    public async Task<GitHubAuthorizedInstallation> VerifyInstallationAsync(
        PlatformGitHubUserAuthorizationConfiguration configuration,
        string code,
        long installationId,
        CancellationToken cancellationToken = default)
    {
        if (installationId <= 0) throw new UnauthorizedAccessException("GitHub returned an invalid installation.");
        var installations = await ListAuthorizedInstallationsAsync(configuration, code, cancellationToken);
        return installations.SingleOrDefault(x => x.InstallationId == installationId)
            ?? throw new UnauthorizedAccessException("The signed-in GitHub account cannot access the selected C-Sweet installation.");
    }

    public async Task<IReadOnlyList<GitHubAuthorizedInstallation>> ListAuthorizedInstallationsAsync(
        PlatformGitHubUserAuthorizationConfiguration configuration, string code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 512)
            throw new UnauthorizedAccessException("GitHub returned an invalid authorization response.");

        using var exchangeRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "https://github.com/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = configuration.ClientId,
                ["client_secret"] = configuration.ClientSecret,
                ["code"] = code
            })
        };
        exchangeRequest.Headers.Accept.Clear();
        exchangeRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var exchange = await http.SendAsync(exchangeRequest, cancellationToken);
        if (!exchange.IsSuccessStatusCode)
            throw new UnauthorizedAccessException("GitHub could not verify the signed-in account.");
        var token = await exchange.Content.ReadFromJsonAsync<TokenPayload>(cancellationToken)
            ?? throw new UnauthorizedAccessException("GitHub returned an empty authorization response.");
        if (string.IsNullOrWhiteSpace(token.AccessToken) || !string.IsNullOrWhiteSpace(token.Error))
            throw new UnauthorizedAccessException("GitHub did not authorize this connection.");

        using var userRequest = CreateAuthorizedRequest(HttpMethod.Get, "https://api.github.com/user", token.AccessToken);
        using var userResponse = await http.SendAsync(userRequest, cancellationToken);
        if (!userResponse.IsSuccessStatusCode)
            throw new UnauthorizedAccessException("C-Sweet could not verify the GitHub account identity.");
        var user = await userResponse.Content.ReadFromJsonAsync<UserPayload>(cancellationToken)
            ?? throw new UnauthorizedAccessException("GitHub returned an empty account identity.");
        if (user.Id <= 0 || string.IsNullOrWhiteSpace(user.Login))
            throw new UnauthorizedAccessException("GitHub returned an invalid account identity.");

        var authorized = new List<GitHubAuthorizedInstallation>();
        for (var page = 1; page <= 10; page++)
        {
            using var installationsRequest = CreateAuthorizedRequest(
                HttpMethod.Get,
                $"https://api.github.com/user/installations?per_page=100&page={page}",
                token.AccessToken);
            using var installationsResponse = await http.SendAsync(installationsRequest, cancellationToken);
            if (!installationsResponse.IsSuccessStatusCode)
                throw new UnauthorizedAccessException("C-Sweet could not verify access to the GitHub installation.");
            var installations = await installationsResponse.Content.ReadFromJsonAsync<InstallationsPayload>(cancellationToken)
                ?? throw new UnauthorizedAccessException("GitHub returned an empty installation list.");
            authorized.AddRange(installations.Installations.Where(x => x.Id > 0).Select(x => new GitHubAuthorizedInstallation(x.Id, user.Id, user.Login)));
            if (installations.Installations.Count < 100)
                break;
        }
        return authorized.DistinctBy(x => x.InstallationId).ToArray();
    }

    private static HttpRequestMessage CreateAuthorizedRequest(
        HttpMethod method,
        string url,
        string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private sealed record TokenPayload(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        string? Error);
    private sealed record UserPayload(long Id, string Login);
    private sealed record InstallationPayload(long Id);
    private sealed record InstallationsPayload(
        [property: JsonPropertyName("installations")] IReadOnlyList<InstallationPayload> Installations);
}
