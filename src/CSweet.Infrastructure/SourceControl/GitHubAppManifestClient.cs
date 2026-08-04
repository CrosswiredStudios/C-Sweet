using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CSweet.Application.SourceControl;

namespace CSweet.Infrastructure.SourceControl;

public sealed class GitHubAppManifestClient(HttpClient http) : IPlatformGitHubManifestClient
{
    public async Task<PlatformGitHubManifestConversion> ConvertAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 256)
            throw new ArgumentException("GitHub returned an invalid manifest code.");
        using var response = await http.PostAsJsonAsync(
            $"app-manifests/{Uri.EscapeDataString(code)}/conversions",
            new { }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<ConversionPayload>(cancellationToken)
            ?? throw new InvalidOperationException("GitHub returned an empty App manifest conversion.");
        if (payload.Id <= 0 || string.IsNullOrWhiteSpace(payload.Pem) ||
            string.IsNullOrWhiteSpace(payload.Slug) || string.IsNullOrWhiteSpace(payload.Name))
            throw new InvalidOperationException("GitHub returned an incomplete App manifest conversion.");
        return new PlatformGitHubManifestConversion(
            payload.Id, payload.Name, payload.Slug, payload.Pem);
    }

    private sealed record ConversionPayload(
        long Id,
        string Name,
        string Slug,
        string Pem);
}
