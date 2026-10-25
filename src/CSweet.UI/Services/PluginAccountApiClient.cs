using System.Net.Http.Json;
using CSweet.Contracts.Plugins;

namespace CSweet.UI.Services;

public interface IPluginAccountApiClient
{
    Task<PluginAccountManagementView> GetAsync(Guid installationId, CancellationToken ct = default);
    Task<BeginPluginAccountResponse> ConnectAsync(Guid installationId, ConnectPluginAccountRequest request, CancellationToken ct = default);
    Task RetryAsync(Guid installationId, Guid setupId, CancellationToken ct = default);
    Task SaveGrantAsync(Guid installationId, Guid accountId, SavePluginAccountGrantRequest request, CancellationToken ct = default);
    Task RevokeGrantAsync(Guid installationId, Guid grantId, long revision, CancellationToken ct = default);
    Task<PluginAccountImpactView> GetDisconnectImpactAsync(Guid installationId, Guid accountId, CancellationToken ct = default);
    Task DisconnectAsync(Guid installationId, Guid accountId, DisconnectPluginAccountRequest request, CancellationToken ct = default);
}
public sealed class PluginAccountApiClient(HttpClient http) : IPluginAccountApiClient
{
    private static string Path(Guid id) => $"api/plugins/installations/{id:D}/account-management";
    public Task<PluginAccountManagementView> GetAsync(Guid id, CancellationToken ct = default) => Send<PluginAccountManagementView>(HttpMethod.Get, Path(id) + "/", null, ct);
    public Task<BeginPluginAccountResponse> ConnectAsync(Guid id, ConnectPluginAccountRequest request, CancellationToken ct = default) => Send<BeginPluginAccountResponse>(HttpMethod.Post, Path(id) + "/connect", request, ct);
    public async Task RetryAsync(Guid id, Guid setupId, CancellationToken ct = default) => await Send<object>(HttpMethod.Post, Path(id) + $"/setup/{setupId:D}/retry", null, ct);
    public async Task SaveGrantAsync(Guid id, Guid accountId, SavePluginAccountGrantRequest request, CancellationToken ct = default) => await Send<object>(HttpMethod.Put, Path(id) + $"/{accountId:D}/business-access", request, ct);
    public async Task RevokeGrantAsync(Guid id, Guid grantId, long revision, CancellationToken ct = default) => await Send<object>(HttpMethod.Delete, Path(id) + $"/business-access/{grantId:D}?revision={revision}", null, ct);
    public Task<PluginAccountImpactView> GetDisconnectImpactAsync(Guid id, Guid accountId, CancellationToken ct = default) => Send<PluginAccountImpactView>(HttpMethod.Get, Path(id) + $"/{accountId:D}/disconnect-impact", null, ct);
    public async Task DisconnectAsync(Guid id, Guid accountId, DisconnectPluginAccountRequest request, CancellationToken ct = default) => await Send<object>(HttpMethod.Post, Path(id) + $"/{accountId:D}/disconnect", request, ct);
    private async Task<T> Send<T>(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
                throw new ApiClientException(response.StatusCode, "Sign in with an account authorized to manage this connection, or ask an enterprise administrator for access.");
            Error? error = null;
            try { error = await response.Content.ReadFromJsonAsync<Error>(ct); }
            catch (System.Text.Json.JsonException) { /* Never render an HTML gateway error or raw server response. */ }
            throw new ApiClientException(response.StatusCode, error?.ErrorMessage ?? "Account management is unavailable. Please refresh and try again.");
        }
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return default!;
        return await response.Content.ReadFromJsonAsync<T>(ct) ?? throw new InvalidOperationException("The account response was empty.");
    }
    private sealed record Error([property: System.Text.Json.Serialization.JsonPropertyName("error")] string ErrorMessage);
}
