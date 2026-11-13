using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CSweet.Contracts.SourceControl;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Setup;

/// <summary>
/// The token C-Sweet presents to GitHub when it reads the catalog. A token saved in settings wins over
/// host configuration. The value is cached briefly so every process picks up a change within a minute
/// without reading the database on each request.
/// </summary>
public interface IGitHubCatalogCredentialProvider
{
    ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken);
    void Invalidate();
}

public sealed class GitHubCatalogCredentialProvider(
    IServiceScopeFactory scopes,
    IDataProtectionProvider dataProtection,
    IOptions<GitHubAgentRepositoryOptions> options,
    TimeProvider clock) : IGitHubCatalogCredentialProvider
{
    internal const string ProtectorPurpose = "CSweet.GitHubCatalog.Token.v1";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);
    private readonly IDataProtector _protector = dataProtection.CreateProtector(ProtectorPurpose);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;

    public async ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (clock.GetUtcNow() - _readAt < CacheLifetime) return _token;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (clock.GetUtcNow() - _readAt < CacheLifetime) return _token;
            string? saved = null;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
                var value = await db.GitHubCatalogCredentials.AsNoTracking().OrderByDescending(x => x.ConfiguredAt)
                    .Select(x => x.ProtectedToken).FirstOrDefaultAsync(cancellationToken);
                if (!string.IsNullOrEmpty(value)) saved = _protector.Unprotect(value);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // An unreadable row (lost key ring, database briefly away) must not stop catalog reads.
                // Fall back to host configuration and try the database again after the cache expires.
            }
            var configured = options.Value.AccessToken;
            _token = !string.IsNullOrWhiteSpace(saved) ? saved : string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
            _readAt = clock.GetUtcNow();
            return _token;
        }
        finally { _gate.Release(); }
    }

    public void Invalidate() => _readAt = DateTimeOffset.MinValue;
}

/// <summary>Signs requests to the GitHub REST API with the catalog credential. No other host receives it.</summary>
public sealed class GitHubCatalogAuthenticationHandler(IGitHubCatalogCredentialProvider credentials) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is { Scheme: "https", Host: "api.github.com" } &&
            await credentials.GetTokenAsync(cancellationToken) is { } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }
}

public interface IGitHubCatalogAccessService
{
    Task<GitHubCatalogAccessStatus> GetStatusAsync(CancellationToken cancellationToken);
    Task<GitHubCatalogAccessStatus> SaveAsync(Guid userId, string token, CancellationToken cancellationToken);
    Task<GitHubCatalogAccessStatus> RemoveAsync(CancellationToken cancellationToken);
}

public sealed class GitHubCatalogAccessService(
    CSweetDbContext db,
    IHttpClientFactory http,
    IDataProtectionProvider dataProtection,
    IGitHubCatalogCredentialProvider credentials,
    IOptions<GitHubAgentRepositoryOptions> options,
    TimeProvider clock) : IGitHubCatalogAccessService
{
    public const string HttpClientName = "CSweet.GitHubCatalogAccess";
    private readonly IDataProtector _protector = dataProtection.CreateProtector(GitHubCatalogCredentialProvider.ProtectorPurpose);

    public async Task<GitHubCatalogAccessStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var saved = await db.GitHubCatalogCredentials.AsNoTracking().OrderByDescending(x => x.ConfiguredAt).FirstOrDefaultAsync(cancellationToken);
        var token = await credentials.GetTokenAsync(cancellationToken);
        var source = saved is not null ? "settings" : string.IsNullOrWhiteSpace(options.Value.AccessToken) ? "none" : "configuration";
        var (limit, remaining, resets, problem) = await ReadAllowanceAsync(token, cancellationToken);
        return new(source != "none", source, saved?.AccountLogin, saved?.AccountType, saved?.ConfiguredAt, saved?.TokenExpiresAt,
            limit, remaining, resets, problem);
    }

    public async Task<GitHubCatalogAccessStatus> SaveAsync(Guid userId, string token, CancellationToken cancellationToken)
    {
        token = token?.Trim() ?? string.Empty;
        if (token.Length is < 20 or > 512 || token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException("Enter a GitHub access token.");
        // Prove the token before storing it: GitHub names the account it belongs to.
        using var request = new HttpRequestMessage(HttpMethod.Get, "user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ArgumentException("GitHub did not accept this token. Check that it was copied in full and has not expired or been revoked.");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub could not verify the token (HTTP {(int)response.StatusCode}). Try again shortly.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var login = document.RootElement.TryGetProperty("login", out var loginElement) ? loginElement.GetString() : null;
        var type = document.RootElement.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(login) || login.Length > 100)
            throw new InvalidOperationException("GitHub did not name the account this token belongs to.");
        // GitHub writes this header as "2027-01-02 03:04:05 UTC", which the parser only accepts with a numeric offset.
        DateTimeOffset? expires = response.Headers.TryGetValues("github-authentication-token-expiration", out var values) &&
            DateTimeOffset.TryParse((values.FirstOrDefault() ?? string.Empty).Replace(" UTC", " +00:00", StringComparison.OrdinalIgnoreCase),
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime() : null;

        var now = clock.GetUtcNow();
        db.GitHubCatalogCredentials.RemoveRange(await db.GitHubCatalogCredentials.ToListAsync(cancellationToken));
        db.GitHubCatalogCredentials.Add(new GitHubCatalogCredential
        {
            Id = Guid.NewGuid(), ProtectedToken = _protector.Protect(token), AccountLogin = login,
            AccountType = type is { Length: > 0 and <= 32 } ? type : "User", ConfiguredByUserId = userId,
            ConfiguredAt = now, VerifiedAt = now, TokenExpiresAt = expires
        });
        await db.SaveChangesAsync(cancellationToken);
        credentials.Invalidate();
        return await GetStatusAsync(cancellationToken);
    }

    public async Task<GitHubCatalogAccessStatus> RemoveAsync(CancellationToken cancellationToken)
    {
        db.GitHubCatalogCredentials.RemoveRange(await db.GitHubCatalogCredentials.ToListAsync(cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
        credentials.Invalidate();
        return await GetStatusAsync(cancellationToken);
    }

    // GitHub does not count this request against the allowance it reports.
    private async Task<(int? Limit, int? Remaining, DateTimeOffset? Resets, string? Problem)> ReadAllowanceAsync(
        string? token, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "rate_limit");
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return (null, null, null, "GitHub no longer accepts the saved token. Replace it.");
            if (!response.IsSuccessStatusCode) return (null, null, null, $"GitHub did not report the allowance (HTTP {(int)response.StatusCode}).");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var core = document.RootElement.GetProperty("resources").GetProperty("core");
            return (core.GetProperty("limit").GetInt32(), core.GetProperty("remaining").GetInt32(),
                DateTimeOffset.FromUnixTimeSeconds(core.GetProperty("reset").GetInt64()), null);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            return (null, null, null, "GitHub could not be reached to read the allowance.");
        }
    }
}
